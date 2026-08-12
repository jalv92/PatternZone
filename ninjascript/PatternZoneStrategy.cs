// PatternZoneStrategy — classic reversal patterns on 1m MNQ, gated by
// long-memory S/R zones; flags add to winners. Detection/decisions live in
// PatternZoneCore (PatternZoneCore.cs, same Custom/Strategies folder) — this
// file is plumbing: series, session levels, orders, drawing.
// Spec: docs/design.md in the repo. Defaults are FROZEN (spec section 10).
//
// CHART REQUIREMENTS:
//   * Primary series = 1 Minute. Every ATR-scaled gate and every bar budget in
//     PatternZoneCore counts 1m bars.
//   * The instrument's FULL ETH session template, NOT an RTH one. The overnight
//     high/low are two of the six permission levels, so an RTH-only template
//     silently deletes them (they stay NaN and the zone engine just skips them —
//     no error, fewer trades). DataLoaded/OnBarUpdate warn when the loaded bars
//     look RTH-only; this is the OPPOSITE of PullbackZone's ETH warning.
//   * NT8's global time zone = US Eastern. Every HHMM parameter and the
//     09:30 / 16:00 / 18:00 session boundaries below are ET wall clock.
//
// ATR: PatternZoneCore.WilderAtr(14), hand-rolled and fed from OnBarUpdate —
// nt8c cannot resolve the ATR() system wrapper (workspace gotcha) and the core's
// recursion is the one the 109 unit tests pin. It crosses sessions and never
// resets, so `canTrade` also gates on CurrentBar >= 14: the engine's internal
// `atr <= 0` guard only rejects bar one, while a partially-warmed ATR is
// positive and shrinks every ATR-scaled gate proportionally.
//
// ORDERS ARE OFF IN THIS FILE'S CURRENT STATE. Every engine action is Print()ed
// and nothing is submitted; the order layer is task 9 and the drawing layer
// task 10. The engine's fill callbacks (OnEntryFilled/OnAddFilled/OnEntryFailed/
// OnAddFailed/OnPositionClosed) are therefore never called yet — the engine
// stays in AwaitingEntryFill after the first entry action by design.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;   // REQUIRED for Draw.* — hook won't miss it, F5 will
using PatternZoneCore;                        // hook reports CS0246 here: FALSE POSITIVE, keep it
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class PatternZoneStrategy : Strategy
    {
        // Internal constants, NOT parameters. The ATR period is pinned by the
        // core's unit tests; the probe length is a diagnostic threshold.
        private const int AtrPeriod = 14;
        private const int RthOpenSecs = 9 * 3600 + 30 * 60;
        private const int RthCloseSecs = 16 * 3600;
        private const int OnOpenSecs = 18 * 3600;
        // A full RTH day (390 bars) plus slack: an ETH template cannot run this
        // long without printing a bar outside 09:30-16:00.
        private const int RthOnlyProbeBars = 400;

        private PzEngine _engine;
        private PzConfig _cfg;
        private WilderAtr _atr;

        // --- session-level accumulators (ETH series, ET clock assumed) ---
        private DateTime _curRthDate = DateTime.MinValue;
        private double _rthHigh = double.NaN, _rthLow = double.NaN, _rthLastClose = double.NaN;
        private double _onHigh = double.NaN, _onLow = double.NaN;
        private double _prevRthHigh = double.NaN, _prevRthLow = double.NaN, _prevRthClose = double.NaN;
        // Keyed by the EVENING the overnight session opened, so the bar stamped
        // 00:00:00 keeps folding into the session that started at 18:00.
        private DateTime _curOnDate = DateTime.MinValue;

        private bool _lockout;
        private int _entriesWindowStartSecs, _cutoffSecs;
        private DateTime _lastBarTime = DateTime.MinValue;
        private readonly HashSet<string> _drawTags = new HashSet<string>();

        private int _barSecs = 60;
        private bool _rthOnlyWarned;
        private int _rthOnlyProbe;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PatternZoneStrategy";
                Description = "Classic reversal patterns (double/triple top-bottom, head & shoulders) on 1m MNQ, permitted to trade only where their extremes sit on a long-memory S/R level; a flag continuation adds one tranche to a winner. Requires an ETH session template. See docs/design.md.";
                Calculate = Calculate.OnBarClose;   // decisions on closed bars; resting orders act intrabar
                EntriesPerDirection = 1 + 5;        // MaxAdds ceiling; validated in DataLoaded
                EntryHandling = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;
                // 0, not a warmup count: the ATR warmup is enforced in OnBarUpdate
                // (CurrentBar >= AtrPeriod) so the recursion still sees every bar.
                BarsRequiredToTrade = 0;

                // FROZEN DEFAULTS — spec section 10. The statistical dials were
                // fixed before any P&L was seen; changing one is a documented
                // amendment, not a tweak. Only the "06. Drawing" block is free.
                SwingStrength = 3;
                TopToleranceAtr = 0.30;
                HeadProminenceAtr = 0.30;
                MaxPatternBars = 60;
                NecklineBreakTicks = 2;
                MinPatternHeightAtr = 1.5;

                ZoneHalfWidthAtr = 0.50;
                UsePriorDayHL = true;
                UseOvernightHL = true;
                UsePriorClose = true;
                UseDayOpen = true;
                UseRound100 = true;
                UseRound50 = false;

                StopBufferAtr = 0.50;
                TargetMultiple = 1.0;

                EnableFlagAddon = true;
                PoleMinAtr = 2.0;
                PoleMaxBars = 8;
                FlagMinBars = 3;
                FlagMaxBars = 10;
                FlagRangeMaxAtr = 1.0;
                MinDistToTargetAtr = 1.5;
                MaxAdds = 1;

                Contracts = 1;
                MaxTradesPerSession = 3;
                TradingStartHhmm = 930;
                TradingEndHhmm = 1555;
                DailyLossLimitUsd = 200;

                DrawZones = true;
                LongBrush = Brushes.MediumSeaGreen;
                ShortBrush = Brushes.IndianRed;
                AddonBrush = Brushes.Goldenrod;
                PatternOpacityPct = 40;
                ZoneOpacityPct = 10;
                DrawRejectedPatterns = false;
            }
            else if (State == State.DataLoaded)
            {
                _cfg = new PzConfig
                {
                    SwingStrength = SwingStrength,
                    TopToleranceAtr = TopToleranceAtr,
                    HeadProminenceAtr = HeadProminenceAtr,
                    MaxPatternBars = MaxPatternBars,
                    NecklineBreakTicks = NecklineBreakTicks,
                    TickSize = TickSize,                 // the instrument's, not a dial
                    MinPatternHeightAtr = MinPatternHeightAtr,
                    ZoneHalfWidthAtr = ZoneHalfWidthAtr,
                    UsePriorDayHL = UsePriorDayHL,
                    UseOvernightHL = UseOvernightHL,
                    UsePriorClose = UsePriorClose,
                    UseDayOpen = UseDayOpen,
                    UseRound100 = UseRound100,
                    UseRound50 = UseRound50,
                    StopBufferAtr = StopBufferAtr,
                    TargetMultiple = TargetMultiple,
                    EnableFlagAddon = EnableFlagAddon,
                    PoleMinAtr = PoleMinAtr,
                    PoleMaxBars = PoleMaxBars,
                    FlagMinBars = FlagMinBars,
                    FlagMaxBars = FlagMaxBars,
                    FlagRangeMaxAtr = FlagRangeMaxAtr,
                    MinDistToTargetAtr = MinDistToTargetAtr,
                    MaxAdds = MaxAdds,
                    MaxTradesPerSession = MaxTradesPerSession,
                };

                _entriesWindowStartSecs = HhmmToSecs(TradingStartHhmm);
                _cutoffSecs = HhmmToSecs(TradingEndHhmm);

                _barSecs = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute
                    ? BarsPeriod.Value * 60
                    : (BarsPeriod.BarsPeriodType == BarsPeriodType.Second ? BarsPeriod.Value : 0);
                if (_barSecs != 60)
                {
                    Log(Name + ": primary series is not 1 Minute — every ATR-scaled gate and every bar budget in the spec counts 1m bars, so this chart does not run the strategy that was designed.",
                        Cbi.LogLevel.Warning);
                    if (_barSecs <= 0)
                        _barSecs = 60;
                }

                // EntriesPerDirection is baked at SetDefaults and cannot see the
                // user's MaxAdds. Range(0, 5) blocks this from the UI; an XML
                // template or an optimizer file can still get past it.
                if (MaxAdds > 5)
                    Log(Name + ": MaxAdds > 5 exceeds EntriesPerDirection (1 + 5) — adds beyond the 6th tranche will be refused by the order layer.",
                        Cbi.LogLevel.Warning);

                ResetAll(false);
            }
        }

        // removeDrawings: true only on a Playback rewind — the discarded pass's
        // objects get wiped. A session rollover keeps history on the chart.
        private void ResetAll(bool removeDrawings)
        {
            if (removeDrawings)
            {
                foreach (string tag in _drawTags)
                    RemoveDrawObject(tag);
                _drawTags.Clear();
            }

            // The engine owns the swing list, the consumed marks and the flag
            // state machine; a rewind must not carry any of it forward, and it
            // exposes no Reset(), so a fresh instance IS the reset.
            _engine = new PzEngine(_cfg);
            _atr = new WilderAtr(AtrPeriod);

            _curRthDate = DateTime.MinValue;
            _curOnDate = DateTime.MinValue;
            _rthHigh = double.NaN; _rthLow = double.NaN; _rthLastClose = double.NaN;
            _onHigh = double.NaN; _onLow = double.NaN;
            _prevRthHigh = double.NaN; _prevRthLow = double.NaN; _prevRthClose = double.NaN;
            _lockout = false;
            _rthOnlyWarned = false;
            _rthOnlyProbe = 0;
        }

        private string Tag(string t)
        {
            _drawTags.Add(t);
            return t;
        }

        private static int HhmmToSecs(int hhmm)
        {
            return (hhmm / 100) * 3600 + (hhmm % 100) * 60;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < 0)
                return;
            DateTime t = Time[0];
            if (t < _lastBarTime) ResetAll(true);          // Playback rewind, PullbackZone pattern
            _lastBarTime = t;

            var bar = new PzBar { Time = t, Open = Open[0], High = High[0], Low = Low[0], Close = Close[0] };
            _atr.Update(bar);

            // NT8 stamps a bar at its CLOSE, so every session test below is on
            // the bar's START. Taking the start as a DateTime rather than
            // subtracting 60 from the seconds-of-day keeps the DATE right too:
            // the bar stamped 00:00:00 belongs to the previous evening, and
            // keying the overnight accumulators off its close date would reset
            // them at midnight, halfway through the session they measure.
            DateTime barStart = t.AddSeconds(-_barSecs);
            int startSecs = (int)barStart.TimeOfDay.TotalSeconds;
            bool inRth = startSecs >= RthOpenSecs && startSecs < RthCloseSecs;
            bool inOn = !inRth && (startSecs >= OnOpenSecs || startSecs < RthOpenSecs);

            // Inverse of PullbackZone's _ethWarned: THIS strategy needs the
            // overnight bars, because two of the six permission levels are the
            // overnight high and low. Detection only — the strategy still runs,
            // it just runs with a smaller level set than the spec describes.
            if (!_rthOnlyWarned)
            {
                if (!inRth)
                    _rthOnlyWarned = true;                 // a non-RTH bar: ETH template confirmed
                else if (++_rthOnlyProbe > RthOnlyProbeBars)
                {
                    _rthOnlyWarned = true;
                    Log(Name + ": " + RthOnlyProbeBars + " bars have all opened inside 09:30-16:00 ET — this looks like an RTH session template. PatternZone needs the instrument's FULL ETH template: without overnight bars the overnight high/low levels stay unavailable and the zone permission runs on four levels instead of six.",
                        Cbi.LogLevel.Warning);
                }
            }

            // Overnight accumulators: reset when the 18:00 boundary is crossed.
            if (inOn)
            {
                // The session that opens at 18:00 is keyed by that evening's
                // date; the small hours belong to the PREVIOUS evening.
                DateTime onDate = startSecs >= OnOpenSecs ? barStart.Date : barStart.Date.AddDays(-1);
                if (onDate != _curOnDate)
                {
                    // ponytail: the first loaded bar can land mid-overnight, in
                    // which case this "session" is a partial range. Harmless —
                    // the first loaded day has NaN prior-day levels anyway.
                    _curOnDate = onDate;
                    _onHigh = High[0];
                    _onLow = Low[0];
                }
                else
                {
                    _onHigh = Math.Max(_onHigh, High[0]);
                    _onLow = Math.Min(_onLow, Low[0]);
                }
            }

            // RTH accumulators: keyed by date; at the FIRST RTH bar of a new
            // date, snapshot yesterday into SessionLevels + OnSessionOpen.
            if (inRth)
            {
                if (barStart.Date != _curRthDate)
                {
                    // Yesterday's aggregates become the prior-day levels. On the
                    // first RTH bar ever they are still NaN, which is exactly the
                    // "unavailable" the zone engine skips.
                    _prevRthHigh = _rthHigh;
                    _prevRthLow = _rthLow;
                    _prevRthClose = _rthLastClose;

                    _engine.OnSessionOpen(new SessionLevels
                    {
                        PriorDayHigh = _prevRthHigh,
                        PriorDayLow = _prevRthLow,
                        PriorClose = _prevRthClose,
                        OvernightHigh = _onHigh,
                        OvernightLow = _onLow,
                        DayOpen = Open[0],                 // this first RTH bar's open
                    });

                    _curRthDate = barStart.Date;
                    _lockout = false;                      // the daily lockout lasts until the next session
                    _rthHigh = High[0];
                    _rthLow = Low[0];
                }
                else
                {
                    _rthHigh = Math.Max(_rthHigh, High[0]);
                    _rthLow = Math.Min(_rthLow, Low[0]);
                }
                _rthLastClose = Close[0];
            }

            // One gate for entries AND adds: inside the window, not locked out,
            // and past the ATR warmup. The warmup term is not redundant with the
            // engine's `atr <= 0` guard — that one only rejects bar one, while a
            // half-filled Wilder mean is positive and shrinks every ATR-scaled
            // gate (min height, zone band, stop buffer, pole) proportionally.
            bool inWindow = startSecs >= _entriesWindowStartSecs && startSecs < _cutoffSecs;
            bool warm = CurrentBar >= AtrPeriod && _atr.IsReady;
            bool canTrade = !_lockout && inWindow && warm;
            List<PzAction> actions = _engine.OnBarClosed(bar, _atr.Value, canTrade);

            foreach (PzAction a in actions)                 // Task 9 replaces this with a switch
                Print(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:yyyy-MM-dd HH:mm} ACTION {2}{3} stop={4:0.##} tgt={5:0.##}{6}",
                    Name, t, a.Type,
                    a.Pattern != null ? " " + a.Pattern.Kind : "",
                    a.StopPrice, a.TargetPrice,
                    a.RejectReason != null ? " reject=" + a.RejectReason : ""));
        }

        #region Properties
        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Swing strength (bars)", Description = "Bars on each side that must not exceed an extreme for it to confirm. Confirmation arrives this many bars late by construction.", GroupName = "01. Detection", Order = 0)]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty, Range(0.05, 2.0)]
        [Display(Name = "Top tolerance (x ATR)", Description = "Max height difference between the extremes of a double/triple; also the shoulder symmetry bound.", GroupName = "01. Detection", Order = 1)]
        public double TopToleranceAtr { get; set; }

        [NinjaScriptProperty, Range(0.05, 2.0)]
        [Display(Name = "Head prominence (x ATR)", Description = "How far the head must clear BOTH shoulders for a head & shoulders to count.", GroupName = "01. Detection", Order = 2)]
        public double HeadProminenceAtr { get; set; }

        [NinjaScriptProperty, Range(10, 300)]
        [Display(Name = "Max pattern span (bars)", Description = "An armed candidate expires this many bars after its FIRST defining swing.", GroupName = "01. Detection", Order = 3)]
        public int MaxPatternBars { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Neckline break (ticks)", Description = "A close must clear the neckline by this much to trigger. 0 = a touch counts.", GroupName = "01. Detection", Order = 4)]
        public int NecklineBreakTicks { get; set; }

        [NinjaScriptProperty, Range(0.5, 5.0)]
        [Display(Name = "Min pattern height (x ATR)", Description = "Extreme-to-neckline distance below this is noise, not a pattern. Also sets the target, which is one height from the neckline.", GroupName = "01. Detection", Order = 5)]
        public double MinPatternHeightAtr { get; set; }

        [NinjaScriptProperty, Range(0.1, 2.0)]
        [Display(Name = "Zone half-width (x ATR)", Description = "Half-width of the band around a level. A pattern's extremes must cluster inside ONE band for the pattern to be permitted.", GroupName = "02. Zones", Order = 0)]
        public double ZoneHalfWidthAtr { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Prior day high/low", GroupName = "02. Zones", Order = 1)]
        public bool UsePriorDayHL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Overnight high/low", Description = "18:00-09:30 ET extremes. Requires an ETH session template — on an RTH chart these levels never exist.", GroupName = "02. Zones", Order = 2)]
        public bool UseOvernightHL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Prior RTH close", GroupName = "02. Zones", Order = 3)]
        public bool UsePriorClose { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Day open (09:30)", GroupName = "02. Zones", Order = 4)]
        public bool UseDayOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Round 100s", GroupName = "02. Zones", Order = 5)]
        public bool UseRound100 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Round 50s", Description = "Off by default: on MNQ the 50s fire often enough to make the zone permission close to no filter at all.", GroupName = "02. Zones", Order = 6)]
        public bool UseRound50 { get; set; }

        [NinjaScriptProperty, Range(0.1, 3.0)]
        [Display(Name = "Stop buffer (x ATR)", Description = "Stop beyond the pattern's extreme (entries) or beyond the flag (adds).", GroupName = "03. Entry", Order = 0)]
        public double StopBufferAtr { get; set; }

        [NinjaScriptProperty, Range(0.3, 3.0)]
        [Display(Name = "Target (x pattern height)", Description = "Measured move from the neckline, in pattern heights. Every tranche exits at this one price.", GroupName = "03. Entry", Order = 1)]
        public double TargetMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable flag add-on", Description = "Add a tranche when a pole-then-flag continuation fires in the open trade's direction.", GroupName = "04. Add-on", Order = 0)]
        public bool EnableFlagAddon { get; set; }

        [NinjaScriptProperty, Range(0.5, 6.0)]
        [Display(Name = "Pole minimum (x ATR)", Description = "Favorable move from the last fill before a consolidation can count as a flag.", GroupName = "04. Add-on", Order = 1)]
        public double PoleMinAtr { get; set; }

        [NinjaScriptProperty, Range(2, 30)]
        [Display(Name = "Pole max (bars)", Description = "The pole must be built within this many bars of the anchor, or the detector re-anchors.", GroupName = "04. Add-on", Order = 2)]
        public int PoleMaxBars { get; set; }

        [NinjaScriptProperty, Range(2, 10)]
        [Display(Name = "Flag min (bars)", Description = "Consolidation bars required before a breakout can trigger the add.", GroupName = "04. Add-on", Order = 3)]
        public int FlagMinBars { get; set; }

        [NinjaScriptProperty, Range(3, 30)]
        [Display(Name = "Flag max (bars)", Description = "A consolidation longer than this stops being a flag; the detector re-anchors.", GroupName = "04. Add-on", Order = 4)]
        public int FlagMaxBars { get; set; }

        [NinjaScriptProperty, Range(0.2, 3.0)]
        [Display(Name = "Flag max range (x ATR)", Description = "A consolidation wider than this is a reversal, not a flag.", GroupName = "04. Add-on", Order = 5)]
        public double FlagRangeMaxAtr { get; set; }

        [NinjaScriptProperty, Range(0.0, 5.0)]
        [Display(Name = "Min room to target (x ATR)", Description = "Skip the add when the target is closer than this — a tranche with no room left pays costs for nothing.", GroupName = "04. Add-on", Order = 6)]
        public double MinDistToTargetAtr { get; set; }

        [NinjaScriptProperty, Range(0, 5)]
        [Display(Name = "Max adds per trade", Description = "Tranches added on top of the base entry. Capped at 5 by EntriesPerDirection.", GroupName = "04. Add-on", Order = 7)]
        public int MaxAdds { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Contracts", Description = "Base tranche size. Each add is another tranche of the same size.", GroupName = "05. Risk", Order = 0)]
        public int Contracts { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Max trades per session", Description = "Base entries per session; adds do not count.", GroupName = "05. Risk", Order = 1)]
        public int MaxTradesPerSession { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Window start (ET HHMM)", Description = "No entries and no adds before this time.", GroupName = "05. Risk", Order = 2)]
        public int TradingStartHhmm { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Window end (ET HHMM)", Description = "No entries at/after this time; any position is flattened.", GroupName = "05. Risk", Order = 3)]
        public int TradingEndHhmm { get; set; }

        [NinjaScriptProperty, Range(0, 100000)]
        [Display(Name = "Daily loss limit ($)", Description = "Realized session loss at or beyond this flattens and locks out until the next session. 0 = off.", GroupName = "05. Risk", Order = 4)]
        public double DailyLossLimitUsd { get; set; }

        // Cosmetic dials — spec section 10 leaves these free to change anytime.
        [NinjaScriptProperty]
        [Display(Name = "Draw zones", GroupName = "06. Drawing", Order = 0)]
        public bool DrawZones { get; set; }

        [XmlIgnore]
        [Display(Name = "Long pattern brush", GroupName = "06. Drawing", Order = 1)]
        public Brush LongBrush { get; set; }
        [Browsable(false)]
        public string LongBrushSerialize { get { return Serialize.BrushToString(LongBrush); } set { LongBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Short pattern brush", GroupName = "06. Drawing", Order = 2)]
        public Brush ShortBrush { get; set; }
        [Browsable(false)]
        public string ShortBrushSerialize { get { return Serialize.BrushToString(ShortBrush); } set { ShortBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Add-on brush", GroupName = "06. Drawing", Order = 3)]
        public Brush AddonBrush { get; set; }
        [Browsable(false)]
        public string AddonBrushSerialize { get { return Serialize.BrushToString(AddonBrush); } set { AddonBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty, Range(5, 100)]
        [Display(Name = "Pattern opacity (%)", GroupName = "06. Drawing", Order = 4)]
        public int PatternOpacityPct { get; set; }

        [NinjaScriptProperty, Range(2, 100)]
        [Display(Name = "Zone opacity (%)", GroupName = "06. Drawing", Order = 5)]
        public int ZoneOpacityPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw rejected patterns", Description = "Diagnostic: draw the patterns the permission gauntlet refused, with the reason. Noisy — off for real runs.", GroupName = "06. Drawing", Order = 6)]
        public bool DrawRejectedPatterns { get; set; }
        #endregion
    }
}
