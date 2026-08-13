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
// recursion is the one the 141 unit tests pin. It crosses sessions and never
// resets, so `canTrade` also gates on 14 fed bars: the engine's internal
// `atr <= 0` guard only rejects bar one, while a partially-warmed ATR is
// positive and shrinks every ATR-scaled gate proportionally.
//
// ORDERS: market entries, ONE aggregate stop + ONE aggregate target covering
// every tranche (fromEntrySignal = "", live-until-cancelled, both legs always
// resubmitted together), adds that re-price the stop only, a daily loss/profit
// lockout — optionally measured across every PatternZone on the account
// (Amendment 6) — and a window flatten. Drawing is pattern/flag geometry only —
// no neckline, no text; full legs incl. lead-in/out (Amendments 1+3, decision #6).
//
// THE ORDER-EVENT RACE RULES THIS FILE. NT8 — Playback especially — can deliver
// OnOrderUpdate/OnExecutionUpdate synchronously, in-stack, BEFORE the Enter*/
// Exit* call that caused them returns. So every in-flight flag and every price
// tracker is written BEFORE its submit, and every clear in the handlers is gated
// on the signal NAME that set it. A tracker written after a submit is a tracker
// the handler already read stale.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;                              // ATM template dropdown (Amendment 5)
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
        // Internal constant, NOT a parameter: the ATR period is pinned by the
        // core's unit tests.
        private const int AtrPeriod = 14;
        private const int RthOpenSecs = 9 * 3600 + 30 * 60;
        private const int RthCloseSecs = 16 * 3600;
        private const int OnOpenSecs = 18 * 3600;

        private PzEngine _engine;
        private PzConfig _cfg;
        private WilderAtr _atr;
        // Bars fed to _atr, NOT CurrentBar: a Playback rewind rebuilds _atr from
        // scratch while CurrentBar keeps counting, which would leave the warmup
        // gate open over a one-sample ATR. Reset on the same path that rebuilds
        // the ATR, so the two can never drift apart.
        private int _atrBars;

        // --- session-level accumulators (ETH series, ET clock assumed) ---
        private DateTime _curRthDate = DateTime.MinValue;
        private double _rthHigh = double.NaN, _rthLow = double.NaN, _rthLastClose = double.NaN;
        private double _onHigh = double.NaN, _onLow = double.NaN;
        private double _prevRthHigh = double.NaN, _prevRthLow = double.NaN, _prevRthClose = double.NaN;
        // Keyed by the EVENING the overnight session opened, so the bar stamped
        // 00:00:00 keeps folding into the session that started at 18:00.
        private DateTime _curOnDate = DateTime.MinValue;

        private bool _lockout;

        // --- order layer -----------------------------------------------------
        private const string SigStop = "PZ_Stop";
        private const string SigTarget = "PZ_Target";
        private const string SigFlatten = "PZ_Flatten";

        // The frozen actions behind the two orders that can be in flight, and
        // the signal names actually submitted for them — the handlers gate every
        // clear on the name, never on an Order reference: the reference is only
        // assigned after Enter* RETURNS, which an in-stack fill beats.
        private PzAction _pendingAction, _pendingAdd;
        private string _entrySig, _addSig;
        private bool _entryPending, _addPending, _flattenPending;
        // Assigned only AFTER Enter* returns, and read for exactly one thing:
        // cancelling a PartFilled entry's remainder when the position it opened
        // closes. Never used for attribution — an in-stack fill beats the
        // assignment, which is why the handlers match on the signal name.
        private Order _entryOrder;
        // The CURRENT bracket legs. Their only job is to let OnOrderUpdate tell a
        // hand-cancel from the Cancelled echo of our own cancel-replace on an add
        // resize: the echo carries the OLD order, which is no longer either ref.
        private Order _stopOrder, _targetOrder;
        // Deferred hand-cancel detector — a bracket Cancelled only counts as "by
        // hand" if the position is still open a bar later.
        private DateTime _stopCancelAt = DateTime.MinValue, _targetCancelAt = DateTime.MinValue;
        // A tracked, bracketed position exists. Distinguishes a later partial of
        // a known order (resize) from a fill with nothing behind it (flatten).
        private bool _inTrade;
        private int _dir;                          // +1 long, -1 short, this trade
        // Contracts held, summed from the execution events themselves rather than
        // read off Position: the brackets must cover the fill being reported now,
        // and NT8's own OnExecutionUpdate sample sizes from the order's fills for
        // exactly that reason. Reset with the rest of the trade on went-flat.
        private int _qty;
        private int _adds;                         // adds FILLED — names PZ_ADD1..n
        private double _stopPx, _targetPx;         // the live aggregate bracket, tick-rounded
        private double _dayStartCum;               // realized CumProfit at the session open
        // Amendment 6, account-wide mode only. `_govKey` is this INSTANCE's identity in
        // the shared registry (instrument plus a random suffix, so two charts on the same
        // instrument are two contributors rather than one overwriting the other), and
        // `_acctSessionDay` is the day it publishes under. It moves in lockstep with
        // `_dayStartCum` — set together at the session open, cleared together in ResetAll
        // — which is what keeps an instance with no baseline out of the shared sum.
        private string _govKey;
        private DateTime _acctSessionDay = DateTime.MinValue;
        private bool _acctWideWarned;              // one non-realtime warning per pass, not per bar

        private int _entriesWindowStartSecs, _cutoffSecs;
        private DateTime _lastBarTime = DateTime.MinValue;
        private readonly HashSet<string> _drawTags = new HashSet<string>();
        // ponytail: grows by one interned string per drawn object for the life
        // of the run (a Playback rewind wipes it via ResetAll(true)); a long
        // multi-day Playback/live session could pile up thousands of tags.
        // Fine at that scale — add an eviction ceiling only if it measurably
        // becomes a memory problem.
        private int _patternSeq;

        // --- ATM mode (Amendment 5) -----------------------------------------
        // The ATM template owns the trade once it is created: NT8 manages its own
        // brackets, and an ATM position is invisible to Position/OnExecutionUpdate,
        // so the engine's state machine is driven by POLLING these ids each bar.
        // Empty id = no live ATM.
        private string _atmId = string.Empty, _atmOrderId = string.Empty;
        private bool _atmPending;                  // created, entry not filled yet
        private bool _atmInPosition;               // entry filled, ATM holding
        // GetAtmStrategyMarketPosition reads Flat for "not reflected yet", "closed"
        // and "unknown id" alike, so closure is only believed once the position has
        // actually been SEEN open (or the ATM reports realized PnL).
        private bool _atmSeenOpen;
        private bool _atmWaitPrinted;              // one "waiting to reflect" line per ATM, not per bar
        private double _atmDayRealized;            // ATM PnL this session — SystemPerformance never sees it
        private bool _atmBlocked;                  // config unusable: never trade
        private bool _atmUnusableWarned;

        // Seconds per bar on a TIME chart, 0 on every other bar type — the flag that
        // picks the bar-start rule in OnBarUpdate, so it is never clamped to a default.
        private int _barSecs = 60;
        private static readonly TimeSpan MaxBarSpan = TimeSpan.FromMinutes(30);
        private bool _rthOnlyWarned;

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
                // (_atrBars > AtrPeriod) so the recursion still sees every bar.
                BarsRequiredToTrade = 0;
                // RealtimeErrorHandling stays at NT8's default (StopCancelClose):
                // an order rejection cancels working orders, closes the position
                // and terminates the strategy — the platform is the outer net;
                // the strategy's own REJECTED->flatten branch is the inner one.
                // Deliberate: IgnoreAllErrors would require complete
                // self-managed rejection handling. Written out rather than left
                // implicit so a platform default change cannot move it silently.
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;

                // FROZEN DEFAULTS — spec section 10. The statistical dials were
                // fixed before any P&L was seen; changing one is a documented
                // amendment, not a tweak. Only the "06. Drawing" block is free.
                SwingStrength = 3;
                TopToleranceAtr = 0.30;
                HeadProminenceAtr = 0.30;
                MaxPatternBars = 60;
                NecklineBreakTicks = 2;
                MinPatternHeightAtr = 1.5;
                UseTrendFilter = true;
                TrendLookbackBars = 60;

                ZoneHalfWidthAtr = 0.50;
                ZoneProximityAtr = 0.50;
                UsePriorDayHL = true;
                UseOvernightHL = true;
                UsePriorClose = true;
                UseDayOpen = true;
                UseRound100 = true;
                UseRound50 = false;

                StopOffsetTicks = 10;
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
                // Amendment 6. Both new dials ship INERT: a live profit target would
                // truncate the pending Phase 3/4 validation runs, and account-wide is
                // off in LatigoBreak too. LatigoBreak ships a 500 target; PatternZone
                // does not, deliberately.
                DailyProfitTargetUsd = 0;
                DailyLossLimitUsd = 200;
                AccountWide = false;

                DrawZones = true;
                LongBrush = Brushes.MediumSeaGreen;
                ShortBrush = Brushes.IndianRed;
                AddonBrush = Brushes.Goldenrod;
                UseAtmStrategy = false;
                AtmTemplateName = "";

                PatternOpacityPct = 65;
                ZoneOpacityPct = 10;
                PatternLineWidth = 4;
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
                    UseTrendFilter = UseTrendFilter,
                    TrendLookbackBars = TrendLookbackBars,
                    ZoneHalfWidthAtr = ZoneHalfWidthAtr,
                    ZoneProximityAtr = ZoneProximityAtr,
                    UsePriorDayHL = UsePriorDayHL,
                    UseOvernightHL = UseOvernightHL,
                    UsePriorClose = UsePriorClose,
                    UseDayOpen = UseDayOpen,
                    UseRound100 = UseRound100,
                    UseRound50 = UseRound50,
                    StopOffsetTicks = StopOffsetTicks,
                    StopBufferAtr = StopBufferAtr,
                    TargetMultiple = TargetMultiple,
                    // Amendment 5: in ATM mode the template owns everything after
                    // the entry, so the engine must never emit an add. Forced here,
                    // shell-side — the core has no idea ATM exists.
                    EnableFlagAddon = EnableFlagAddon && !UseAtmStrategy,
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
                // Amendment 4: every bar type is supported. Minute and Second are
                // the only time types classified above, and both always yield a
                // positive value, so 0 means exactly "not a time chart".
                if (_barSecs != 60)
                    Log(Name + ": primary series is " + BarsPeriod.Value + " " + BarsPeriod.BarsPeriodType
                        + " — supported, but every bar-count dial (trend lookback, max pattern span, pole/flag budgets) counts THIS chart's bars and the ATR scales to them, so the dials mean something different here. The validated baseline is 1 Minute.",
                        Cbi.LogLevel.Warning);

                // Amendment 5. A user who ticked ATM on chose it deliberately, so a
                // broken template NEVER silently falls back to the managed path —
                // it blocks trading outright.
                if (UseAtmStrategy)
                {
                    string tpl = (AtmTemplateName ?? string.Empty).Trim();
                    _atmBlocked = tpl.Length == 0 || !File.Exists(Path.Combine(AtmTemplateDir, tpl + ".xml"));
                    if (_atmBlocked)
                        Log(Name + ": ATM mode is ON but the template \"" + tpl + "\" is empty or not found in "
                            + AtmTemplateDir + " — NO trading. Pick a template from the dropdown (it lists the ATM templates saved on this machine).",
                            Cbi.LogLevel.Error);
                    else
                        Log(Name + ": ATM mode ON, template \"" + tpl + "\". The template now OWNS the trade after entry — it supplies the stop, the target AND the position size, so Stop offset, Stop buffer, Target multiple and Contracts are all ignored, and the flag add-on is DISABLED. PatternZone still decides the entry, the trading window flatten and the daily-loss lockout.",
                            Cbi.LogLevel.Information);
                }

                // EntriesPerDirection is baked at SetDefaults and cannot see the
                // user's MaxAdds. Range(0, 5) blocks this from the UI; an XML
                // template or an optimizer file can still get past it.
                if (MaxAdds > 5)
                    Log(Name + ": MaxAdds > 5 exceeds EntriesPerDirection (1 + 5) — adds beyond the 6th tranche will be refused by the order layer.",
                        Cbi.LogLevel.Warning);

                // Amendment 6. Instrument first for a readable breach log, random suffix
                // so two instances on the SAME instrument and account are two
                // contributors rather than one silently overwriting the other.
                _govKey = Instrument.FullName + "/" + Guid.NewGuid().ToString("N").Substring(0, 4);
                if (AccountWide)
                    Log(Name + ": account-wide daily limits are ON — the limits are measured against the SUM of every PATTERNZONE instance on this account, and a breach on any one of them flattens and locks out all of them. It does NOT include other strategies (LatigoBreak, TBStrategy) or manual trades: the sum is built from these instances' own numbers, never from the account's aggregates.",
                        Cbi.LogLevel.Information);

                ResetAll(false);
            }
            else if (State == State.Terminated)
            {
                // Amendment 6, C1. `_govKey` gets a FRESH guid at every DataLoaded, so
                // without this every disable/re-enable, parameter change or reconnect
                // would strand the dead key's last published P&L in the shared record
                // while the reloaded instance publishes under the new one — the group
                // would double-count a phantom that no longer trades. Terminated is the
                // teardown NT8 guarantees for all of those. The record and its Breached
                // flag survive: only this contributor leaves.
                if (Account != null && !string.IsNullOrEmpty(_govKey))
                    PatternZoneShell.DailyGovernor.Drop(Account.Name, _govKey);
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
                _patternSeq = 0;
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
            _atrBars = 0;
            _lockout = false;
            _rthOnlyWarned = false;
            _acctSessionDay = DateTime.MinValue;        // lockstep with _dayStartCum below
            _acctWideWarned = false;
            // Amendment 6. A Playback rewind moves this instance BACK to a day the shared
            // registry has already passed, and DailyGovernor.For refuses an older day —
            // which would leave account-wide mode silently inert for the rest of the run.
            // Dropping the record lets the group re-form on the rewound day. ONLY on a
            // rewind: doing this at DataLoaded too would mean a strategy restart wipes a
            // live breach broadcast, i.e. a daily limit you can clear with a checkbox.
            // AccountWide is in the test on purpose: an opted-OUT instance rewinding must
            // not wipe the record the opted-in ones are sharing.
            if (removeDrawings && AccountWide && Account != null)
                PatternZoneShell.DailyGovernor.Forget(Account.Name);

            // Order state. A rewind discards the pass that owned these; anything
            // that pass left working reappears as an execution with nothing
            // behind it, which OnExecutionUpdate flattens on sight.
            // A rewind discards the pass that owned the ATM. It keeps running in NT8
            // with nobody watching it, so say so and try to close it before letting go.
            if (_atmId.Length > 0)
            {
                Log(Name + ": a live ATM (" + _atmId + ") was orphaned by a reset — attempting to close it. Check the Orders tab.",
                    Cbi.LogLevel.Warning);
                CloseAtm("orphaned by reset");
            }
            ClearAtm();
            _atmDayRealized = 0;
            _atmUnusableWarned = false;

            _pendingAction = null; _pendingAdd = null;
            _entrySig = null; _addSig = null;
            _entryPending = false; _addPending = false; _flattenPending = false;
            _entryOrder = null; _stopOrder = null; _targetOrder = null;
            _stopCancelAt = DateTime.MinValue; _targetCancelAt = DateTime.MinValue;
            _inTrade = false;
            _dir = 0; _qty = 0; _adds = 0;
            _stopPx = 0; _targetPx = 0;
            // NaN until the first session open. Every comparison against NaN is
            // false, so the daily-loss guard is inert until it is armed — which
            // is correct: no entry can fire before a session open either.
            _dayStartCum = double.NaN;
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
            _atrBars++;

            // NT8 stamps a bar at its CLOSE, so every session test below is on
            // the bar's START. Taking the start as a DateTime rather than
            // subtracting 60 from the seconds-of-day keeps the DATE right too:
            // the bar stamped 00:00:00 belongs to the previous evening, and
            // keying the overnight accumulators off its close date would reset
            // them at midnight, halfway through the session they measure.
            // Time charts: the stamp arithmetic is exact even across missing bars.
            // (NT8 skips a minute with no trades, so Time[1] is NOT this bar's start
            // there — that gap is what makes the prev-close rule wrong for them.)
            // Everything else has no duration to subtract, so a bar starts when the
            // previous one closed, with a cap for the session gap and halts.
            DateTime barStart = _barSecs > 0
                ? t.AddSeconds(-_barSecs)
                : (CurrentBar > 0 && t - Time[1] <= MaxBarSpan ? Time[1] : t.AddSeconds(-1));
            int startSecs = (int)barStart.TimeOfDay.TotalSeconds;
            bool inRth = startSecs >= RthOpenSecs && startSecs < RthCloseSecs;
            bool inOn = !inRth && (startSecs >= OnOpenSecs || startSecs < RthOpenSecs);

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

                    // Inverse of PullbackZone's _ethWarned, checked at the moment
                    // it matters: a session opening with no overnight range means
                    // the overnight bars are not on this chart. Testing for a
                    // non-RTH bar instead would be silenced by NT8's own "CME US
                    // Index Futures RTH" template, which runs to 16:15. The
                    // prior-close term skips the legitimately-empty first loaded
                    // session; anything else that eats the overnight range trips
                    // this too, which is the point.
                    if (!_rthOnlyWarned && !double.IsNaN(_prevRthClose) && double.IsNaN(_onHigh))
                    {
                        _rthOnlyWarned = true;
                        Log(Name + ": a full RTH session opened with no overnight bars — the chart's session template looks RTH-only; OvernightHigh/Low will stay unavailable and the zone engine runs on fewer levels. Use the instrument's full ETH template.",
                            Cbi.LogLevel.Warning);
                    }

                    _engine.OnSessionOpen(new SessionLevels
                    {
                        PriorDayHigh = _prevRthHigh,
                        PriorDayLow = _prevRthLow,
                        PriorClose = _prevRthClose,
                        OvernightHigh = _onHigh,
                        OvernightLow = _onLow,
                        DayOpen = Open[0],                 // this first RTH bar's open
                    });
                    DrawZonesForSession(barStart, _engine.Levels);

                    _curRthDate = barStart.Date;
                    _lockout = false;                      // the daily lockout lasts until the next session
                    // Baseline for the realized daily loss. Snapshotted here and
                    // nowhere else, so the limit measures THIS session.
                    _dayStartCum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                    _atmDayRealized = 0;                   // the ATM half of the same baseline
                    // Amendment 6: the shared registry's trading day, set with the
                    // baseline it belongs to. A new day means a new record — every
                    // instance's contribution AND the breach broadcast reset together.
                    _acctSessionDay = barStart.Date;
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
            bool warm = _atrBars > AtrPeriod && _atr.IsReady;

            // --- order management, BEFORE the engine call ------------------
            // A position and a lockout both outlive the pattern that created
            // them, and a lockout decided here has to gate THIS bar's actions.
            bool flat = Position.MarketPosition == MarketPosition.Flat;
            // Self-healing latch. Every flatten path gates on !_flattenPending,
            // so a flatten whose exit never reached the went-flat bookkeeping (an
            // unattributable fill, a rewind) would otherwise silence the cutoff
            // backstop for the rest of the run. Flat means nothing is pending.
            if (flat)
                _flattenPending = false;
            // Second look at the daily limits: SystemPerformance books a trade when
            // the position closes, and if that booking lands after the execution
            // event that closed it, this is what catches it — a bar late, but
            // before any new entry, because it runs ahead of `canTrade`.
            CheckDailyLimits();
            CheckBracketCancels(t);
            // ATM mode: the template's fills reach no handler of ours, so this poll
            // IS the engine's state machine. Runs before the flatten arms below so a
            // trade that just closed is already known to be flat.
            PollAtm();
            if (_atmId.Length > 0 && (_lockout || startSecs >= _cutoffSecs))
                CloseAtm(_lockout ? "daily limit lockout" : "trading window closed");
            // Went-flat retry net, same philosophy as the flatten retry below:
            // the bar loop is where anything the event handlers missed gets a
            // second look. OnExecutionUpdate's teardown is gated on Position
            // reading Flat in-stack, and a closing fill can arrive before it
            // does; left undone, `_inTrade` stays true and the engine stays
            // InPosition for the rest of the run, rejecting every later pattern
            // as "busy". The two pending gates keep this off a working
            // entry/add whose fill is still in the air.
            if (flat && _inTrade && !_entryPending && !_addPending)
                WentFlat();
            // One Exit call is not guaranteed to fill. This test runs on every
            // bar, so it IS the retry loop — for the cutoff, the lockout, and the
            // orphan flatten. `!_inTrade` is the orphan arm: holding contracts we
            // have no tracked trade for. The orphan flatten in OnExecutionUpdate
            // decides from Position.MarketPosition, which may not yet include the
            // fill that triggered it — if it no-ops there, this catches it a bar
            // later instead of letting real contracts ride unprotected. It cannot
            // false-trigger on a working entry: that leaves Position flat, so
            // `!flat` is false, and on the submission bar this runs before
            // SubmitEntry anyway.
            if ((_lockout || startSecs >= _cutoffSecs || !_inTrade) && !flat && !_flattenPending)
                FlattenNow();

            // inRth as well as inWindow: TradingStartHhmm can be set before the
            // open, and the six permission levels are all previous-session
            // aggregates — at 03:00 they describe a session that has not started.
            // ATM mode with a broken template or outside realtime takes NO trades and
            // never falls back to the managed path — the user chose ATM deliberately.
            // Folded into canTrade so the engine stays silent rather than arming
            // patterns it cannot act on. (AtmStrategyCreate is ignored on historical
            // data, which is the whole Strategy Analyzer.)
            bool atmUnusable = UseAtmStrategy && (_atmBlocked || State != State.Realtime);
            if (atmUnusable && !_atmUnusableWarned)
            {
                _atmUnusableWarned = true;
                Log(Name + ": ATM mode takes no trades here — " + (_atmBlocked
                        ? "the template is missing or unset."
                        : "ATM strategies never run on historical data. On a live chart this is just the warmup over loaded bars and trading starts when it reaches realtime; in the Strategy Analyzer it means the whole run."),
                    Cbi.LogLevel.Warning);
            }
            // Amendment 6. The shared record is keyed by account NAME, and outside
            // realtime that name is a virtual account every run shares — so in the
            // Analyzer an optimization would pool unrelated iterations into ONE governor
            // and let one iteration's breach lock out the others, silently. Same shape as
            // the ATM warning above, including the "this is just the warmup" wording, so
            // a live chart's historical pass does not read as an alarm.
            if (AccountWide && State != State.Realtime && !_acctWideWarned)
            {
                _acctWideWarned = true;
                Log(Name + ": account-wide daily limits are ON outside realtime. On a live chart this is just the warmup over loaded bars and the shared governor starts meaning something when the chart reaches realtime. In the Strategy Analyzer the account is virtual and shared by every iteration of the run, so an optimization pools unrelated iterations into one governor — leave account-wide OFF for backtests.",
                    Cbi.LogLevel.Warning);
            }
            bool canTrade = !_lockout && inWindow && inRth && warm && !atmUnusable;
            List<PzAction> actions = _engine.OnBarClosed(bar, _atr.Value, canTrade);

            foreach (PzAction a in actions)
            {
                switch (a.Type)
                {
                    case PzActionType.EnterLong:
                    case PzActionType.EnterShort:
                        SubmitEntry(a);
                        break;
                    case PzActionType.AddLong:
                    case PzActionType.AddShort:
                        SubmitAdd(a);
                        break;
                    default:
                        HandleDraw(a);                     // Task 10
                        break;
                }
            }
        }

        // --- order layer -----------------------------------------------------
        //
        // Every Enter*/Exit* call below is preceded by the tracker mutations the
        // handlers read: NT8 can deliver the fill in-stack, before the submitting
        // call returns. See the file header.

        private static string SigFor(PatternKind k)
        {
            switch (k)
            {
                case PatternKind.DoubleTop:     return "PZ_DT";
                case PatternKind.DoubleBottom:  return "PZ_DB";
                case PatternKind.TripleTop:     return "PZ_TT";
                case PatternKind.TripleBottom:  return "PZ_TB";
                case PatternKind.HeadShoulders: return "PZ_HS";
                case PatternKind.InverseHeadShoulders: return "PZ_IHS";
                // A kind added to the core without a signal here must not fall
                // through to some other pattern's name: the handlers attribute
                // fills BY name, so a silent default would mis-book a real trade.
                default: throw new ArgumentOutOfRangeException("k", k, "unmapped pattern kind");
            }
        }

        private static bool IsEntrySig(string n)
        {
            return n == "PZ_DT" || n == "PZ_DB" || n == "PZ_TT"
                || n == "PZ_TB" || n == "PZ_HS" || n == "PZ_IHS";
        }

        private static bool IsAddSig(string n)
        {
            return n != null && n.StartsWith("PZ_ADD", StringComparison.Ordinal);
        }

        // Where NT8 keeps ATM strategy templates — the dropdown reads this folder.
        internal static string AtmTemplateDir
        {
            get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "AtmStrategy"); }
        }

        // --- ATM mode (Amendment 5) -----------------------------------------
        // The template owns the trade once created, so this path submits the entry
        // and then only WATCHES. Same race discipline as the managed path: every id
        // and flag is written BEFORE AtmStrategyCreate, because the callback runs on
        // the UI thread and can land before the call returns.
        private void SubmitAtmEntry(PzAction a)
        {
            if (_atmBlocked || State != State.Realtime || _atmId.Length > 0)
            {
                Print(Name + ": ATM entry refused — blocked config, not realtime, or one is already live.");
                _engine.OnEntryFailed();
                return;
            }

            string id = GetAtmStrategyUniqueId();
            string orderId = GetAtmStrategyUniqueId();
            _atmId = id;                                   // all BEFORE the create
            _atmOrderId = orderId;
            _atmPending = true;
            _atmInPosition = false;

            AtmStrategyCreate(
                a.Type == PzActionType.EnterLong ? OrderAction.Buy : OrderAction.SellShort,
                OrderType.Market, 0, 0, TimeInForce.Day,
                orderId, AtmTemplateName.Trim(), id,
                // The callback runs on the UI thread and may land before this call
                // returns, so it touches only the ATM trio and the engine — and it
                // may assume the trio is still ITS trade because the guard above
                // refuses a second ATM while one is live.
                (errorCode, callbackId) =>
                {
                    if (callbackId != id)                  // id-gated, like every clear in this file
                        return;
                    if (errorCode == ErrorCode.NoError)
                        return;
                    Print(Name + ": ATM create FAILED (" + errorCode + ") — releasing the gate.");
                    ClearAtm();
                    _engine.OnEntryFailed();
                });

            Print(string.Format(CultureInfo.InvariantCulture,
                "{0} {1:yyyy-MM-dd HH:mm} ATM ENTRY {2} template={3} atmId={4}",
                Name, Time[0], a.Pattern != null ? a.Pattern.Kind.ToString() : "?", AtmTemplateName.Trim(), id));
        }

        // An ATM position is invisible to Position and fires none of our handlers,
        // so the engine's state machine is driven from here, once per closed bar.
        // Every getter is wrapped: they throw once the id is no longer known to NT8.
        private void PollAtm()
        {
            if (_atmId.Length == 0 || State != State.Realtime)
                return;

            if (_atmPending)
            {
                string[] st = null;
                try { st = GetAtmStrategyEntryOrderStatus(_atmOrderId); } catch { }
                if (st != null && st.Length > 2)
                {
                    double fill;
                    double.TryParse(st[0], NumberStyles.Any, CultureInfo.InvariantCulture, out fill);
                    double filledQty;
                    double.TryParse(st[1], NumberStyles.Any, CultureInfo.InvariantCulture, out filledQty);
                    bool terminal = st[2] == "Cancelled" || st[2] == "Rejected";

                    // A terminal entry that filled ANY quantity left a position behind:
                    // AtmStrategyCreate takes no quantity (the template sets it), so a
                    // multi-lot template makes partial fills reachable. Treating that as
                    // a failure would drop a live position out of the flatten and lockout
                    // paths. Only a zero-fill terminal state is a real failure.
                    if (st[2] == "Filled" || (terminal && filledQty > 0))
                    {
                        _atmPending = false;
                        _atmInPosition = true;
                        Print(Name + ": ATM entry " + (terminal ? st[2] + " after a PARTIAL fill of " + filledQty + " " : "filled ")
                            + "at " + fill.ToString("0.##", CultureInfo.InvariantCulture));
                        _engine.OnEntryFilled(fill);
                        // NT8 does not reflect the position until at least the next
                        // OnBarUpdate, so the closure test below must not run on the
                        // pass that saw the fill — it would read Flat and forget a live ATM.
                        return;
                    }
                    if (terminal)
                    {
                        Print(Name + ": ATM entry " + st[2] + " unfilled — back to flat.");
                        ClearAtm();
                        _engine.OnEntryFailed();
                        return;
                    }
                }
            }

            if (!_atmInPosition)
                return;

            MarketPosition mp = MarketPosition.Flat;
            try { mp = GetAtmStrategyMarketPosition(_atmId); } catch { }
            if (mp != MarketPosition.Flat)
            {
                _atmSeenOpen = true;                   // the position is reflected now
                return;
            }

            // Flat here is ambiguous: NT8 returns Flat for "not reflected yet",
            // "closed" and "unknown id" alike. Only trust it once the position was
            // actually seen open, or once the ATM reports realized PnL — otherwise
            // wait another bar. Erring this way keeps a live ATM tracked; the other
            // way loses it.
            double pnl = 0;
            try { pnl = GetAtmStrategyRealizedProfitLoss(_atmId); } catch { }
            if (!_atmSeenOpen && pnl == 0)
            {
                if (!_atmWaitPrinted)                  // first waiting bar only — a stall must be visible, not spam
                {
                    _atmWaitPrinted = true;
                    Print(Name + ": waiting for the ATM position to reflect (id " + _atmId + ").");
                }
                return;
            }
            _atmDayRealized += pnl;
            Print(string.Format(CultureInfo.InvariantCulture,
                "{0} ATM trade closed, realized {1:0.##} USD | session ATM total {2:0.##}",
                Name, pnl, _atmDayRealized));
            ClearAtm();
            _engine.OnPositionClosed();
            CheckDailyLimits();
        }

        private void ClearAtm()
        {
            _atmId = string.Empty;
            _atmOrderId = string.Empty;
            _atmPending = false;
            _atmInPosition = false;
            _atmSeenOpen = false;
            _atmWaitPrinted = false;
        }

        // The strategy-level risk rules still bind in ATM mode. AtmStrategyClose
        // flattens the position AND cancels the template's own stop/target; called
        // every bar until the poll above sees flat, same retry shape as FlattenNow.
        private void CloseAtm(string why)
        {
            if (_atmId.Length == 0 || State != State.Realtime)
                return;
            Print(Name + ": closing ATM (" + why + ").");
            try { AtmStrategyClose(_atmId); } catch { }
        }

        private void SubmitEntry(PzAction a)
        {
            if (UseAtmStrategy)
            {
                SubmitAtmEntry(a);
                return;
            }
            // The engine has ALREADY moved to AwaitingEntryFill by emitting this
            // action. Refusing without telling it would strand it there for the
            // rest of the run, so every refusal path below reports the failure.
            if (a.Pattern == null || Position.MarketPosition != MarketPosition.Flat || _entryPending)
            {
                Print(Name + ": entry refused — no pattern, not flat, or one is already working.");
                _engine.OnEntryFailed();
                return;
            }

            string sig = SigFor(a.Pattern.Kind);
            _pendingAction = a;                            // all BEFORE the submit
            _entrySig = sig;
            // Cleared here so an in-stack teardown during the submit below cannot
            // cancel the PREVIOUS trade's (already terminal) entry order.
            _entryOrder = null;
            _entryPending = true;
            Order o = a.Type == PzActionType.EnterLong
                ? EnterLong(0, Contracts, sig)
                : EnterShort(0, Contracts, sig);
            if (o == null)
            {
                // NT8's internal order handling ignored the submission. Nothing
                // is working, so releasing the gate is the whole point: left
                // armed, `_entryPending` would silently end the run's trading.
                _entryPending = false;
                _pendingAction = null;
                _engine.OnEntryFailed();
                Print(Name + ": entry submission returned no order — gate released.");
                return;
            }
            // AFTER the call, deliberately: an in-stack fill has already run the
            // handlers by now. Only ever read to cancel a partial's remainder.
            _entryOrder = o;
            // A submission rejected in-stack already released the gate and told
            // the engine; announcing an entry that no longer exists would put a
            // phantom trade in the log. `o.OrderState`, not `_entryPending`: an
            // in-stack FILL clears that flag too, which would otherwise swallow
            // the very line Replay debugging needs.
            if (o.OrderState != OrderState.Rejected)
                Print(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:yyyy-MM-dd HH:mm} ENTRY {2} {3} x{4} stop={5:0.##} tgt={6:0.##}",
                    Name, Time[0], sig, a.Pattern.Kind, Contracts, a.StopPrice, a.TargetPrice));
        }

        private void SubmitAdd(PzAction a)
        {
            // Same contract as SubmitEntry: the engine is in AwaitingAddFill and
            // only OnAddFilled/OnAddFailed can move it out.
            if (!_inTrade || Position.MarketPosition == MarketPosition.Flat || _addPending)
            {
                Print(Name + ": add refused — no tracked position, or one is already working.");
                _engine.OnAddFailed();
                return;
            }

            string sig = "PZ_ADD" + (_adds + 1).ToString(CultureInfo.InvariantCulture);
            _pendingAdd = a;                               // all three BEFORE the submit
            _addSig = sig;
            _addPending = true;
            Order o = a.Type == PzActionType.AddLong
                ? EnterLong(0, Contracts, sig)
                : EnterShort(0, Contracts, sig);
            if (o == null)
            {
                _addPending = false;
                _pendingAdd = null;
                _engine.OnAddFailed();
                Print(Name + ": add submission returned no order — gate released.");
                return;
            }
            if (o.OrderState != OrderState.Rejected)       // see the ENTRY line above
                Print(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:yyyy-MM-dd HH:mm} ADD {2} x{3} newstop={4:0.##}",
                    Name, Time[0], sig, Contracts, a.StopPrice));
        }

        // BOTH legs, always together, and never before the trackers they read.
        // Under OCO a stop cancel-replace kills the target leg too, so a resubmit
        // that touched only the stop would silently leave the position without a
        // target. `fromEntrySignal = ""` is what makes ONE stop and ONE target
        // cover every tranche instead of one pair per entry signal.
        private void SubmitExits()
        {
            // EVERYTHING is read into locals first. The first submit can be
            // immediately marketable and re-enter this handler chain in-stack:
            // a stop that fills on submission runs the went-flat teardown, which
            // zeroes _qty/_stopPx/_targetPx, and the second leg would then go out
            // as a 0-quantity order at price 0 — an invalid-parameter error NT8
            // can disable the strategy for.
            int q = _qty, d = _dir;
            double sp = _stopPx, tp = _targetPx;
            // Only the quantity is guarded, and only against the not-yet-filled
            // case. A stop-price term here would be a way to submit NOTHING and
            // hold the position silently unprotected. `tp <= 0` is different: it
            // is the hand-cancelled-target flag, and skipping only that leg is
            // the point — the stop still goes out.
            if (q <= 0)
                return;
            // Nulled BEFORE the submits so the replaced orders' in-stack
            // Cancelled echoes cannot match the current refs in OnOrderUpdate.
            _stopOrder = null; _targetOrder = null;
            if (d > 0)
            {
                _stopOrder = ExitLongStopMarket(0, true, q, sp, SigStop, "");
                if (tp > 0)
                    _targetOrder = ExitLongLimit(0, true, q, tp, SigTarget, "");
            }
            else
            {
                _stopOrder = ExitShortStopMarket(0, true, q, sp, SigStop, "");
                if (tp > 0)
                    _targetOrder = ExitShortLimit(0, true, q, tp, SigTarget, "");
            }
        }

        // Deferred hand-cancel detector. A bracket Cancelled event only counts as
        // "by hand" if the position is STILL open a bar later: our own
        // cancel-replaces are filtered by reference in OnOrderUpdate, and a
        // closing fill's OCO cancel is wiped by the went-flat teardown first.
        //
        // DECISION: a hand-cancel is RESPECTED, not re-asserted. Dragging or
        // pulling a bracket in Chart Trader is a deliberate manual override, and
        // a strategy that silently puts it back is fighting its operator. The
        // cost is that the position can run unprotected, so it is announced
        // loudly rather than swallowed.
        private void CheckBracketCancels(DateTime t)
        {
            if (_stopCancelAt != DateTime.MinValue && (t - _stopCancelAt).TotalSeconds >= 1)
            {
                _stopCancelAt = DateTime.MinValue;
                Print(Name + ": " + SigStop + " cancelled by hand — the position is UNPROTECTED on that side.");
            }
            if (_targetCancelAt != DateTime.MinValue && (t - _targetCancelAt).TotalSeconds >= 1)
            {
                _targetCancelAt = DateTime.MinValue;
                _targetPx = 0;                             // respect it: no add resize may resurrect the target
                Print(Name + ": " + SigTarget + " cancelled by hand — take profit removed for this trade.");
            }
        }

        // PREMISE: SINGLE INSTRUMENT. `Position` here — and every other
        // handler-side Position read — is the primary series' position only
        // because there is one instrument on this strategy.
        //
        // Explicit barsInProgressIndex: this is reached from OnExecutionUpdate
        // too, where BarsInProgress is non-deterministic. The trailing "" is
        // fromEntrySignal, not a signal name; empty detaches the exit from any
        // single tranche so it closes the whole position.
        private void FlattenNow()
        {
            _flattenPending = true;                        // BEFORE the Exit*
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(0, Position.Quantity, SigFlatten, "");
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort(0, Position.Quantity, SigFlatten, "");
            else
                _flattenPending = false;
        }

        private void Lockout(string why)
        {
            if (_lockout)
                return;
            _lockout = true;
            Print(Name + ": " + why + " — locked out until the next session.");
            if (Position.MarketPosition != MarketPosition.Flat && !_flattenPending)
                FlattenNow();
        }

        // This instance's day P&L against this session's baseline. `_dayStartCum` is
        // NaN until the first session open; every comparison against NaN is false, so
        // the guard is inert until it is armed.
        //
        // Realized only when `includeOpen` is false — the basis the per-strategy limits
        // have always used. SystemPerformance never books an ATM trade, so the ATM total
        // is added in: in managed mode it is 0, in ATM mode the SystemPerformance term is.
        //
        // `includeOpen` adds what is still on the table and is used ONLY by the
        // account-wide sum (Amendment 6). That basis is deliberate, not an oversight:
        // a shared governor exists to close the account before an account-level
        // drawdown rule fires, prop firms measure open P&L, and a $1,200 loser sitting
        // open on another chart is exactly what the switch is bought for. LatigoBreak
        // measures the same way (LatigoBreakStrategy.cs:830-838).
        private double OwnDayPnl(bool includeOpen)
        {
            double pnl = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - _dayStartCum
                + _atmDayRealized;
            if (!includeOpen)
                return pnl;
            if (Position.MarketPosition != MarketPosition.Flat)
                return pnl + Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency);
            // An ATM position is invisible to `Position`, so ATM mode needs its own
            // getter or account-wide would price an open ATM trade at zero.
            if (_atmInPosition && _atmId.Length > 0)
            {
                try { return pnl + GetAtmStrategyUnrealizedProfitLoss(_atmId); }
                catch { }
            }
            return pnl;
        }

        // The shared record for this account and trading day, or null when this instance
        // must not take part: the switch is off, there is no account (some non-realtime
        // contexts), no session baseline exists yet, or the group has already moved past
        // this instance's day.
        private PatternZoneShell.DailyGovernor.AccountDay CurrentAccountDay()
        {
            if (!AccountWide || Account == null || _acctSessionDay == DateTime.MinValue)
                return null;
            return PatternZoneShell.DailyGovernor.For(Account.Name, _acctSessionDay);
        }

        // Amendment 6. TWO triggers, ONE lockout — the loss limit and the profit target
        // both end in the same Lockout() the loss limit has always used, so both inherit
        // its flatten, its ATM close and its "no entries until the next session".
        // `AccountWide` swaps only the number being measured: this instance's own day
        // P&L, or the SUM across every PatternZone publishing to this account's record.
        private void CheckDailyLimits()
        {
            if (_lockout)
                return;

            PatternZoneShell.DailyGovernor.AccountDay gov = CurrentAccountDay();
            if (gov != null && gov.Breached)
            {
                // Honored even with both of THIS instance's limits at 0: the group
                // breached, so this chart stops with it. That is the whole switch.
                Lockout("account-wide daily limit hit on another PatternZone");
                return;
            }

            double dayPnl;
            if (gov != null)
            {
                // Published BEFORE the limits-off return below, deliberately: an instance
                // with its own limits at 0 still has to count toward everyone else's sum.
                // Rollover seam: `For` above and `Publish` below are two separate locks,
                // so a publish can land in a record that another instance just replaced
                // at a day roll — that contribution is lost for one bar and republished
                // on the next, because nobody caches the record. Do NOT "optimize" the
                // per-tick re-fetch away; the re-fetch IS the self-healing.
                double own = OwnDayPnl(true);
                // A NaN contribution would make the group's SUM NaN, every comparison
                // against it false, and the governor would die silently for EVERY
                // instance. Fall back to REALIZED-ONLY rather than 0: a 0 would also
                // drop this instance's realized loss from the total, so an account-wide
                // loss limit would fire late — the unsafe direction. The ATM unrealized
                // getter is the reachable NaN source; a throw from it already lands on
                // realized-only, so both of its failure modes now degrade the same way.
                // Safe by construction: `gov` is non-null only once `_acctSessionDay` is
                // set, which is in lockstep with `_dayStartCum`, so the realized-only
                // figure cannot itself be NaN here.
                dayPnl = gov.Publish(_govKey, double.IsNaN(own) ? OwnDayPnl(false) : own);
            }
            else
                dayPnl = OwnDayPnl(false);

            if (DailyLossLimitUsd <= 0 && DailyProfitTargetUsd <= 0)
                return;
            bool hitTarget = DailyProfitTargetUsd > 0 && dayPnl >= DailyProfitTargetUsd;
            bool hitLoss = DailyLossLimitUsd > 0 && dayPnl <= -DailyLossLimitUsd;
            if (!hitTarget && !hitLoss)
                return;

            if (gov != null)
                gov.Breached = true;                   // broadcast: the rest lock out on their next bar
            Lockout("daily " + (hitTarget ? "profit target " : "loss ")
                + dayPnl.ToString("0", CultureInfo.InvariantCulture) + " USD"
                + (gov != null ? ", account-wide [" + gov.Breakdown() + "]" : ""));
        }

        // Chart-only feedback for WHY the strategy entered — semi-transparent
        // pattern/flag geometry. NO TEXT, ever (spec decision #6): only lines
        // and rectangles below.
        private void HandleDraw(PzAction a)
        {
            // Amendment 2. WHY a pattern was refused is invisible on the chart
            // by construction (no text, ever), which made the gauntlet
            // unauditable in Replay. The reason goes to the Output window
            // instead — ahead of the ChartControl guard, so a headless Analyzer
            // run can be audited too, and behind the same toggle so a real run
            // stays silent.
            // a.Pattern is null-guarded: the reserved "flag_no_position" reason
            // carries a flag, not a pattern, and no engine branch emits it today.
            if (a.Type == PzActionType.DrawRejected && DrawRejectedPatterns)
                Print(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:yyyy-MM-dd HH:mm} REJECTED {2} {3}",
                    Name, Time[0], a.RejectReason,
                    a.Pattern != null ? a.Pattern.Kind.ToString() : "flag"));

            if (ChartControl == null)              // headless Strategy Analyzer: never draw
                return;

            switch (a.Type)
            {
                case PzActionType.DrawPattern:
                    DrawPattern(a.Pattern, PatternOpacityPct);
                    break;
                case PzActionType.DrawRejected:
                    // Both direction slots can resolve on the same bar, so an
                    // entry and a rejection can land in the SAME action batch —
                    // handled independently, no exclusivity assumed here.
                    if (DrawRejectedPatterns)
                        DrawPattern(a.Pattern, PatternOpacityPct / 2);
                    break;
                case PzActionType.DrawFlag:
                    DrawFlag(a.Flag);
                    break;
            }
        }

        // One line per consecutive swing pair — the M / W / 5-point zigzag, and
        // nothing else. Same geometry for an accepted pattern and a rejected
        // one, just at half opacity.
        //
        // Amendment 1: the dashed neckline segment is GONE (Javier does not want
        // it on the chart). The engine still carries `NecklineAtBreak` on the
        // action as the trigger price of record; the drawing layer ignores it.
        private void DrawPattern(PatternCandidate p, int opacityPct)
        {
            int id = _patternSeq++;
            Brush brush = Alpha(p.IsShort ? ShortBrush : LongBrush, opacityPct);
            PzSwing[] sw = p.Swings;
            // Amendment 3: the legs, so a W reads as a W and not as a V with a
            // roof. Lead-in from the prior opposite swing; lead-out to THIS bar,
            // which is the break bar the action fired on.
            if (p.HasLeadIn)
                Draw.Line(this, Tag("PZ_P" + id + "_in"), false,
                    p.LeadInSwing.Time, p.LeadInSwing.Price, sw[0].Time, sw[0].Price,
                    brush, DashStyleHelper.Solid, PatternLineWidth);
            for (int i = 0; i < sw.Length - 1; i++)
                Draw.Line(this, Tag("PZ_P" + id + "_" + i), false,
                    sw[i].Time, sw[i].Price, sw[i + 1].Time, sw[i + 1].Price,
                    brush, DashStyleHelper.Solid, PatternLineWidth);
            Draw.Line(this, Tag("PZ_P" + id + "_out"), false,
                sw[sw.Length - 1].Time, sw[sw.Length - 1].Price, Time[0], Close[0],
                brush, DashStyleHelper.Solid, PatternLineWidth);
        }

        // Pole line + the two flag-envelope rails. One-shot: fired once, at
        // the add trigger bar, with the rails ending at Time[0] of that bar —
        // not a live redraw that tracks the flag as it builds.
        private void DrawFlag(FlagInfo f)
        {
            int id = _patternSeq++;
            Brush brush = Alpha(AddonBrush, PatternOpacityPct);
            Draw.Line(this, Tag("PZ_F" + id + "_pole"), false,
                f.PoleStartTime, f.PoleStartPrice, f.PoleEndTime, f.PoleEndPrice,
                brush, DashStyleHelper.Solid, PatternLineWidth);
            Draw.Line(this, Tag("PZ_F" + id + "_hi"), false,
                f.FlagStartTime, f.FlagHigh, Time[0], f.FlagHigh,
                brush, DashStyleHelper.Solid, PatternLineWidth);
            Draw.Line(this, Tag("PZ_F" + id + "_lo"), false,
                f.FlagStartTime, f.FlagLow, Time[0], f.FlagLow,
                brush, DashStyleHelper.Solid, PatternLineWidth);
        }

        // Session S/R bands: drawn ONCE per session, right after OnSessionOpen,
        // from the same levels the zone engine gates patterns against — fixed
        // SlateGray because zones are context, not a directional signal.
        private void DrawZonesForSession(DateTime sessionOpen, SessionLevels levels)
        {
            if (!DrawZones || ChartControl == null)
                return;
            double hw = ZoneHalfWidthAtr * _atr.Value;
            DateTime sessionEnd = sessionOpen.Date.AddSeconds(_cutoffSecs);
            string key = sessionOpen.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

            DrawZoneBand(key, "PDH", UsePriorDayHL, levels.PriorDayHigh, hw, sessionOpen, sessionEnd);
            DrawZoneBand(key, "PDL", UsePriorDayHL, levels.PriorDayLow, hw, sessionOpen, sessionEnd);
            DrawZoneBand(key, "ONH", UseOvernightHL, levels.OvernightHigh, hw, sessionOpen, sessionEnd);
            DrawZoneBand(key, "ONL", UseOvernightHL, levels.OvernightLow, hw, sessionOpen, sessionEnd);
            DrawZoneBand(key, "PC", UsePriorClose, levels.PriorClose, hw, sessionOpen, sessionEnd);
            DrawZoneBand(key, "OPEN", UseDayOpen, levels.DayOpen, hw, sessionOpen, sessionEnd);
        }

        private void DrawZoneBand(string key, string name, bool enabled, double level, double hw,
            DateTime start, DateTime end)
        {
            if (!enabled || double.IsNaN(level))
                return;
            // Outline gets the same opacity as the area: the areaOpacity argument
            // only fades the fill, so a raw brush here draws a full-strength
            // border around a 10%-opacity band.
            Draw.Rectangle(this, Tag("PZ_Z" + key + "_" + name), false,
                start, level + hw, end, level - hw,
                Alpha(Brushes.SlateGray, ZoneOpacityPct), Brushes.SlateGray, ZoneOpacityPct);
        }

        private Brush Alpha(Brush src, int pct)
        {
            var sc = src as SolidColorBrush;
            Color c = sc != null ? sc.Color : Colors.Gray;
            var b = new SolidColorBrush(Color.FromArgb((byte)(255 * pct / 100), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;
            Order o = execution.Order;
            string n = o.Name;
            bool isAdd = IsAddSig(n);

            if (isAdd || IsEntrySig(n))
            {
                // Anything that leaves us holding contracts counts: a full fill,
                // a partial fill, or a cancel after a partial.
                OrderState st = o.OrderState;
                if (st != OrderState.Filled && st != OrderState.PartFilled
                    && !(st == OrderState.Cancelled && o.Filled > 0))
                    return;

                // The frozen action is consumed on this order's FIRST execution;
                // a later partial of the same order only resizes the brackets.
                // `_entryPending` answers a different question — a PartFilled
                // order is NOT terminal, its remainder is still working, so that
                // gate stays shut until OnOrderUpdate reports the order dead.
                PzAction a = null;
                if (isAdd && n == _addSig)
                {
                    a = _pendingAdd;
                    _pendingAdd = null;
                    if (st != OrderState.PartFilled)
                        _addPending = false;               // name-gated clear
                }
                else if (!isAdd && n == _entrySig)
                {
                    a = _pendingAction;
                    _pendingAction = null;
                    if (st != OrderState.PartFilled)
                        _entryPending = false;             // name-gated clear
                }

                // A fill with nothing behind it: a rewound pass, a remainder
                // arriving after its position already closed, or an add whose
                // base is gone. Real contracts either way — never leave them
                // unbracketed, and never fall through to the went-flat branch,
                // which would latch _flattenPending on forever.
                if (!_inTrade && (isAdd || a == null))
                {
                    Print(Name + ": " + n + " execution with no tracked trade behind it — flattening.");
                    if (!_flattenPending)
                        FlattenNow();
                    // The engine may be awaiting a fill that will now never be
                    // reported as one; put it back to flat rather than strand it.
                    _engine.OnPositionClosed();
                    return;
                }

                _qty += quantity;                          // trackers BEFORE the submits that read them
                if (a != null)
                {
                    if (isAdd)
                    {
                        _adds++;
                        // The add action's StopPrice IS the new aggregate stop
                        // (the flag structure); the target is the pattern's and
                        // does not move — every tranche exits at one price.
                        _stopPx = Instrument.MasterInstrument.RoundToTickSize(a.StopPrice);
                    }
                    else
                    {
                        _inTrade = true;
                        _dir = a.Type == PzActionType.EnterLong ? 1 : -1;
                        _stopPx = Instrument.MasterInstrument.RoundToTickSize(a.StopPrice);
                        _targetPx = Instrument.MasterInstrument.RoundToTickSize(a.TargetPrice);
                    }
                }

                // PROTECTION FIRST — nothing goes between a live fill and its
                // stop. The lockout branch closes a fill that landed while the
                // lockout was already up, on the fill event itself: no brackets,
                // no extra bar of exposure.
                if (_lockout)
                {
                    if (!_flattenPending)
                        FlattenNow();
                }
                else
                    SubmitExits();

                // The engine hears about the fill only once the protection is out
                // — and only if we are STILL in the trade. The submits above can
                // re-enter this handler in-stack (a marketable target on a bar
                // that gapped through it, the lockout flatten) and run the
                // went-flat teardown, which already told the engine
                // OnPositionClosed. Reporting a fill on top of that would put the
                // engine back InPosition while the strategy is flat, and nothing
                // resets that state — not even OnSessionOpen — so every later
                // pattern break would reject as "busy" for the rest of the run.
                if (a != null && _inTrade)
                {
                    if (isAdd)
                        _engine.OnAddFilled(price);
                    else
                        _engine.OnEntryFilled(price);
                }
                return;
            }

            // Went flat, by any exit: our brackets, our flatten, or NT8's own
            // "Exit on session close". Gated on `_inTrade` so a stale exit around
            // a Playback rewind books nothing against the fresh pass.
            if (!_inTrade || Position.MarketPosition != MarketPosition.Flat)
                return;

            WentFlat();
        }

        // Everything a closed trade has to release, in one place because it has
        // two callers: OnExecutionUpdate's closing execution (the normal path)
        // and OnBarUpdate's retry arm, for the case where Position had not yet
        // read Flat in-stack when that execution arrived.
        private void WentFlat()
        {
            _inTrade = false;
            _flattenPending = false;
            _qty = 0;
            _adds = 0;
            _dir = 0;
            _stopPx = 0; _targetPx = 0;
            _stopOrder = null; _targetOrder = null;
            // A closing fill's OCO cancel of the other leg must not be mistaken
            // for a hand-cancel one bar from now.
            _stopCancelAt = DateTime.MinValue; _targetCancelAt = DateTime.MinValue;
            // An add still working has nothing left to add to. It is a market
            // order so it has almost certainly filled already; if it fills after
            // this, the orphan net in OnExecutionUpdate flattens it.
            _addPending = false;
            _pendingAdd = null;

            // A PartFilled ENTRY's remainder is a different problem: it is still
            // working and would open a fresh naked position minutes later, alone,
            // against a trade that is over. Kill it here (PullbackZone's
            // CancelEntry("position_closed")) rather than let the orphan net clean
            // up after a fill that should never have happened. `_entryPending` is
            // exactly "the order is not terminal", so it is the right guard — and
            // it is still true here only in that partial-remainder case.
            if (_entryPending && _entryOrder != null)
                CancelOrder(_entryOrder);
            // Cleared AFTER the cancel and unconditionally: leaving the trio set
            // is what would wedge SubmitEntry's `!_entryPending` guard shut for
            // the rest of the run. Dropping `_entrySig` also shuts the window
            // where a late remainder could be attributed to a LATER trade that
            // happens to share a pattern kind.
            _entryPending = false;
            _pendingAction = null;
            _entrySig = null;
            _entryOrder = null;

            _engine.OnPositionClosed();
            CheckDailyLimits();
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (order == null)
                return;
            string n = order.Name;

            if (n == SigFlatten)
            {
                if (orderState == OrderState.Rejected || orderState == OrderState.Cancelled)
                    _flattenPending = false;               // the bar loop retries
                return;
            }

            if (n == SigStop || n == SigTarget)
            {
                // REJECTED IS TESTED FIRST, AHEAD OF THE REFERENCE FILTER BELOW.
                // Rejected is never an echo: our cancel-replaces echo as
                // Cancelled, never Rejected, so a name-matched Rejected bracket
                // event is always a real leg dying on a live position. Filtering
                // by reference first would swallow it in the exact case that
                // matters — SubmitExits nulls both refs BEFORE it submits, so a
                // new aggregate stop rejected in-stack arrives while they are
                // still null, and the position would sit unprotected with not
                // even a print to show for it.
                if (orderState == OrderState.Rejected)
                {
                    if (Position.MarketPosition != MarketPosition.Flat && !_flattenPending)
                    {
                        Print(Name + ": " + n + " REJECTED (" + error + ") — position unprotected, flattening.");
                        FlattenNow();
                    }
                    return;
                }
                // Events for orders that are not the CURRENT references are
                // echoes of our own cancel-replaces on an add resize — ignored
                // wholesale. The time-based detector below cannot do this job on
                // its own: a resize cancel and its replacement happen at the same
                // instant with the position still open a bar later, so it would
                // read every add as a hand-cancel and pull the target.
                if (!ReferenceEquals(order, _stopOrder) && !ReferenceEquals(order, _targetOrder))
                    return;
                // Stamp only; CheckBracketCancels decides a bar later, once a
                // closing fill has had the chance to wipe it.
                if (orderState == OrderState.Cancelled
                    && Position.MarketPosition != MarketPosition.Flat
                    && !_flattenPending && !_lockout)
                {
                    if (n == SigStop) _stopCancelAt = time;
                    else _targetCancelAt = time;
                }
                return;
            }

            if (orderState != OrderState.Rejected && orderState != OrderState.Cancelled)
                return;

            // The order is dead, so its gate is released either way. Whether the
            // ENGINE hears "failed" depends on whether anything filled: a cancel
            // after a partial already reported OnEntryFilled/OnAddFilled from the
            // execution handler, and a "failed" on top of that would drop the
            // engine out of a position it is actually in.
            if (IsAddSig(n) && n == _addSig && _addPending)
            {
                _addPending = false;
                if (filled == 0)
                {
                    _pendingAdd = null;
                    _engine.OnAddFailed();
                }
            }
            else if (IsEntrySig(n) && n == _entrySig && _entryPending)
            {
                _entryPending = false;
                if (filled == 0)
                {
                    _pendingAction = null;
                    _engine.OnEntryFailed();
                }
            }
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

        [NinjaScriptProperty]
        [Display(Name = "Require a prior trend", Description = "A reversal pattern must have something to reverse: its FIRST defining extreme must be the extreme of the lookback window behind it (tops only after an up-leg, bottoms only after a down-leg).", GroupName = "01. Detection", Order = 6)]
        public bool UseTrendFilter { get; set; }

        [NinjaScriptProperty, Range(10, 500)]
        [Display(Name = "Trend lookback (bars)", Description = "How far back the prior-trend window reaches from the pattern's first defining extreme. Shorter history than this is not a rejection — the window clamps.", GroupName = "01. Detection", Order = 7)]
        public int TrendLookbackBars { get; set; }

        [NinjaScriptProperty, Range(0.1, 2.0)]
        [Display(Name = "Zone half-width (x ATR)", Description = "Half-width of the band around a level, and the band DRAWN on the chart. A pattern's extremes must cluster inside one band (plus the proximity allowance below) for the pattern to be permitted.", GroupName = "02. Zones", Order = 0)]
        public double ZoneHalfWidthAtr { get; set; }

        [NinjaScriptProperty, Range(0.0, 2.0)]
        [Display(Name = "Zone proximity (x ATR)", Description = "Extra ATR margin beyond the zone band for pattern permission — a pattern forming NEAR a level still qualifies. Not drawn: permission reaches further than the band on the chart. 0 = strict band.", GroupName = "02. Zones", Order = 1)]
        public double ZoneProximityAtr { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Prior day high/low", GroupName = "02. Zones", Order = 2)]
        public bool UsePriorDayHL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Overnight high/low", Description = "18:00-09:30 ET extremes. Requires an ETH session template — on an RTH chart these levels never exist.", GroupName = "02. Zones", Order = 3)]
        public bool UseOvernightHL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Prior RTH close", GroupName = "02. Zones", Order = 4)]
        public bool UsePriorClose { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Day open (09:30)", GroupName = "02. Zones", Order = 5)]
        public bool UseDayOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Round 100s", GroupName = "02. Zones", Order = 6)]
        public bool UseRound100 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Round 50s", Description = "Off by default: on MNQ the 50s fire often enough to make the zone permission close to no filter at all.", GroupName = "02. Zones", Order = 7)]
        public bool UseRound50 { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Stop offset (ticks)", Description = "Entry stop, this far beyond the pattern's LAST defining swing extreme — the second top/bottom, the third extreme of a triple, or the right shoulder of a head & shoulders (not the head).", GroupName = "03. Entry", Order = 0)]
        public int StopOffsetTicks { get; set; }

        [NinjaScriptProperty, Range(0.1, 3.0)]
        [Display(Name = "Stop buffer (x ATR)", Description = "Add-on only: after a flag add, the aggregate stop sits this far beyond the flag's far edge. Entry stops use the stop offset above.", GroupName = "03. Entry", Order = 1)]
        public double StopBufferAtr { get; set; }

        [NinjaScriptProperty, Range(0.3, 3.0)]
        [Display(Name = "Target (x pattern height)", Description = "Measured move from the neckline, in pattern heights. Every tranche exits at this one price.", GroupName = "03. Entry", Order = 2)]
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

        // Amendment 6 — the daily-limits block, ported from LatigoBreak for parity across
        // Javier's strategies. Kept in "05. Risk" rather than a group of its own so the
        // existing group numbering survives; the three sit together at the bottom of it,
        // in the order LatigoBreak shows them (target, limit, account-wide).
        [NinjaScriptProperty, Range(0, 100000)]
        [Display(Name = "Daily profit target (USD)", Description = "Realized session profit at or beyond this flattens and locks out until the next session — the winning half of the same governor as the loss limit. 0 = off.", GroupName = "05. Risk", Order = 4)]
        public double DailyProfitTargetUsd { get; set; }

        [NinjaScriptProperty, Range(0, 100000)]
        [Display(Name = "Daily loss limit (USD)", Description = "Realized session loss at or beyond this flattens and locks out until the next session. 0 = off.", GroupName = "05. Risk", Order = 5)]
        public double DailyLossLimitUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Account-wide (all markets)", Description = "Measure the two limits above against the SUM of the day P&L of every PATTERNZONE instance on this account, so one breach flattens and locks out all of them together. Other strategies and manual trades are NOT included. The shared total also counts OPEN P&L, unlike the per-strategy limits. OFF = this instance's own realized P&L only.", GroupName = "05. Risk", Order = 6)]
        public bool AccountWide { get; set; }

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

        // Cosmetic dial (review finding): [NinjaScriptProperty] gates
        // constructor-param inclusion and optimizer/walk-forward eligibility,
        // NOT grid visibility (that's [Browsable]+[Display] — see LongBrush
        // above, which is grid-editable with no [NinjaScriptProperty] either).
        // Dropping it here only takes the three cosmetic dials below (both
        // opacities and the line width) off the optimizable-parameter list;
        // they stay editable in the strategy dialog and persist via standard
        // serialization.
        [Range(5, 100)]
        [Display(Name = "Pattern opacity (%)", GroupName = "06. Drawing", Order = 4)]
        public int PatternOpacityPct { get; set; }

        [Range(2, 100)]
        [Display(Name = "Zone opacity (%)", GroupName = "06. Drawing", Order = 5)]
        public int ZoneOpacityPct { get; set; }

        [Range(1, 10)]
        [Display(Name = "Pattern line width", Description = "Stroke width of the pattern polyline and the flag's pole and rails.", GroupName = "06. Drawing", Order = 6)]
        public int PatternLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ATM strategy", Description = "Route entries through an NT8 ATM strategy template instead of the built-in bracket. The template then OWNS the trade: it supplies the stop and target, the stop offset/buffer and target multiple are ignored, and the flag add-on is disabled. Realtime/Playback only — the Strategy Analyzer ignores ATM strategies.", GroupName = "07. ATM", Order = 0)]
        public bool UseAtmStrategy { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(PatternZoneShell.AtmTemplateNameConverter))]
        [Display(Name = "ATM template", Description = "One of the ATM strategy templates saved on this machine (the same list Chart Trader shows). Required when ATM mode is on — an unknown name blocks trading rather than silently falling back.", GroupName = "07. ATM", Order = 1)]
        public string AtmTemplateName { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw rejected patterns", Description = "Diagnostic: draw the patterns the permission gauntlet refused, with the reason. Noisy — off for real runs.", GroupName = "06. Drawing", Order = 7)]
        public bool DrawRejectedPatterns { get; set; }
        #endregion
    }

}

// Own namespace, NOT NinjaTrader.NinjaScript.Strategies: NT8 compiles every file
// under bin/Custom into one assembly, and a helper type sitting in an NT8
// namespace is how a CS0101 duplicate-type clash starts (the TrapFlow gap).
namespace PatternZoneShell
{
    // Amendment 6: the shared day governor behind "Account-wide (all markets)".
    // STATIC state, so one record per account is shared by every strategy instance
    // in the NT8 process that publishes into it — today that is PatternZone only,
    // which is exactly what the dialog and the README claim. Public and neutrally
    // named (not nested in the strategy) so another strategy CAN adopt it later and
    // make the governor genuinely cross-strategy; until one does, "account-wide"
    // means "every PatternZone on this account".
    //
    // Per-instance CONTRIBUTIONS, never Account.Get(realized) + Account.Get(unrealized):
    // those are two separately-updated aggregates. The instant a winner's target fills,
    // realized is already credited while account unrealized still carries the closed
    // position, so their sum double-counts that trade and fires the profit target early.
    // LatigoBreak hit exactly that live on 2026-08-10 — a $750 target flattened
    // everything at $539 realized. Each instance's own SystemPerformance/Position pair
    // is event-ordered on its own strategy thread, so the shared sum inherits that
    // consistency. (LatigoBreakStrategy.cs:117-129.)
    public static class DailyGovernor
    {
        // One trading day of one account. `Breached` is the broadcast: the instance
        // that trips a limit sets it, and every other instance locks out on its next
        // bar — volatile because those instances run on their own strategy threads.
        public sealed class AccountDay
        {
            public DateTime Day;
            // Volatile on the PRIVATE field, exposed through a plain property: a public
            // volatile field is not CLS-compliant (CS3026) and NT8's assembly is marked
            // CLS-compliant, so the obvious spelling would ship a new warning.
            private volatile bool _breached;
            public bool Breached
            {
                get { return _breached; }
                set { _breached = value; }
            }
            private readonly Dictionary<string, double> _pnl = new Dictionary<string, double>();

            // Publish this instance's contribution and read the group total back in one
            // atomic step — a publish that raced a sum could total a half-updated group.
            public double Publish(string key, double pnl)
            {
                lock (_pnl)
                {
                    _pnl[key] = pnl;
                    double total = 0;
                    foreach (double v in _pnl.Values)
                        total += v;
                    return total;
                }
            }

            // Remove ONE contributor, on its way out. The record and its Breached flag
            // stay: a breach has to outlive the instance that left, or disabling the
            // breached chart would clear the group's lockout.
            public void Remove(string key)
            {
                lock (_pnl)
                    _pnl.Remove(key);
            }

            // "MNQ 09-26/4f2a -412.50; ES 09-26/9c01 120.00; " — which instrument put
            // the group where it is. Only ever built on the bar a limit actually trips.
            public string Breakdown()
            {
                var sb = new System.Text.StringBuilder();
                lock (_pnl)
                    foreach (KeyValuePair<string, double> kv in _pnl)
                        sb.Append(kv.Key).Append(' ')
                          .Append(kv.Value.ToString("0.00", CultureInfo.InvariantCulture))
                          .Append("; ");
                return sb.ToString();
            }
        }

        private static readonly Dictionary<string, AccountDay> Accounts = new Dictionary<string, AccountDay>();

        // Fetch-or-create for one account's CURRENT day. Called on every governor tick
        // and never cached by the caller, so a wipe-and-recreate cannot split the group.
        // Null when the registry has already moved to a LATER day than the caller's: an
        // instance still grinding through history must not push yesterday's numbers into
        // today's total. A newer day replaces the record outright, which is what resets
        // both the contributions and the breach broadcast at a session rollover.
        public static AccountDay For(string account, DateTime day)
        {
            lock (Accounts)
            {
                AccountDay g;
                Accounts.TryGetValue(account, out g);
                if (g != null && g.Day > day)
                    return null;
                if (g == null || g.Day < day)
                {
                    g = new AccountDay { Day = day, Breached = false };
                    Accounts[account] = g;
                }
                return g;
            }
        }

        // Drop an account's record so the group can re-form on an EARLIER day. Only a
        // Playback rewind needs this (see ResetAll) — `For` refuses an older day, so
        // without it a rewound run would leave shared mode silently inert.
        public static void Forget(string account)
        {
            lock (Accounts)
                Accounts.Remove(account);
        }

        // Retire one instance's contribution when that instance goes away. Without it
        // every disable/re-enable, parameter change or reconnect would leave the dead
        // `_govKey`'s last published P&L frozen in the record while the reloaded
        // instance publishes under a FRESH key — the group would double-count a phantom
        // and lock everyone out (or trip the profit target) on P&L that no longer exists.
        public static void Drop(string account, string key)
        {
            lock (Accounts)
            {
                AccountDay g;
                if (Accounts.TryGetValue(account, out g))
                    g.Remove(key);
            }
        }
    }

    // Amendment 5: fills the "ATM template" dropdown from the templates saved on
    // THIS machine, the same folder Chart Trader's ATM selector reads.
    // StringConverter, not TypeConverter: GetStandardValuesExclusive = false lets a
    // name be typed, and the base has to know how to convert that string.
    // ponytail: enumerating the folder is the whole implementation — NT8 exposes no
    // public template-name API in the documented surface.
    public class AtmTemplateNameConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            var names = new List<string>();
            try
            {
                string dir = NinjaTrader.NinjaScript.Strategies.PatternZoneStrategy.AtmTemplateDir;
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.xml"))
                        names.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }                                  // an unreadable folder = empty list, never a crashed dialog
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return new StandardValuesCollection(names);
        }
    }
}
