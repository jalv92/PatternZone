// PatternZoneCore.cs — pure C# detection logic for PatternZone (1m MNQ chart
// patterns gated by S/R zones). ZERO `using NinjaTrader.*`, own namespace
// `PatternZoneCore`, C# 7.3 only: NT8 ships its own types under common names,
// and a NinjaTrader using here risks a CS0101 duplicate-type clash against
// NT8-shipped sources at compile time (the TrapFlow gap in nt8c's memory).
// Purity keeps this exact file compilable both by the NT8 Custom assembly
// and by the net8 test runner (tests/PatternZone.Tests.csproj), which has no
// NinjaTrader assemblies on its reference path — behavior must match in both.

using System;
using System.Collections.Generic;

namespace PatternZoneCore
{
    public struct PzBar { public DateTime Time; public double Open, High, Low, Close; }

    public struct PzSwing { public int BarIndex; public DateTime Time; public double Price; public bool IsHigh; }

    public sealed class PzConfig
    {
        public int SwingStrength = 3;
        public double TopToleranceAtr = 0.30;
        public double HeadProminenceAtr = 0.30;
        public int MaxPatternBars = 60;
        public int NecklineBreakTicks = 2;
        public double TickSize = 0.25;
        public double MinPatternHeightAtr = 1.5;
        public double ZoneHalfWidthAtr = 0.50;
        public bool UsePriorDayHL = true, UseOvernightHL = true, UsePriorClose = true, UseDayOpen = true, UseRound100 = true, UseRound50 = false;
        public double StopBufferAtr = 0.50;
        public double TargetMultiple = 1.0;
        public bool EnableFlagAddon = true;
        public double PoleMinAtr = 2.0;
        public int PoleMaxBars = 8;
        public int FlagMinBars = 3, FlagMaxBars = 10;
        public double FlagRangeMaxAtr = 1.0;
        public double MinDistToTargetAtr = 1.5;
        public int MaxAdds = 1;
        public int MaxTradesPerSession = 3;
    }

    // House Wilder recursion (PullbackZoneStrategy.cs:1170-1180): seed = tr[0]
    // itself (running mean of one sample), TR uses the previous bar's close,
    // and it crosses sessions — never resets.
    public sealed class WilderAtr
    {
        private readonly int _period;
        private int _n;
        private double _value;
        private double _prevClose;

        public WilderAtr(int period)
        {
            _period = period;
        }

        public void Update(PzBar bar)
        {
            double tr = _n == 0
                ? bar.High - bar.Low
                : Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - _prevClose), Math.Abs(bar.Low - _prevClose)));
            _value = _n < _period ? (_value * _n + tr) / (_n + 1) : _value + (tr - _value) / _period;
            _prevClose = bar.Close;
            _n++;
        }

        public double Value { get { return _value; } }
        public bool IsReady { get { return Value > 0; } }
    }

    // House reveal rule (PullbackZoneStrategy.cs:459-471): the pivot sits
    // `strength` bars back and is confirmed once its window fills; strict-unique
    // max/min over the 2*strength+1 window (equality anywhere else in the
    // window rejects it). A bar can confirm a high AND a low at once.
    public sealed class SwingDetector
    {
        private readonly int _strength;
        private readonly int _windowSize;
        private readonly List<PzBar> _window = new List<PzBar>();

        public SwingDetector(int strength)
        {
            _strength = strength;
            _windowSize = 2 * strength + 1;
        }

        public List<PzSwing> Update(PzBar bar, int barIndex)
        {
            _window.Add(bar);
            if (_window.Count > _windowSize)
                _window.RemoveAt(0);

            var result = new List<PzSwing>();
            if (_window.Count < _windowSize)
                return result;

            PzBar candidate = _window[_strength];
            int candidateIndex = barIndex - _strength;
            double ph = candidate.High, pl = candidate.Low;
            bool hiMax = true, loMin = true;
            int hiEq = 0, loEq = 0;
            for (int i = 0; i < _window.Count; i++)
            {
                PzBar w = _window[i];
                if (w.High > ph) hiMax = false;
                else if (w.High == ph) hiEq++;
                if (w.Low < pl) loMin = false;
                else if (w.Low == pl) loEq++;
            }
            if (hiMax && hiEq == 1)
                result.Add(new PzSwing { BarIndex = candidateIndex, Time = candidate.Time, Price = ph, IsHigh = true });
            if (loMin && loEq == 1)
                result.Add(new PzSwing { BarIndex = candidateIndex, Time = candidate.Time, Price = pl, IsHigh = false });
            return result;
        }
    }

    public enum PatternKind { DoubleTop, DoubleBottom, TripleTop, TripleBottom, HeadShoulders, InverseHeadShoulders }

    public sealed class PatternCandidate
    {
        public PatternKind Kind;
        public bool IsShort;                 // top-family => short
        public PzSwing[] Swings;             // defining swings, oldest first (3 or 5)
        public double ExtremePrice;          // stop anchor: max top / head / min bottom
        public int ArmedBarIndex;            // bar the candidate was created on
        // Neckline as a 2-point line; equal points => horizontal.
        public PzSwing NeckP1, NeckP2;
        public double[] ZoneExtremes;        // prices that must touch a zone (2 tops, 3 tops, or head)

        public double NecklineAt(int barIndex)
        {
            if (NeckP2.BarIndex == NeckP1.BarIndex)
                return NeckP1.Price;
            double slope = (NeckP2.Price - NeckP1.Price) / (NeckP2.BarIndex - NeckP1.BarIndex);
            return NeckP1.Price + slope * (barIndex - NeckP1.BarIndex);
        }
    }

    public static class PatternScanner
    {
        // Each returns null or a candidate built from the TAIL of the alternating swing list.
        public static PatternCandidate TryDouble(IReadOnlyList<PzSwing> swings, PzConfig cfg, double atr)
        {
            if (swings.Count < 3)
                return null;

            PzSwing a = swings[swings.Count - 3];
            PzSwing b = swings[swings.Count - 2];
            PzSwing c = swings[swings.Count - 1];

            if (a.IsHigh != c.IsHigh || b.IsHigh == a.IsHigh)
                return null;
            if (Math.Abs(a.Price - c.Price) > cfg.TopToleranceAtr * atr)
                return null;

            bool isTop = a.IsHigh;
            return new PatternCandidate
            {
                Kind = isTop ? PatternKind.DoubleTop : PatternKind.DoubleBottom,
                IsShort = isTop,
                Swings = new[] { a, b, c },
                ExtremePrice = isTop ? Math.Max(a.Price, c.Price) : Math.Min(a.Price, c.Price),
                ArmedBarIndex = c.BarIndex,
                NeckP1 = b,
                NeckP2 = b,
                ZoneExtremes = new[] { a.Price, c.Price },
            };
        }
    }
}
