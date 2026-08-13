# PatternZone — Design Spec

- **Date:** 2026-08-12
- **Status:** approved section-by-section in brainstorm; pending Javier's review of this written spec
- **Repo:** `jalv92/PatternZone` (public, MIT — to be created at `projects/Trading/PatternZone/`)
- **Instrument / TF:** MNQ, 1-minute detection, RTH trading window, personal account (no prop rules)

## 1. What & why

Automated NinjaTrader 8 strategy that trades classic **reversal chart patterns** (double/triple top & bottom, head & shoulders and inverse) on 1-minute MNQ, but **only when the pattern forms at a long-memory support/resistance zone** (prior-day H/L, overnight H/L, prior close, day open, round numbers). **Continuation patterns (bull/bear flags) are used exclusively to add to an already-open position** — a flag with no open position does nothing. Every entry and every add draws the detected pattern geometry on the chart, semi-transparent, no text.

The core hypothesis: reversal patterns as such carry no edge on NQ intraday (extensively falsified in this workspace), but reversal patterns **conditioned on long-memory levels** test the one hypothesis the 2026-08 studies left alive.

## 2. Prior evidence this design stands on

- [[strategy-profitability-gates]] — the house gate table applies (dollar rows scaled ÷10 for MNQ, §12).
- [[break-retest-study]] — same-session 30s pivot levels carry zero information (placebo-identical); intrabar triggers create lookahead. → This design uses **only long-memory levels** and **close-confirmed triggers**.
- [[trendline-retest-study]] — 15m trendline class killed. → Wedges (a trendline-fit machine) deferred to Phase 1.1.
- [[mff-eval-project]] — ~128 trials: unconditioned OHLCV price patterns dead on NQ intraday. → The zone condition IS the thesis; the pattern alone is not.
- [[nq-strategy-search-2026-08]] — stops < ~1×ATR(1m) are noise-stopped by construction. → Min pattern-height filter (§6).
- Route chosen by Javier: **NT8-first** (no PropSim mirror in v1). Consequence: no placebo/excursion controls are available, so epistemic honesty lives entirely in the validation protocol (§12): frozen defaults, out-of-sample only, Replay forward as THE gate, pre-registered kill criteria.

## 3. Approved decisions (audit trail, 2026-08-12)

1. Zones = **long-memory class only** (the live hypothesis). Session-internal pivots excluded.
2. Reversal set v1 = double top/bottom, triple top/bottom, H&S + inverse (one shared swing machine). **Wedges = Phase 1.1** (separate trendline machine; not in v1).
3. Continuation = **flags, add-on only**. Detected flag with no open position → no action (drawing only if `DrawRejectedPatterns`).
4. Environment = personal MNQ account. No prop-firm envelope; risk via strategy parameters.
5. Detection timeframe = **1 minute** (matches the reference screenshots; 30s rejected as noise).
6. Chart feedback = **pattern geometry drawn over the candles, semi-transparent, no letters/names/tables** (Javier's explicit final choice over the corner-table idea).
7. Name = **PatternZone**.

## 4. Detection engine

**Swing machine (1m).** A swing high (low) is confirmed when `SwingStrength` bars on each side are lower (higher). Confirmation arrives `SwingStrength` bars after the extreme — causal by construction. All pattern logic consumes only confirmed swings.

**ATR.** Wilder ATR(14) on the 1m series. Period fixed (not a parameter) to cap degrees of freedom. All tolerances/buffers below are ATR-scaled.

**Patterns (v1, all on confirmed swings):**

| Pattern | Definition | Neckline |
|---|---|---|
| Double top | 2 swing highs, height diff ≤ `TopToleranceAtr`, one intervening swing low | the intervening low |
| Double bottom | mirror | the intervening high |
| Triple top/bottom | 3 extremes within `TopToleranceAtr`, 2 intervening swings | worst (furthest) intervening swing |
| H&S / inverse | 3 highs (lows): head exceeds both shoulders by ≥ `HeadProminenceAtr`; shoulders within `TopToleranceAtr` of each other | line through the two intervening valleys (peaks); trigger level = its value at the breaking bar |

**Structural rules:**
- **Prior trend (Amendment 2):** a reversal must have something to reverse. The pattern's FIRST defining extreme must be the extreme of the `TrendLookbackBars` window behind it — a top-family pattern only after an up-leg, a bottom-family one only after a down-leg. Evaluated once, at candidate creation. Shorter history clamps rather than rejecting.
- Max pattern width: first defining swing → neckline break ≤ `MaxPatternBars` (default 60 × 1m).
- Min pattern height (extreme→neckline) ≥ `MinPatternHeightAtr` — kills micro-patterns AND guarantees stops > 1 ATR (§2).
- One live armed pattern per direction; first confirmed break wins; swings of a traded pattern are consumed (no re-trigger on the same structure). A double top whose neckline never breaks can evolve into a triple top / H&S as new swings confirm.
- Mutual exclusion DT vs H&S: diff ≤ `TopToleranceAtr` → double-top candidate; middle peak ≥ `HeadProminenceAtr` above both → H&S candidate.

**Confirmation & trigger.** `Calculate = OnBarClose`. A pattern fires when a 1m bar **closes** beyond its neckline by ≥ `NecklineBreakTicks`. Entry at market on the next bar. Zero intrabar decisions (lookahead lesson, §2).

## 5. Long-memory zones (the permission)

A confirmed pattern is **tradeable only if its defining extreme(s) touch a zone**:
- Double top/bottom: **both** extremes touch the same zone band.
- Triple: ≥2 of 3 extremes touch the same band.
- H&S: the **head** touches (the head is the rejection point).

**Level classes (each with its own toggle):**

| Level | Definition | Toggle default |
|---|---|---|
| Prior-day High / Low | prior RTH session (09:30–16:00 ET) H/L | on |
| Overnight High / Low | 18:00 ET prior day → 09:30 ET today | on |
| Prior close | last 1m close at/before 16:00 ET prior RTH | on |
| Day open | first RTH bar open (09:30 ET) | on |
| Round numbers ×100 | every 100 pts (e.g. 29,900 / 30,000) | on |
| Round numbers ×50 | intermediate 50s | off |

Zone band = level ± `ZoneHalfWidthAtr` × ATR, **plus `ZoneProximityAtr` × ATR of proximity allowance for permission only** (Amendment 1): a pattern that forms *near* a level qualifies too. The band drawn on the chart stays the half-width, so the permission reaches further than what is painted — deliberate, and called out in the validation runbook so it is not read as a drawing bug. Session levels (PDH/PDL, ON H/L, prior close, day open) recompute once per session at the RTH open. Round-number zones are evaluated against the **nearest** ×100 (×50) level to the pattern extreme — no pre-materialized list.

**Data/session handling:** single 1m MNQ series on the instrument's full ETH session template (overnight data must exist in the series). The strategy trades only inside the parametrized RTH window and computes level windows internally via session/time logic.

## 6. Entry & trade management

- **Direction:** top-family patterns → short; bottom-family → long.
- **Entry:** market, open of the bar after the confirming close.
- **Stop:** the pattern's **last defining swing** ± `StopOffsetTicks` ticks (Amendment 1). That swing is always the last extreme — second top/bottom, third extreme of a triple, right shoulder of an H&S (deliberately **not** the head). The extreme keeps its other job: a close beyond it still invalidates an armed candidate. `StopBufferAtr` now governs only the add-on's aggregate stop (§7).
- **Target:** measured move — pattern height projected from the break point, × `TargetMultiple` (default 1.0). Classic rule; risk ≈ (neckline→last swing) + offset vs reward ≈ height.
- One position at a time, flat-to-flat. One shot per pattern; no re-entry on the same neckline.
- While a position is open, new reversal confirmations are **ignored** — no reversing, no stacking beyond the flag add-on. They draw only if `DrawRejectedPatterns`.
- Forced flat at trading-window end.

## 7. Flag add-on (continuation, add-only)

Armed only while a position is open (and adds remaining < `MaxAdds`):

- **Pole:** favorable move ≥ `PoleMinAtr` × ATR within ≤ `PoleMaxBars` bars, measured from entry fill (or from the last add fill).
- **Flag:** the next `FlagMinBars`–`FlagMaxBars` bars consolidate: total H-L envelope ≤ `FlagRangeMaxAtr` × ATR, no 1m close beyond the pole's extreme (no continuation yet), and net drift flat or against the position.
- **Add trigger:** 1m close beyond the flag boundary in the position's favor → add `+1` MNQ (market, next bar).
- **Guard:** remaining distance to target ≥ `MinDistToTargetAtr` × ATR at trigger, else skip.
- **After an add:** the aggregate stop for the WHOLE position moves to the flag's far edge ∓ `StopBufferAtr` × ATR (structure-protected). Target unchanged: all tranches exit at the original pattern target or the aggregate stop.
- Flag detected with no position → nothing (draw only if `DrawRejectedPatterns`).

## 8. Drawing layer (no text, ever)

- **Reversal entry:** semi-transparent polyline over the defining swings (the M / W zigzag; 5-point H&S), plus the **lead-in** leg from the prior opposite swing and the **lead-out** leg to the break bar (Amendment 3), so the pattern reads as a complete shape rather than a fragment. Drawn at confirmation. **No neckline segment** (Amendment 1 — Javier does not want it on the chart).
- **Flag add:** pole line + the two parallel channel lines of the flag.
- **Zones:** faint horizontal bands (level ± half-width), `DrawZones` toggle. The band drawn is the half-width only; permission reaches `ZoneProximityAtr` further (§5), so a permitted extreme can sit outside the drawn band.
- **Rejected patterns** (out-of-zone / under-height / flag-without-position): even fainter, `DrawRejectedPatterns` (default off) — audit tool for Replay.
- Colors: `LongBrush` / `ShortBrush` / `AddonBrush`, opacity `PatternOpacityPct` (default 65) and `ZoneOpacityPct` (default 10). Stroke width `PatternLineWidth` (default 4), `DrawOnPricePanel`, no autoscale, tags per internal pattern id, drawings persist for the session.
- No labels, no names, no tables (decision #6).

## 9. Account risk

- `Contracts` base (default 1 MNQ) + up to `MaxAdds` (default 1).
- `DailyLossLimitUsd` / `DailyProfitTargetUsd`: session P&L at or beyond either → flatten + lockout for the day. One governor, two triggers; `0` disables a trigger (Amendment 6).
- `AccountWide` (default off): measure both triggers against the SUM of the day P&L of every **PatternZone** instance on the account, one breach locking all of them out together. Not other strategies, not manual trades (Amendment 6).
- `MaxTradesPerSession` (default 3; adds don't count as trades).
- Trading window `TradingStart`–`TradingEnd` (default 09:30–15:55 ET), forced flat at end.

## 10. Parameters (closed list)

Statistical dials (frozen before any P&L is seen; changes = documented amendment):

| # | Parameter | Default |
|---|---|---|
| 1 | SwingStrength | 3 |
| 2 | TopToleranceAtr | 0.30 |
| 3 | HeadProminenceAtr | 0.30 |
| 4 | MaxPatternBars | 60 |
| 5 | NecklineBreakTicks | 2 |
| 6 | MinPatternHeightAtr | 1.5 |
| 6a | UseTrendFilter *(Amendment 2)* | true |
| 6b | TrendLookbackBars *(Amendment 2)* | 60 |
| 7 | ZoneHalfWidthAtr | 0.50 |
| 8 | ZoneProximityAtr *(Amendment 1)* | 0.50 |
| 9–14 | Level toggles (PDH/PDL, ON H/L, prior close, day open, ×100, ×50) | on ×5, ×50 off |
| 15 | StopOffsetTicks *(Amendment 1)* | 10 |
| 16 | StopBufferAtr *(add-on stop only since Amendment 1)* | 0.50 |
| 17 | TargetMultiple | 1.0 |
| 18 | EnableFlagAddon | true |
| 19 | PoleMinAtr | 2.0 |
| 20 | PoleMaxBars | 8 |
| 21 | FlagMinBars | 3 |
| 22 | FlagMaxBars | 10 |
| 23 | FlagRangeMaxAtr | 1.0 |
| 24 | MinDistToTargetAtr | 1.5 |
| 25 | MaxAdds | 1 |
| 26 | Contracts | 1 |
| 27 | MaxTradesPerSession | 3 |
| 28 | DailyLossLimitUsd | 200 |
| 28a | DailyProfitTargetUsd *(Amendment 6)* | 0 (off) |
| 28b | AccountWide *(Amendment 6)* | false |
| 29–30 | TradingStart / TradingEnd | 09:30 / 15:55 ET |

Cosmetic dials (free to change anytime): LongBrush, ShortBrush, AddonBrush, PatternOpacityPct (65), ZoneOpacityPct (10), PatternLineWidth (4), DrawZones (true), DrawRejectedPatterns (false).

## 11. NT8 implementation notes

- Single file `ninjascript/PatternZoneStrategy.cs`, `namespace NinjaTrader.NinjaScript.Strategies` → deploys to `Custom/Strategies/` (the namespace decides the folder, house rule).
- `Calculate = OnBarClose`. All order actions from `OnBarUpdate` only — never from market-data callbacks ([[nt8-orders-from-marketdata-thread-crash]]).
- Managed approach. Unique signal names per tranche (`PZ_DT`, `PZ_HS`, `PZ_TT`, `PZ_ADD1`…), `EntriesPerDirection = 1 + MaxAdds` with `EntryHandling.UniqueEntries`. Brackets via `SetStopLoss/SetProfitTarget(signal, CalculationMode.Price, …)`; the post-add aggregate stop updates every open signal's stop to the flag-structure price.
- In-flight order flags before every `Enter*`/`Exit*` ([[nt8-order-event-race]]).
- Compile with `nt8c` on every edit (PostToolUse hook covers `projects/Trading/`). Deploy = copy the `.cs` byte-identical to the Windows NT8 Custom folder after EVERY closed task + the two post-deploy checks ([[nt8-deploy-copy-files]]). Work is not done until the file is there.
- Drawing via `Draw.Line` segments / `Draw.Rectangle` with alpha brushes.

## 12. Validation protocol & gates (pre-registered)

Costs assumed throughout: MNQ ≈ $1.24–1.54 RT commission + 2 ticks slippage ($1.00) ≈ **$2.3–2.5/RT per contract**.

1. **Compile + deploy** (nt8c, Custom folder, post-deploy checks).
2. **Visual QA (detector fidelity):** Market Replay over recent sessions. Must-pass: the strategy detects and draws the **two hand-annotated episodes from Javier's screenshots** (double top ~29,985 and double bottom/W ~29,650, MNQ 09-26, 1m — pin exact dates on the chart) the way Javier drew them. Detector wrong here → fix before any backtest.
3. **Frozen backtest:** defaults per §10, frozen BEFORE the first P&L. Strategy Analyzer, Order Fill Resolution = High (1-tick), commission + slippage as above, longest available MNQ 1m history (NQ data substitution only as a documented fallback if MNQ history is short, with costs kept at MNQ scale). Judged against the house gate table with dollar rows ÷10: **avg trade ≥ $5/contract net (≈2× cost; ideal $8–15), PF ≥ 1.3, expectancy ≥ 0.10R, sample ≥ 100 trades, Sharpe ≥ 1.0, commissions/gross ≤ 30%, equity R² ≥ 0.65**. Red-flag check (too-good = suspect) per the same memory.
4. **THE gate — Replay forward:** ≥ 20–30 RTH sessions the strategy has never seen (same standard as RlpLong). Only a PASS here qualifies real money on the personal account.
5. **Kill criteria:** frozen-defaults expectancy ≤ 0 after costs → project result is negative and **gets published in the README** (house tradition). Any re-tune after seeing P&L = a documented amendment with its own out-of-sample shot, never silent.
6. **Add-on judged separately:** report base-entry-only vs base+adds. If adds don't improve expectancy/DD, `EnableFlagAddon` default flips to false in v1.1.

## 13. Repo & deliverables

```
PatternZone/
├── ninjascript/PatternZoneStrategy.cs
├── docs/design.md          ← this spec, copied
├── README.md               ← honest, readme-craft, no "promising" language
└── LICENSE                 ← MIT
```

Deliverables in order: repo scaffold → strategy .cs (compiling via nt8c) → deploy to NT8 Custom → visual QA vs screenshots → frozen backtest → Replay forward verdict.

## 14. Out of scope (v1)

- Wedges (Phase 1.1 — separate converging-trendline machine).
- Pennants, cup & handle, diamonds, islands, rounding tops/bottoms, megaphone (not selected).
- Continuation patterns as standalone entries (explicitly ruled out).
- PropSim mirror / placebo controls (route B; can be retrofitted later using the PullbackZone mirror playbook).
- 30-second detection timeframe.
- Prop-firm envelope logic.

## Amendments

Every change to the frozen spec after it was written lands here, dated, with
what was known at the time. The §10 table above carries the current values; this
section is the audit trail of how they got there.

**Amendment 1 — 2026-08-12 (pre-P&L, from Phase 1 visual QA).** Javier's
feedback after watching the detector draw on a live chart. No P&L existed yet,
which is the only honest moment to change the frozen dials — nothing here was
chosen by looking at a result.

1. **Entry stop = the pattern's last defining swing ± `StopOffsetTicks`**
   (default 10), replacing extreme ± `StopBufferAtr` × ATR. The last defining
   swing is always the last extreme: second top/bottom, third extreme of a
   triple, and the **right shoulder** of an H&S — not the head, which is where
   the two rules differ most (an H&S stop is now materially tighter). The
   pattern extreme keeps its other role as the candidate-invalidation level.
   `StopBufferAtr` survives, governing **only** the add-on's aggregate stop
   (flag far edge ∓ `StopBufferAtr` × ATR), which is unchanged.
2. **The neckline is no longer drawn.** The swing polyline stays; the dashed
   neckline segment is gone from both the accepted and rejected paths. The
   engine still computes and carries `NecklineAtBreak` as the trigger price of
   record — the drawing layer simply no longer consumes it.
3. **Visibility:** `PatternLineWidth` added (default 4, cosmetic, applies to the
   pattern polyline and the flag's pole and rails); `PatternOpacityPct` default
   40 → 65.
4. **`ZoneProximityAtr` added** (default 0.50): the permission band becomes
   (`ZoneHalfWidthAtr` + `ZoneProximityAtr`) × ATR, so patterns forming near a
   level qualify. The **drawn** band is unchanged at the half-width, so
   permission deliberately reaches further than the painted band.

Consequence for the geometry: risk is no longer a pure ATR multiple, since the
stop offset is a fixed tick distance while the pattern height is ATR-scaled.
There is no single R floor across pattern families any more — see
`docs/validation.md` (Phase 3, "On the breakeven") for the recomputed numbers.

**Amendment 2 — 2026-08-12 (pre-P&L, from Phase 1 visual QA).** Javier watched
the strategy arm a top-family (short) reversal in the middle of a decline. A
reversal pattern with no prior trend is not a reversal of anything, and nothing
in the frozen §4 rules said so — the omission was a gap in the spec, not a
tuning preference, which is why it is fixed rather than left for a later run.

5. **Prior-trend gate.** `UseTrendFilter` (default true) and
   `TrendLookbackBars` (default 60). At candidate CREATION the engine checks the
   window `[first defining swing − TrendLookbackBars .. first defining swing]`:
   a top-family pattern is permitted only if that first top is the window's
   highest high, a bottom-family one only if its first bottom is the lowest low.
   Ties pass (the swing is itself a bar in the window). The window clamps to the
   history actually available, so a short history never auto-rejects.
   The `Fire` gauntlet order is now, and is pinned as:
   **busy → canTrade (silent) → session_cap → height → trend → zone → stop**,
   so a pattern with no trend behind it reports `"trend"` even when it is also
   off its zone.
6. **Rejection reasons became observable.** The chart carries no text by design
   (decision #6), which left the whole gauntlet unauditable in Replay. When
   `DrawRejectedPatterns` is on, each rejection now also prints one line — time,
   `REJECTED`, reason, pattern kind — to the **Output window**. The
   no-text-on-chart rule is untouched.
7. **Wrong-side stops are refused** (`"stop"`, added in review). A sloped H&S
   neckline keeps extrapolating: by the time the break lands it can sit *above*
   the right shoulder plus the offset, which would submit a short whose stop is
   BELOW its entry — an inverted bracket. The engine now checks the computed
   stop against the break close and rejects when it is not beyond the entry on
   the adverse side. Rejected, not re-anchored: a break priced past the
   pattern's own right shoulder is no longer the setup being traded.

**Amendment 3 — 2026-08-12 (pre-P&L, from Phase 1 visual QA). Drawing only —
no gate, no trade geometry.** Connecting only the defining swings drew a double
bottom as a "V with a roof": the shape a human recognises includes the legs that
lead into and out of it.

8. **The drawn pattern now completes its legs.** A **lead-in** segment from the
   swing immediately preceding the first defining swing (opposite-type by
   alternation, so it is the real origin of the move into the pattern) and a
   **lead-out** segment from the last defining swing to the break bar's close.
   The lead-in is omitted, never invented, when the swing list did not hold that
   preceding swing. Both use the same brush, width and opacity rules as the rest
   of the polyline, so rejected patterns get them at half opacity too.

**Amendment 4 — 2026-08-12 (pre-P&L). Shell only — the core is untouched.**
Javier wants to run 150-tick charts without losing seconds/minutes. The
detection engine was already bar-agnostic; the shell's session bookkeeping was
not, because it derived a bar's start as `close − barSeconds`, which is
meaningless for a tick/volume/range bar.

9. **Any-bar-type session bookkeeping — and the rule splits by chart type.**
   - **Time charts (Minute / Second) keep the stamp arithmetic**, `close −
     barSeconds`, byte-identical to pre-amendment behavior. This is not a
     fallback, it is the *correct* rule for them: NT8 **skips** a minute with no
     trades, so there the previous bar's close is not this bar's start. Using it
     silently misclassified the first RTH bar after a dead 09:28–09:30 as
     overnight (the whole session-open block fired late) and a post-16:00 bar
     after a dead close as RTH (prior close polluted). Caught in review.
   - **Every other bar type** (tick / volume / range) has no duration to
     subtract, so a bar starts when the previous one closed — exact for
     contiguous bars. Across the session gap, a halt, or on the first bar a
     30-minute cap falls back to `close − 1s`, which classifies a post-gap tick
     bar by the side its ticks actually printed on.

   `_barSecs` is the discriminator — positive on a time chart, 0 on every other
   bar type, and deliberately never clamped to a default. Everything downstream
   (`inRth` / `inOn`, the overnight accumulators, the first-RTH-bar snapshot and
   `DayOpen`, the window gates) reads `barStart` unchanged.
10. **The bar-type warning was reworded.** It no longer says the chart "does not
    run the strategy that was designed" — every bar type is supported now. It
    names the detected bar type and warns that all bar-count dials count THIS
    chart's bars while the ATR scales to them, so the dials mean something
    different per chart. The validated baseline stays 1 Minute, and **each
    bar-type variant is its own strategy for evidence purposes** — see
    `docs/validation.md`.

**Amendment 5 — 2026-08-12 (pre-P&L). Shell only — the core is untouched.**
Javier wants the option of handing a trade to one of his own NT8 ATM strategy
templates once PatternZone has found the entry.

11. **ATM mode.** `UseAtmStrategy` (default **false**) and `AtmTemplateName`
    (a dropdown of the ATM templates saved on this machine — the folder Chart
    Trader reads). With it **off, nothing changes**: the managed path is
    byte-identical, which is what makes this a strict superset.
    With it on, an entry is submitted via `AtmStrategyCreate` (market) and **the
    template owns the trade from that moment**. It supplies the stop, the target and
    the **position size**, so `StopOffsetTicks`, `StopBufferAtr`, `TargetMultiple`
    and `Contracts` are all ignored, and the **flag add-on is disabled** — the shell hands the engine a config with
    `EnableFlagAddon = false`, so no add is ever emitted (the core has no idea
    ATM exists). One line at startup lists exactly what the template overrides.
12. **The engine is driven by polling in ATM mode.** An ATM position is
    invisible to `Position` and fires none of our order handlers, so each closed
    bar checks `GetAtmStrategyEntryOrderStatus` and `GetAtmStrategyMarketPosition`
    and calls `OnEntryFilled` / `OnEntryFailed` / `OnPositionClosed` itself. One
    ATM at a time; flat-to-flat is preserved because a live `atmId` is refused by
    the entry guard.
13. **Strategy-level risk still binds.** The trading-window flatten and the
    daily-limit lockout both call `AtmStrategyClose`, retried each bar until the
    poll sees flat. `SystemPerformance` never books an ATM trade, so each closed
    ATM's `GetAtmStrategyRealizedProfitLoss` is accumulated into the session
    total that the daily-limit guard reads (and, in account-wide mode, the total
    this instance publishes to the shared record — Amendment 6).
14. **Two refusals, both loud, neither silent.** An empty or missing template
    blocks trading outright rather than falling back to the managed path — the
    user chose ATM deliberately. And `AtmStrategyCreate` is ignored on historical
    data, so in the Strategy Analyzer (and any non-realtime state) ATM mode takes
    **no trades at all** and says so once.

**Amendment 6 — 2026-08-12 (pre-P&L). Shell only — the core is untouched.**
Javier wants the daily-limits block he already has in LatigoBreak, here, behaving
the same way: the same three controls, the same semantics. None of this is a
trading decision, so `PatternZoneCore.cs` and its 141 assertions are unchanged.

15. **One governor, two triggers.** `DailyProfitTargetUsd` (new, default `0` =
    off) joins `DailyLossLimitUsd`. Either one, breached, calls the **same**
    `Lockout()` the loss limit always called, so the profit target inherits every
    behavior already built and tested: flatten the managed position, retry that
    flatten each bar until flat, `AtmStrategyClose` a live ATM on the same bar,
    and refuse entries until the next session open. There is no second lockout
    path. `CheckDailyLoss` is renamed `CheckDailyLimits`, since it no longer only
    checks a loss.
16. **`AccountWide` (default off) switches only the number being measured.** Off,
    the computation is the pre-amendment one, byte-for-byte: this instance's
    realized session P&L plus its ATM total. On, the instance publishes its day
    P&L into a shared per-account record and the triggers are measured against the
    SUM of every contribution; a breach sets a broadcast flag that locks out every
    other instance on that account on its next bar, including instances whose own
    limits are `0` — those still publish, so they count toward everyone else's sum.

    **What "account-wide" honestly means.** The registry is static, so the sum
    covers **other PatternZone instances** — PatternZone on MNQ and PatternZone on
    NQ share a governor. It does **not** include LatigoBreak, TBStrategy or manual
    trades. The class ships public in the `PatternZoneShell` namespace
    (`DailyGovernor`) precisely so another strategy *can* publish into it later and
    make the governor genuinely cross-strategy; until one does, the dialog, the
    startup log and the README all say "every PatternZone", never "your account".
17. **Contributions, never `Account.Get(realized) + Account.Get(unrealized)`.**
    Those are two separately-updated aggregates: the instant a winner's target
    fills, realized is already credited while account unrealized still carries the
    closed position, so their sum double-counts that trade and fires the profit
    target early. LatigoBreak hit exactly this live on 2026-08-10 — a $750 target
    flattened everything at $539 realized. Each instance's own numbers are
    event-ordered on its own strategy thread, so the shared sum inherits that
    consistency.
18. **The two bases differ, deliberately.** The per-strategy path stays
    **realized-only** (what it has always measured, and what the frozen spec
    describes). The account-wide contribution **adds open P&L** — `Position`'s
    unrealized, or `GetAtmStrategyUnrealizedProfitLoss` in ATM mode, since an ATM
    position is invisible to `Position`. That is LatigoBreak's basis
    (`LatigoBreakStrategy.cs:830-838`) and it is the protective one: a shared
    governor exists to close the account before an account-level drawdown rule
    fires, and prop firms measure open P&L. Practical difference: **on** catches a
    big loser while it is still open; **off** only after the trade books.
19. **Two silent-failure guards.** `_acctSessionDay` moves in lockstep with
    `_dayStartCum` (set together at the RTH open, cleared together in `ResetAll`)
    and a NaN contribution is published as `0`, because `_dayStartCum` is
    deliberately NaN until the first session open and one NaN in the sum makes
    every comparison false — the governor would die silently for the whole group.
    And the registry wipe on reset is gated to a **Playback rewind** only: doing it
    at `DataLoaded` too would mean disabling and re-enabling a strategy clears a
    live breach broadcast, i.e. a daily limit you can escape with a checkbox.

**Amendment 8 — 2026-08-12 (pre-P&L). Intraday pivot zones.**
Javier approved a NEW level class: S/R built from repeatedly-touched intraday
pivots, on top of the six session/round levels. The zone engine is ported from
`PullbackZoneStrategy.cs:405-541` rather than reinvented.

**Stated honestly up front: this level class is unvalidated, and its family has a
poor record here.** The two studies that tested levels-plus-retest on 15m data
were both killed — `[[break-retest-study]]` (detection fine, naive entry dead on
a triple kill) and `[[trendline-retest-study]]` (does not separate from placebo,
negative expectancy). What is different this time is the *use*: a pivot zone is
only a **permission** input to a pattern that must independently qualify, never
an entry trigger on its own. That is a weaker claim than either dead study made,
which is the only reason it is worth running — and it is exactly why Phase 3/4
must be able to attribute results to it (run the frozen backtest with it off as
well as on before believing any lift).

20. **A pivot zone carries its own band.** `UseIntradayPivots` (default **on** —
    the user asked for it working), `PivotSeriesMinutes` (5), `PivotMinTouches`
    (3 — PullbackZone ships 2), plus the ported dials `PivotK` (3),
    `PivotZoneWidthAtr` (0.30), `PivotBreakAtr` (0.25), `PivotExpiryDays` (2).
    A pivot is revealed k bars back by a strict-unique extreme, accumulates
    touches (bar enters the band, closes back outside), and is promoted to a zone
    at `PivotMinTouches`. Its half-width is **frozen at the pivot series' ATR on
    the reveal bar** — a zone is a fixed box and its edges cannot drift. Zones die
    on a clean break or on calendar-day expiry.
21. **The core stays pure.** The shell computes the zones and hands the engine a
    `List<PzZone>` (price + half-width) ONCE, mutating it in place; `PzZone` is a
    two-field struct with no NT8 types, so the whole seam is unit-testable and the
    suite pins it (7 new assertions, 148 total). Session and round levels are
    tried FIRST — a pivot zone never steals the reported reason from a level that
    already qualified. Proximity (`ZoneProximityAtr`) is applied to a pivot band
    the same way it is to a named one: added on top of that band's own
    half-width, because proximity means "how far outside a band still counts".
22. **Its own series, added only when enabled.** `AddDataSeries(Minute,
    PivotSeriesMinutes)` at `State.Configure`, so with the feature off the
    strategy stays single-series and every path is untouched. Folding runs from
    the PRIMARY branch and reads the pivot series by **absolute index** through
    `BarsArray` — a secondary series' own processing pointer is one bar late — and
    only for bars whose time is at or before the current primary bar's close. That
    `> Time[0]` guard is the only thing between the fold loop and lookahead.
23. **Drawing.** Born-to-now rectangles in the PullbackZone style, OrangeRed for
    pivot highs and DodgerBlue for lows (distinct from the SlateGray session
    bands), greyed once when they die, under the same `DrawZones` toggle.
