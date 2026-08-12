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
        // Amendment 2: a reversal pattern needs a prior trend to reverse.
        public bool UseTrendFilter = true;
        public int TrendLookbackBars = 60;
        public double TickSize = 0.25;
        public double MinPatternHeightAtr = 1.5;
        public double ZoneHalfWidthAtr = 0.50;
        // Amendment 1: permission reaches this much FURTHER than the drawn band.
        public double ZoneProximityAtr = 0.50;
        public bool UsePriorDayHL = true, UseOvernightHL = true, UsePriorClose = true, UseDayOpen = true, UseRound100 = true, UseRound50 = false;
        // Add-on aggregate stop only (flag far edge). The ENTRY stop is
        // StopOffsetTicks beyond the pattern's last defining swing — Amendment 1.
        public double StopBufferAtr = 0.50;
        public int StopOffsetTicks = 10;
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
        public double ExtremePrice;          // invalidation level: max top / head / min bottom
        public int ArmedBarIndex;            // bar the candidate was created on
        // Amendment 2: was there a trend to reverse? Evaluated ONCE, at
        // creation, against the history behind the first defining swing.
        public bool TrendOk;
        // Amendment 3, drawing only: the swing before Swings[0] — opposite-type
        // by alternation, so it is the real origin of the leg INTO the pattern.
        // False when the list had no such swing; the drawing omits the segment
        // rather than inventing a point.
        public bool HasLeadIn;
        public PzSwing LeadInSwing;
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

    public sealed class SessionLevels
    {
        // double.NaN = unavailable (e.g. first loaded day). All prices, not offsets.
        public double PriorDayHigh = double.NaN, PriorDayLow = double.NaN;
        public double OvernightHigh = double.NaN, OvernightLow = double.NaN;
        public double PriorClose = double.NaN, DayOpen = double.NaN;
    }

    // Permission rule of spec §5: extremes must cluster in ONE band around a
    // named or round level. Toggles/half-width read from cfg at call time
    // (not snapshotted) since PzConfig is mutable for the life of the engine.
    public sealed class ZoneEngine
    {
        private readonly PzConfig _cfg;
        private SessionLevels _levels = new SessionLevels();

        public ZoneEngine(PzConfig cfg)
        {
            _cfg = cfg;
        }

        public void SetLevels(SessionLevels levels)
        {
            _levels = levels;
        }

        public SessionLevels Levels { get { return _levels; } }

        // Session levels (toggled on, non-NaN) first, then nearest round levels —
        // a named level beats a round number for reporting when both match.
        public List<double> CandidateLevels(double refPrice)
        {
            var result = new List<double>();
            if (_cfg.UsePriorDayHL)
            {
                if (!double.IsNaN(_levels.PriorDayHigh)) result.Add(_levels.PriorDayHigh);
                if (!double.IsNaN(_levels.PriorDayLow)) result.Add(_levels.PriorDayLow);
            }
            if (_cfg.UseOvernightHL)
            {
                if (!double.IsNaN(_levels.OvernightHigh)) result.Add(_levels.OvernightHigh);
                if (!double.IsNaN(_levels.OvernightLow)) result.Add(_levels.OvernightLow);
            }
            if (_cfg.UsePriorClose && !double.IsNaN(_levels.PriorClose))
                result.Add(_levels.PriorClose);
            if (_cfg.UseDayOpen && !double.IsNaN(_levels.DayOpen))
                result.Add(_levels.DayOpen);

            if (_cfg.UseRound100)
            {
                double r100 = Math.Round(refPrice / 100.0) * 100.0;
                result.Add(r100);
            }
            if (_cfg.UseRound50)
            {
                double r50 = Math.Round(refPrice / 50.0) * 50.0;
                if (result.Count == 0 || r50 != result[result.Count - 1])
                    result.Add(r50);
            }
            return result;
        }

        public bool Permits(PatternCandidate p, double atr, out double zoneLevel)
        {
            // Amendment 1: the PERMISSION band is half-width + proximity, wider
            // than the band the shell draws (half-width alone). Deliberate — a
            // pattern that forms just outside a level still qualifies, so an
            // extreme can sit visibly outside the drawn band and still trade.
            double hw = (_cfg.ZoneHalfWidthAtr + _cfg.ZoneProximityAtr) * atr;
            int required = RequiredCount(p.Kind);

            foreach (double level in CandidateLevels(p.ZoneExtremes[0]))
            {
                int count = 0;
                foreach (double e in p.ZoneExtremes)
                    if (Math.Abs(e - level) <= hw)
                        count++;
                if (count >= required)
                {
                    zoneLevel = level;
                    return true;
                }
            }
            zoneLevel = 0.0;
            return false;
        }

        private static int RequiredCount(PatternKind kind)
        {
            switch (kind)
            {
                case PatternKind.HeadShoulders:
                case PatternKind.InverseHeadShoulders:
                    return 1;
                default:
                    return 2;
            }
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
                HasLeadIn = swings.Count >= 4,
                LeadInSwing = swings.Count >= 4 ? swings[swings.Count - 4] : default(PzSwing),
            };
        }

        // Last 5 swings e1,v1,e2,v2,e3 (e* same type, v* opposite).
        public static PatternCandidate TryTriple(IReadOnlyList<PzSwing> swings, PzConfig cfg, double atr)
        {
            if (swings.Count < 5)
                return null;

            int n = swings.Count;
            PzSwing e1 = swings[n - 5], v1 = swings[n - 4], e2 = swings[n - 3], v2 = swings[n - 2], e3 = swings[n - 1];

            if (e1.IsHigh != e2.IsHigh || e2.IsHigh != e3.IsHigh)
                return null;
            if (v1.IsHigh != v2.IsHigh || v1.IsHigh == e1.IsHigh)
                return null;

            double tol = cfg.TopToleranceAtr * atr;
            if (Math.Abs(e1.Price - e2.Price) > tol || Math.Abs(e2.Price - e3.Price) > tol || Math.Abs(e1.Price - e3.Price) > tol)
                return null;

            bool isTop = e1.IsHigh;
            PzSwing worst = isTop
                ? (v1.Price <= v2.Price ? v1 : v2)
                : (v1.Price >= v2.Price ? v1 : v2);

            return new PatternCandidate
            {
                Kind = isTop ? PatternKind.TripleTop : PatternKind.TripleBottom,
                IsShort = isTop,
                Swings = new[] { e1, v1, e2, v2, e3 },
                ExtremePrice = isTop
                    ? Math.Max(e1.Price, Math.Max(e2.Price, e3.Price))
                    : Math.Min(e1.Price, Math.Min(e2.Price, e3.Price)),
                ArmedBarIndex = e3.BarIndex,
                NeckP1 = worst,
                NeckP2 = worst,
                ZoneExtremes = new[] { e1.Price, e2.Price, e3.Price },
                HasLeadIn = n >= 6,
                LeadInSwing = n >= 6 ? swings[n - 6] : default(PzSwing),
            };
        }

        // Last 5 swings e1,v1,e2,v2,e3; e2 is the head, e1/e3 the shoulders.
        public static PatternCandidate TryHeadShoulders(IReadOnlyList<PzSwing> swings, PzConfig cfg, double atr)
        {
            if (swings.Count < 5)
                return null;

            int n = swings.Count;
            PzSwing e1 = swings[n - 5], v1 = swings[n - 4], e2 = swings[n - 3], v2 = swings[n - 2], e3 = swings[n - 1];

            if (e1.IsHigh != e2.IsHigh || e2.IsHigh != e3.IsHigh)
                return null;
            if (v1.IsHigh != v2.IsHigh || v1.IsHigh == e1.IsHigh)
                return null;

            bool isTop = e1.IsHigh;
            double prominence = cfg.HeadProminenceAtr * atr;
            double headOverShoulder1 = isTop ? e2.Price - e1.Price : e1.Price - e2.Price;
            double headOverShoulder2 = isTop ? e2.Price - e3.Price : e3.Price - e2.Price;
            if (headOverShoulder1 < prominence || headOverShoulder2 < prominence)
                return null;
            if (Math.Abs(e1.Price - e3.Price) > cfg.TopToleranceAtr * atr)
                return null;

            return new PatternCandidate
            {
                Kind = isTop ? PatternKind.HeadShoulders : PatternKind.InverseHeadShoulders,
                IsShort = isTop,
                Swings = new[] { e1, v1, e2, v2, e3 },
                ExtremePrice = e2.Price,
                ArmedBarIndex = e3.BarIndex,
                NeckP1 = v1,
                NeckP2 = v2,
                ZoneExtremes = new[] { e2.Price },
                HasLeadIn = n >= 6,
                LeadInSwing = n >= 6 ? swings[n - 6] : default(PzSwing),
            };
        }
    }

    // Continuation add-on: arm on a fill's direction/price, feed closed bars
    // while armed. Tracks a favorable-extreme "pole" from the anchor, then a
    // tight "flag" consolidation after it; fires an add trigger when price
    // closes beyond the flag envelope in favor. State machine per task-6 spec.
    public sealed class FlagInfo
    {
        public int PoleStartBar, PoleEndBar;         // anchor bar -> pole extreme bar
        public double PoleStartPrice, PoleEndPrice;
        public int FlagStartBar, FlagEndBar;         // consolidation window
        public double FlagHigh, FlagLow;
        // Chart times so the drawing layer never has to map bar indexes back:
        public DateTime PoleStartTime, PoleEndTime, FlagStartTime;
    }

    public sealed class FlagDetector
    {
        private readonly PzConfig _cfg;

        private bool _armed;
        private int _dir;
        private double _anchorPrice;
        private int _anchorBar;
        private DateTime _anchorTime;
        private bool _anchorTimeKnown;

        private double _extreme;
        private int _extremeBar;
        private DateTime _extremeTime;

        private bool _flagActive;
        private int _flagCount;
        private double _flagHigh, _flagLow;
        private int _flagStartBar, _flagEndBar;
        private DateTime _flagStartTime;

        public FlagDetector(PzConfig cfg)
        {
            _cfg = cfg;
        }

        public void Arm(int dir, double anchorPrice, int anchorBar)
        {
            _armed = true;
            _dir = dir;
            _anchorPrice = anchorPrice;
            _anchorBar = anchorBar;
            _anchorTimeKnown = false;
            _extreme = anchorPrice;
            _extremeBar = anchorBar;
            _flagActive = false;
            _flagCount = 0;
        }

        public void Disarm()
        {
            _armed = false;
        }

        public FlagInfo Update(PzBar bar, int barIndex, double atr)
        {
            if (!_armed)
                return null;
            // ponytail: Arm() takes no bar time (price/index only), so the
            // exact anchor-bar time is unknown; approximate with the first
            // fed bar's time. Re-anchors below see the real bar and are exact.
            if (!_anchorTimeKnown)
            {
                _anchorTime = bar.Time;
                _anchorTimeKnown = true;
            }

            return _flagActive
                ? UpdateBuildingFlag(bar, barIndex, atr)
                : UpdateWaitingForPole(bar, barIndex, atr);
        }

        private FlagInfo UpdateWaitingForPole(PzBar bar, int barIndex, double atr)
        {
            double candidate = _dir > 0 ? bar.High : bar.Low;
            bool extended = _dir > 0 ? candidate > _extreme : candidate < _extreme;
            if (extended)
            {
                _extreme = candidate;
                _extremeBar = barIndex;
                _extremeTime = bar.Time;
                return null;
            }
            // ponytail: an all-extension run (no pullback bar) would otherwise
            // leave extremeBar - anchorBar growing past PoleMaxBars forever,
            // orphaning the detector since PoleExists() could never pass again.
            // Mirror the flag side's re-anchor-on-overrun; a too-small move
            // that's still within budget just keeps waiting.
            if (_extremeBar - _anchorBar > _cfg.PoleMaxBars)
            {
                Reanchor(bar, barIndex);
                return null;
            }
            if (!PoleExists(atr))
                return null;

            _flagActive = true;
            AddFlagBar(bar, barIndex);
            return CheckEnvelope(bar, barIndex, atr);
        }

        private FlagInfo UpdateBuildingFlag(PzBar bar, int barIndex, double atr)
        {
            if (_flagCount < _cfg.FlagMinBars)
            {
                if (_dir * (bar.Close - _extreme) > 0)
                {
                    // Close beyond the pole extreme in favor before the flag
                    // matured: still the pole, not a consolidation yet.
                    // Extend the extreme and restart the flag.
                    _extreme = _dir > 0 ? bar.High : bar.Low;
                    _extremeBar = barIndex;
                    _extremeTime = bar.Time;
                    _flagCount = 0;
                    return null;
                }
                AddFlagBar(bar, barIndex);
                return CheckEnvelope(bar, barIndex, atr);
            }

            bool trigger = _dir > 0
                ? bar.Close >= _flagHigh + _cfg.TickSize
                : bar.Close <= _flagLow - _cfg.TickSize;
            if (trigger)
            {
                FlagInfo info = BuildInfo();
                Reanchor(bar, barIndex);
                return info;
            }
            AddFlagBar(bar, barIndex);
            return CheckEnvelope(bar, barIndex, atr);
        }

        private bool PoleExists(double atr)
        {
            return _dir * (_extreme - _anchorPrice) >= _cfg.PoleMinAtr * atr
                && _extremeBar - _anchorBar <= _cfg.PoleMaxBars;
        }

        private void AddFlagBar(PzBar bar, int barIndex)
        {
            if (_flagCount == 0)
            {
                _flagHigh = bar.High;
                _flagLow = bar.Low;
                _flagStartBar = barIndex;
                _flagStartTime = bar.Time;
            }
            else
            {
                _flagHigh = Math.Max(_flagHigh, bar.High);
                _flagLow = Math.Min(_flagLow, bar.Low);
            }
            _flagEndBar = barIndex;
            _flagCount++;
        }

        // Range/max-bars breach re-anchors regardless of how the flag bar
        // arrived (fresh start or already-matured flag still accreting).
        private FlagInfo CheckEnvelope(PzBar bar, int barIndex, double atr)
        {
            bool tooWide = _flagHigh - _flagLow > _cfg.FlagRangeMaxAtr * atr;
            bool tooLong = _flagCount > _cfg.FlagMaxBars;
            if (tooWide || tooLong)
                Reanchor(bar, barIndex);
            return null;
        }

        private void Reanchor(PzBar bar, int barIndex)
        {
            _anchorPrice = bar.Close;
            _anchorBar = barIndex;
            _anchorTime = bar.Time;
            _anchorTimeKnown = true;
            _extreme = _anchorPrice;
            _extremeBar = barIndex;
            _extremeTime = bar.Time;
            _flagActive = false;
            _flagCount = 0;
        }

        private FlagInfo BuildInfo()
        {
            return new FlagInfo
            {
                PoleStartBar = _anchorBar,
                PoleEndBar = _extremeBar,
                PoleStartPrice = _anchorPrice,
                PoleEndPrice = _extreme,
                FlagStartBar = _flagStartBar,
                FlagEndBar = _flagEndBar,
                FlagHigh = _flagHigh,
                FlagLow = _flagLow,
                PoleStartTime = _anchorTime,
                PoleEndTime = _extremeTime,
                FlagStartTime = _flagStartTime,
            };
        }
    }

    public enum PzActionType { EnterLong, EnterShort, AddLong, AddShort, DrawPattern, DrawFlag, DrawRejected }

    public sealed class PzAction
    {
        public PzActionType Type;
        public double StopPrice, TargetPrice;    // Enter*: initial bracket. Add*: StopPrice = new AGGREGATE stop.
        public PatternCandidate Pattern;         // Enter* + DrawPattern + DrawRejected
        public FlagInfo Flag;                    // Add* + DrawFlag
        // The neckline evaluated at the BREAK bar, in ENGINE index space (the
        // shell counts bars separately: a Playback rewind rebuilds the engine
        // from bar 0 while NT8's CurrentBar keeps climbing).
        // Amendment 1: the drawing layer no longer consumes this — the neckline
        // is not drawn. Kept because it is the trigger price of record.
        public double NecklineAtBreak;
        // DrawRejected: "zone" | "height" | "trend" | "stop" | "busy" | "session_cap" | "flag_no_position".
        // The last one is unreachable here by construction (spec §7/rule 8: the
        // flag detector is only ever armed while in position), so no branch
        // below emits it — it stays in the shell's vocabulary, not the engine's.
        public string RejectReason;
    }

    // The brain: swings -> alternating list -> pattern candidates -> neckline
    // break -> permission gauntlet -> typed actions the shell executes. Owns no
    // NT8 concepts and no ATR: the shell feeds one closed bar plus the ATR value
    // and whether trading is allowed, and reports fills back through On*.
    public sealed class PzEngine
    {
        private enum PzState { Flat, AwaitingEntryFill, InPosition, AwaitingAddFill }

        private const int MaxRetainedSwings = 40;
        // Amendment 2: recent bar extremes, keyed by bar index, for the
        // prior-trend window. ponytail: the window anchors on the first defining
        // swing and reaches BACKWARD, so the binding constraint is just
        // TrendLookbackBars + 1 <= 512 — comfortably true at the Range(10, 500)
        // ceiling. A first swing older than the ring is treated as unevaluable
        // and PASSES, the same way short history does: the gate never fails on
        // missing data.
        private const int TrendRingSize = 512;

        private readonly PzConfig _cfg;
        private readonly SwingDetector _swingDetector;
        private readonly ZoneEngine _zones;
        private readonly FlagDetector _flags;
        private readonly List<PzSwing> _alt = new List<PzSwing>();
        private readonly double[] _ringHigh = new double[TrendRingSize];
        private readonly double[] _ringLow = new double[TrendRingSize];
        // Swings that belonged to a pattern that actually entered; they can
        // never arm anything again. Persists across sessions with _alt.
        private readonly HashSet<long> _consumed = new HashSet<long>();

        private PatternCandidate _armedLong, _armedShort;
        private PzState _state = PzState.Flat;
        private int _barIndex = -1;
        private int _trades;
        private int _adds;
        private int _dir;              // open trade direction: +1 long, -1 short
        private double _target;        // open trade target, for the add-on guard

        public PzEngine(PzConfig cfg)
        {
            _cfg = cfg;
            _swingDetector = new SwingDetector(cfg.SwingStrength);
            _zones = new ZoneEngine(cfg);
            _flags = new FlagDetector(cfg);
        }

        public SessionLevels Levels { get { return _zones.Levels; } }

        // Swings, consumed marks and the ATR recursion (shell-side) cross the
        // session boundary — structure does not restart at the open.
        public void OnSessionOpen(SessionLevels levels)
        {
            _zones.SetLevels(levels);
            _trades = 0;
            _armedLong = null;
            _armedShort = null;
            _flags.Disarm();
            _adds = 0;
        }

        public List<PzAction> OnBarClosed(PzBar bar, double atr, bool canTrade)
        {
            _barIndex++;
            _ringHigh[_barIndex % TrendRingSize] = bar.High;
            _ringLow[_barIndex % TrendRingSize] = bar.Low;
            var actions = new List<PzAction>();

            foreach (PzSwing s in _swingDetector.Update(bar, _barIndex))
                if (Integrate(s))
                    Arm(atr);

            Resolve(ref _armedShort, bar, atr, canTrade, actions);
            Resolve(ref _armedLong, bar, atr, canTrade, actions);
            UpdateFlags(bar, atr, canTrade, actions);
            return actions;
        }

        public void OnEntryFilled(double fillPrice)
        {
            _state = PzState.InPosition;
            _adds = 0;
            if (_cfg.EnableFlagAddon && _cfg.MaxAdds > 0)
                _flags.Arm(_dir, fillPrice, _barIndex);
        }

        // The detector re-anchors itself on trigger, which is only safe because
        // the engine disarms it the moment an add is emitted: re-arming here on
        // the actual fill is the single place the pole may start from.
        public void OnAddFilled(double fillPrice)
        {
            _adds++;
            _state = PzState.InPosition;
            if (_adds < _cfg.MaxAdds)
                _flags.Arm(_dir, fillPrice, _barIndex);
            else
                _flags.Disarm();
        }

        // Submission rejected: back to flat. The pattern's swings stay consumed
        // on purpose — a failed submission must not re-fire the same structure.
        public void OnEntryFailed()
        {
            _state = PzState.Flat;
            _flags.Disarm();
        }

        // Add submission rejected: the base position is untouched, so back to
        // in-position with the add count unspent. The detector stays disarmed
        // (emitting the add disarmed it) — no more adds this trade. Fails safe:
        // an add rejection is rare, and re-arming would need an anchor price
        // the order layer never got.
        public void OnAddFailed()
        {
            _state = PzState.InPosition;
        }

        public void OnPositionClosed()
        {
            _state = PzState.Flat;
            _flags.Disarm();
            _adds = 0;
        }

        // Alternation: same-type in a row collapses to the more extreme one, in
        // place, so the tail the scanners read is always e,v,e,v,e. Returns
        // whether the list changed (a rejected duplicate can't arm anything).
        private bool Integrate(PzSwing s)
        {
            if (_alt.Count > 0 && _alt[_alt.Count - 1].IsHigh == s.IsHigh)
            {
                PzSwing last = _alt[_alt.Count - 1];
                bool moreExtreme = s.IsHigh ? s.Price > last.Price : s.Price < last.Price;
                if (!moreExtreme)
                    return false;
                _alt[_alt.Count - 1] = s;
                return true;
            }
            _alt.Add(s);
            if (_alt.Count > MaxRetainedSwings)
                _alt.RemoveAt(0);
            return true;
        }

        private void Arm(double atr)
        {
            PatternCandidate c = Scan(atr);
            if (c == null)
                return;
            c.ArmedBarIndex = _barIndex;
            c.TrendOk = TrendPermits(c);
            if (c.IsShort)
                _armedShort = c;
            else
                _armedLong = c;
        }

        // Amendment 2. A top-family pattern only reverses something if its FIRST
        // top is the highest high of the window behind it — you can only print
        // that after an up-leg. Mirror for bottoms. Ties pass: the first top is
        // itself a bar high inside the window, so the test is "nothing beat it".
        // Evaluated at creation, off the first defining swing, so a pattern is
        // judged by the leg that built it and not by whatever happened while it
        // was still arming.
        private bool TrendPermits(PatternCandidate c)
        {
            if (!_cfg.UseTrendFilter)
                return true;
            PzSwing s0 = c.Swings[0];
            int oldest = Math.Max(0, _barIndex - (TrendRingSize - 1));
            if (s0.BarIndex < oldest)
                return true;                     // older than the ring: unevaluable, never a rejection
            int start = Math.Max(s0.BarIndex - _cfg.TrendLookbackBars, oldest);
            for (int i = start; i <= s0.BarIndex; i++)
            {
                bool beaten = c.IsShort
                    ? _ringHigh[i % TrendRingSize] > s0.Price
                    : _ringLow[i % TrendRingSize] < s0.Price;
                if (beaten)
                    return false;
            }
            return true;
        }

        // Most specific first. A match built on a consumed swing is skipped
        // rather than fatal: the same tail often yields a triple whose first
        // extreme was already traded and a double that is entirely fresh.
        private PatternCandidate Scan(double atr)
        {
            PatternCandidate c = PatternScanner.TryTriple(_alt, _cfg, atr);
            if (IsFresh(c))
                return c;
            c = PatternScanner.TryHeadShoulders(_alt, _cfg, atr);
            if (IsFresh(c))
                return c;
            c = PatternScanner.TryDouble(_alt, _cfg, atr);
            return IsFresh(c) ? c : null;
        }

        private bool IsFresh(PatternCandidate c)
        {
            if (c == null)
                return false;
            foreach (PzSwing s in c.Swings)
                if (_consumed.Contains(SwingKey(s)))
                    return false;
            return true;
        }

        private static long SwingKey(PzSwing s)
        {
            return (long)s.BarIndex * 4 + (s.IsHigh ? 1 : 0);
        }

        // Expiry and the break test, both on every closed bar including the
        // arming one. Either way the slot is freed: a resolved candidate never
        // re-fires, and a rejected one must not re-reject on every later bar.
        private void Resolve(ref PatternCandidate slot, PzBar bar, double atr, bool canTrade, List<PzAction> actions)
        {
            PatternCandidate c = slot;
            if (c == null)
                return;

            bool failed = c.IsShort ? bar.Close > c.ExtremePrice : bar.Close < c.ExtremePrice;
            if (failed || _barIndex - c.Swings[0].BarIndex > _cfg.MaxPatternBars)
            {
                slot = null;
                return;
            }

            double neckline = c.NecklineAt(_barIndex);
            double breakBuffer = _cfg.NecklineBreakTicks * _cfg.TickSize;
            bool broke = c.IsShort
                ? bar.Close <= neckline - breakBuffer
                : bar.Close >= neckline + breakBuffer;
            if (!broke)
                return;

            slot = null;
            Fire(c, bar, neckline, atr, canTrade, actions);
        }

        private void Fire(PatternCandidate c, PzBar bar, double neckline, double atr, bool canTrade, List<PzAction> actions)
        {
            // Every gate below is ATR-scaled: at atr <= 0 the min-height filter
            // passes trivially, the zone band collapses to the level itself and
            // the stop buffer vanishes. Warmup is not a trade setup — drop it
            // silently, like the canTrade case.
            if (atr <= 0)
                return;
            if (_state != PzState.Flat)
            {
                actions.Add(Reject(c, "busy", neckline));
                return;
            }
            if (!canTrade)
                return;                              // lockout / window: the shell already knows why
            if (_trades >= _cfg.MaxTradesPerSession)
            {
                actions.Add(Reject(c, "session_cap", neckline));
                return;
            }

            double height = Math.Abs(c.ExtremePrice - neckline);
            if (height < _cfg.MinPatternHeightAtr * atr)
            {
                actions.Add(Reject(c, "height", neckline));
                return;
            }
            // Amendment 2, and it sits AHEAD of the zone: a pattern with no
            // trend behind it is not a reversal at all, whatever level it
            // formed on. Decided at creation (c.TrendOk), reported here.
            if (!c.TrendOk)
            {
                actions.Add(Reject(c, "trend", neckline));
                return;
            }
            double zoneLevel;
            if (!_zones.Permits(c, atr, out zoneLevel))
            {
                actions.Add(Reject(c, "zone", neckline));
                return;
            }

            int dirSign = c.IsShort ? -1 : 1;
            // Amendment 1 (Javier, pre-P&L): the stop anchors the pattern's LAST
            // defining swing, not its extreme. By construction that swing is
            // always the last EXTREME — second top/bottom, third extreme of a
            // triple, and the RIGHT SHOULDER of an H&S, deliberately not the
            // head: the head is what invalidates the candidate, the shoulder is
            // what the break has to hold. Fixed tick distance, not ATR-scaled.
            double stopAnchor = c.Swings[c.Swings.Length - 1].Price;
            double stopPrice = stopAnchor - dirSign * _cfg.StopOffsetTicks * _cfg.TickSize;
            // A sloped neckline extrapolates: on an H&S it can climb past the
            // right shoulder by the time the break lands, which puts the stop on
            // the WRONG side of the entry — an inverted bracket. Reject rather
            // than re-anchor: a break priced beyond the pattern's own right
            // shoulder is not the setup any more.
            if (dirSign * (stopPrice - bar.Close) >= 0)
            {
                actions.Add(Reject(c, "stop", neckline));
                return;
            }
            actions.Add(new PzAction
            {
                Type = c.IsShort ? PzActionType.EnterShort : PzActionType.EnterLong,
                Pattern = c,
                StopPrice = stopPrice,
                TargetPrice = neckline + dirSign * height * _cfg.TargetMultiple,
                NecklineAtBreak = neckline,
            });
            actions.Add(new PzAction { Type = PzActionType.DrawPattern, Pattern = c, NecklineAtBreak = neckline });

            foreach (PzSwing s in c.Swings)
                _consumed.Add(SwingKey(s));
            _trades++;
            _dir = dirSign;
            _target = actions[actions.Count - 2].TargetPrice;
            _state = PzState.AwaitingEntryFill;
        }

        private static PzAction Reject(PatternCandidate c, string reason, double neckline)
        {
            return new PzAction { Type = PzActionType.DrawRejected, Pattern = c, RejectReason = reason, NecklineAtBreak = neckline };
        }

        private void UpdateFlags(PzBar bar, double atr, bool canTrade, List<PzAction> actions)
        {
            if (atr <= 0)
                return;                              // same warmup guard as Fire: pole, envelope and guard are all ATR-scaled
            if (_state != PzState.InPosition || !_cfg.EnableFlagAddon || _adds >= _cfg.MaxAdds)
                return;

            FlagInfo f = _flags.Update(bar, _barIndex, atr);
            if (f == null || !canTrade)
                return;
            if (_dir * (_target - bar.Close) < _cfg.MinDistToTargetAtr * atr)
                return;                              // too little room left to be worth a tranche

            actions.Add(new PzAction
            {
                Type = _dir > 0 ? PzActionType.AddLong : PzActionType.AddShort,
                Flag = f,
                StopPrice = _dir > 0
                    ? f.FlagLow - _cfg.StopBufferAtr * atr
                    : f.FlagHigh + _cfg.StopBufferAtr * atr,
                TargetPrice = _target,               // unchanged: every tranche exits together
            });
            actions.Add(new PzAction { Type = PzActionType.DrawFlag, Flag = f });
            _flags.Disarm();
            _state = PzState.AwaitingAddFill;
        }
    }
}
