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

Zone band = level ± `ZoneHalfWidthAtr` × ATR. Session levels (PDH/PDL, ON H/L, prior close, day open) recompute once per session at the RTH open. Round-number zones are evaluated against the **nearest** ×100 (×50) level to the pattern extreme — no pre-materialized list.

**Data/session handling:** single 1m MNQ series on the instrument's full ETH session template (overnight data must exist in the series). The strategy trades only inside the parametrized RTH window and computes level windows internally via session/time logic.

## 6. Entry & trade management

- **Direction:** top-family patterns → short; bottom-family → long.
- **Entry:** market, open of the bar after the confirming close.
- **Stop:** pattern extreme (highest top / head / lowest bottom) ± `StopBufferAtr` × ATR.
- **Target:** measured move — pattern height projected from the break point, × `TargetMultiple` (default 1.0). Classic rule; risk ≈ height + buffer vs reward ≈ height.
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

- **Reversal entry:** semi-transparent polyline over the defining swings (the M / W zigzag; 5-point H&S) + a horizontal neckline segment spanning the pattern. Drawn at confirmation.
- **Flag add:** pole line + the two parallel channel lines of the flag.
- **Zones:** faint horizontal bands (level ± half-width), `DrawZones` toggle.
- **Rejected patterns** (out-of-zone / under-height / flag-without-position): even fainter, `DrawRejectedPatterns` (default off) — audit tool for Replay.
- Colors: `LongBrush` / `ShortBrush` / `AddonBrush`, opacity `PatternOpacityPct` (default 40) and `ZoneOpacityPct` (default 10). Thin strokes, `DrawOnPricePanel`, no autoscale, tags per internal pattern id, drawings persist for the session.
- No labels, no names, no tables (decision #6).

## 9. Account risk

- `Contracts` base (default 1 MNQ) + up to `MaxAdds` (default 1).
- `DailyLossLimitUsd`: realized session P&L breaches → flatten + lockout for the day.
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
| 7 | ZoneHalfWidthAtr | 0.50 |
| 8–13 | Level toggles (PDH/PDL, ON H/L, prior close, day open, ×100, ×50) | on ×5, ×50 off |
| 14 | StopBufferAtr | 0.50 |
| 15 | TargetMultiple | 1.0 |
| 16 | EnableFlagAddon | true |
| 17 | PoleMinAtr | 2.0 |
| 18 | PoleMaxBars | 8 |
| 19 | FlagMinBars | 3 |
| 20 | FlagMaxBars | 10 |
| 21 | FlagRangeMaxAtr | 1.0 |
| 22 | MinDistToTargetAtr | 1.5 |
| 23 | MaxAdds | 1 |
| 24 | Contracts | 1 |
| 25 | MaxTradesPerSession | 3 |
| 26 | DailyLossLimitUsd | 200 |
| 27–28 | TradingStart / TradingEnd | 09:30 / 15:55 ET |

Cosmetic dials (free to change anytime): LongBrush, ShortBrush, AddonBrush, PatternOpacityPct (40), ZoneOpacityPct (10), DrawZones (true), DrawRejectedPatterns (false).

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
