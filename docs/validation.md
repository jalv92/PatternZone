# PatternZone — pre-registered validation runbook

**Written 2026-08-12, before any P&L had been seen.** Every gate, threshold and
kill criterion below was fixed while the only thing that existed was code. That
is the whole point: four pattern-strategy families died in this workspace with
the same signature — thresholds judged in-sample, average trade landing on top
of the commission — and the only defence against a fifth is a bar set before the
numbers arrive.

Rules that govern the whole document:

- **Frozen defaults.** The statistical dials in `docs/design.md` §10 are the
  strategy. Phase 3 and Phase 4 run them untouched. Changing one after seeing
  P&L is an amendment (see [Kill criteria](#kill-criteria-and-amendments)),
  never a tweak.
- **Out-of-sample only.** A session used to debug the detector is spent; it
  cannot be counted again as evidence.
- **One bar type, one strategy.** Since Amendment 4 the strategy runs on any bar
  type, but a 150-tick variant is a *different strategy* for evidence purposes —
  different dials, different ATR scale, different trade population. Phases 1–4
  apply per variant and results never transfer between them. The pre-registered
  baseline is **1 Minute**.
- **A negative result gets published.** If the strategy fails, the README says
  so in plain language and the repo stays up. House tradition — the negative
  results are the reason the workspace still has money.

Phases run in order. A failure stops the run: there is no point backtesting a
detector that draws the wrong patterns.

---

## Phase 0 — Prerequisites (the chart)

Every item here is a way to get silently wrong answers rather than an error
message. Check them before Phase 1 and re-check them whenever you rebuild the
chart.

- [ ] **Instrument = MNQ**, front month. The costs, the gate table and the
      `DailyLossLimitUsd` default are all at MNQ scale.
- [ ] **Primary series = 1 Minute.** Every ATR-scaled gate and every bar budget
      in the core counts 1-minute bars. On any other series the strategy logs a
      warning at `DataLoaded` and then runs something that is not the design.
- [ ] **The instrument's FULL ETH session template**, not an RTH one. The
      overnight high/low are two of the six permission levels; an RTH-only
      template deletes them without an error. Two symptoms to watch for: the
      warning `a full RTH session opened with no overnight bars` in the log at
      the **second** loaded session open (not the first — the first session is
      legitimately empty of prior data), and overnight zone bands missing from
      the chart.
- [ ] **NT8 global time zone = US Eastern** (Tools > Options > General). Every
      `HHMM` parameter and the 09:30 / 16:00 / 18:00 session boundaries in the
      code are ET wall clock, read off the bar timestamps.
- [ ] **Days to load ≥ the QA window + 2 extra days.** Prior-day high/low,
      prior close and the overnight range all come from the session *before*
      the one being judged, and the first loaded day has none of them.
- [ ] **ATR warmup: 15 bars.** The first trade is possible on the 15th bar fed
      to the ATR, not the first. Pattern arming needs more than that anyway —
      three confirmed swings at `SwingStrength = 3`, each confirmed 3 bars late.

---

## Phase 1 — Visual QA (Market Replay)

**Question this phase answers:** does the detector see what Javier sees? Nothing
downstream matters if it does not.

### The two must-pass episodes

These are the hand-annotated screenshots from 2026-08 that motivated the whole
project. Both on **MNQ 09-26, 1-minute**:

| # | Episode | Approx. price | Must be |
|---|---|---|---|
| 1 | Double top | ~29,985 | detected, drawn as an M over the two tops, breaking on the bar a human would call the break, and permitted (a zone band under the tops) |
| 2 | Double bottom / W | ~29,650 | detected, drawn as a W, breaking on the same bar a human would, and permitted |

The neckline is **not drawn** since Amendment 1, so it cannot be compared
against the hand annotation directly. Judge it by proxy: the entry arrow lands
on the bar whose close broke the level you would have drawn.

- [ ] **Pin the exact dates first.** Open the chart, find both episodes by
      price, and write the two session dates into the [result log](#result-log)
      below *before* running anything. An episode located after the fact is not
      a test.

### The 6-step checklist

Run once per must-pass episode.

- [ ] **1. Load** — MNQ 1m chart, ETH template, Days to load covering the
      episode date + 2 days before it.
- [ ] **2. Enable** — apply `PatternZoneStrategy` with defaults, plus
      `DrawRejectedPatterns = true` for this phase only. It is the audit tool,
      and since Amendment 2 it works in two places at once: refused patterns are
      drawn faintly on the chart **and** each one prints its reason to the
      **Output window** (`REJECTED <reason> <pattern kind>`). The chart itself
      still carries no text, so the Output window is where the WHY lives. The
      vocabulary is `busy` / `session_cap` / `height` / `trend` / `zone` /
      `stop`, and it is evaluated in that order — a pattern that fails two gates
      reports the first one only. `stop` is the rarest and the most interesting:
      a sloped neckline outran the pattern's own right shoulder, so the stop
      would have landed on the wrong side of the entry.
- [ ] **3. Replay** — Market Replay through the episode at a speed where you
      can watch the bars close.
- [ ] **4. Screenshot** — capture the chart at the moment the pattern is
      complete and drawn. Save it; these captures are also the README's images
      (`docs/assets/hero.png`, `entries.png`, `zones.png`).
- [ ] **5. Compare** — put the capture next to the hand-annotated screenshot.
      Same swings? Entry on the same bar you would have taken?
- [ ] **6. Verdict** — PASS or FAIL in the result log, with the reason. A FAIL
      stops the run and goes back to the detector.

### What to exclude from judgment

- [ ] **Ignore the FIRST loaded day entirely.** Three separate reasons, all
      benign: prior-day levels are NaN so the zone engine runs on fewer levels;
      `DayOpen` is whatever the first RTH bar's open happens to be, which is an
      arbitrary mid-session price if the history starts intraday; and the zone
      bands render thin because ATR is still warming up. None of this is a bug
      and none of it happens on day two.

### Eyeball checks (the drawing layer)

- [ ] **No neckline is drawn** (Amendment 1). Only the swing polyline, the
      flag's pole and rails, and the zone bands. A neckline segment appearing on
      the chart means a stale build is loaded.
- [ ] Pattern and zone opacities and the stroke weight are readable on Javier's
      dark chart theme — `PatternOpacityPct` (65), `ZoneOpacityPct` (10) and
      `PatternLineWidth` (4) are free to change, they are cosmetic dials.
- [ ] **Zone bands are drawn once per session**, at the RTH open, and span the
      trading window. Bands repeating within one session, or missing at an
      open, is a real defect.
- [ ] **Do not judge permission by band pixels.** Two independent reasons, both
      by design:
      - The bands are drawn with the ATR as it stood at the session open, while
        pattern permission is tested with the ATR at the moment the pattern
        fires. The two differ.
      - **The permission band is wider than the drawn band** (Amendment 1). The
        drawing paints `ZoneHalfWidthAtr` (0.50 × ATR); permission uses
        `ZoneHalfWidthAtr + ZoneProximityAtr` (1.00 × ATR at the defaults), so
        an extreme sitting a full band-width outside the painted band can still
        be permitted, on purpose.

      Judge permission by the runbook rules and the rejected-pattern markers,
      not by the drawing. To see the strict band on the chart instead, set
      `ZoneProximityAtr = 0` — but that is a different strategy, not a display
      option, so put it back before Phase 3.
- [ ] No text anywhere on the chart. The design forbids labels, names and
      tables (spec decision #6). Rejection reasons appear in the **Output
      window**, never on the chart.
- [ ] **Every drawn reversal has a trend behind it** (Amendment 2). Scroll back
      from each pattern: a short (M) must sit at the top of a rise, a long (W)
      at the bottom of a fall. A top armed mid-decline is the exact defect this
      gate was added for — if one appears, the gate is broken, not the drawing.
      Expect `REJECTED trend ...` lines in the Output window for the ones it
      caught.

---

## Phase 2 — Order-layer exercises (Market Replay)

**Question this phase answers:** does the order plumbing survive the events that
actually break NT8 strategies? These seven exercises exist because each one is a
hole that was found and closed during review; they are the regression suite for
the parts no unit test can reach.

> **Deviating from the defaults is allowed and expected here.** Nothing in
> Phase 2 is measured as P&L, so forcing a scenario with `Contracts = 2` or a
> temporarily smaller `TargetMultiple` costs nothing. Phase 3 is where the
> defaults must be untouched.

- [ ] **1. Engine liveness across a fast target hit.**
      Force or wait for a trade whose **target fills on the same bar as the
      entry** (a temporarily small `TargetMultiple` makes this easy to
      reproduce). Then keep replaying and confirm **a second trade is taken
      later in the session**.
      *Failure symptom (this was a real bug, now fixed):* the Output window goes
      quiet after the first entry and the chart fills with rejected-pattern
      markers — the engine was stranded mid-state and rejects everything after
      as "busy".

- [ ] **2. Position size after an add.**
      Take a trade that gets one flag add. The account panel must show
      **base + adds** (2 contracts at `Contracts = 1, MaxAdds = 1`).
      *If a discrepancy is suspected*, temporarily add a print at the top of
      **`OnBarUpdate`** — not inside `SubmitExits` — and diff a whole session:
      ```csharp
      Print(Name + " qty check: internal=" + _qty + " position=" + Position.Quantity);
      ```
      The two must agree on every line. Remove the print afterwards.
      **Why not `SubmitExits`:** it runs inside `OnExecutionUpdate`, after
      `_qty` has taken the fill being reported and before `Position.Quantity`
      necessarily reflects it. `_qty` legitimately leads there by the size of
      that fill; comparing at that point manufactures a failure. The tracker
      sums executions deliberately, because the brackets must cover the fill
      being reported *now* — do not "fix" it to read `Position` instead, which
      re-opens the bug it was written to avoid.

- [ ] **3. Orphan-flatten net actually submits.**
      With `Contracts = 2`, or by rewinding Playback while a position is open,
      produce a fill with no tracked trade behind it. The Output window prints
      `execution with no tracked trade behind it — flattening`. **Then check the
      Orders tab**: a `PZ_Flatten` order must actually be there. A print with no
      order is the failure — it means real contracts are riding unprotected.

- [ ] **4. Add resize with a gap through the new stop.**
      Find or force an add whose new aggregate stop lands on the wrong side of
      the market (a fast bar through the flag). The stop is rejected on
      submission; the strategy must print
      `REJECTED (...) — position unprotected, flattening` and **flatten**, not
      hold the position without a stop.
      **The platform terminating the strategy on that rejection is also an
      acceptable outcome**, not a failure: `RealtimeErrorHandling` is left at
      NT8's default `StopCancelClose`, which cancels working orders, closes the
      position and stops the strategy — the outer net. Check the Log/Output to
      see which net fired first: a `REJECTED … flattening` print means the
      strategy's own inner net ran before the platform stepped in.

- [ ] **5. Hand-cancel a bracket leg in Chart Trader.**
      Mid-position, cancel one leg by hand. Expected, and this asymmetry is
      deliberate:
      - The `UNPROTECTED` / `take profit removed` warning prints **one bar
        later** (the detector is deferred so it cannot fire on our own
        cancel-replace echoes).
      - A hand-cancelled **target stays cancelled** for the rest of that trade.
      - A hand-cancelled **stop comes back** on the next add resize.
      Both directions are safe: protection returns, profit-taking does not get
      resurrected against the operator's decision.
      If a hand-cancel is followed by a rejection, **the platform terminating
      the strategy is an acceptable outer-net outcome** (`RealtimeErrorHandling`
      is at NT8's default `StopCancelClose`). Read the Log/Output to see which
      net fired: a `REJECTED … flattening` print means the strategy's own inner
      net ran first.

- [ ] **6. Daily-loss lockout.**
      Breach `DailyLossLimitUsd` late in a session (lower it temporarily to make
      this reachable). Confirm **no further entries for the rest of that
      session**, and that the **next RTH open (09:30 ET, not the 18:00 overnight
      open) re-arms trading with a fresh baseline** — the limit measures one
      session, not the run.

- [ ] **6b. Daily profit target (Amendment 6).**
      Same exercise on the winning side: set `DailyProfitTargetUsd` low enough to
      be reachable and confirm the hit **flattens and locks out** exactly like the
      loss limit — the log line reads `daily profit target N USD`, the managed
      position goes flat, and in ATM mode a live ATM is closed on the same bar
      (`closing ATM (daily limit lockout)`). Then confirm the next RTH open
      re-arms it.

- [ ] **6c. Account-wide (Amendment 6). Two charts, one account.**
      Run PatternZone on two instruments (MNQ and NQ) on the same account with
      **Account-wide** ticked on both. Drive ONE of them into its loss limit and
      confirm the other **locks out on its next closed bar without having traded**,
      with a log line naming the account-wide total and the per-instrument
      breakdown. Then confirm the honest scope: a breach in PatternZone does
      **not** stop LatigoBreak or any other strategy on that account — only
      PatternZone instances publish into the shared record.

- [ ] **6d. Account-wide survives a restart, resets on the day.**
      After 6c, disable and re-enable the locked-out strategy **in the same
      session**: it must re-lock immediately rather than resume trading (the breach
      broadcast outlives a strategy restart on purpose). The next RTH open must
      then clear both the broadcast and every contribution.

- [ ] **8. ATM mode (only if you intend to trade it).** Realtime or Playback
      only. Tick `UseAtmStrategy`, pick a template, and confirm in order:
      the entry arrives as an **ATM strategy** (Chart Trader shows it, and its
      own stop/target appear in the Orders tab — ours do not); **no flag add**
      ever fires; the **15:55 window flatten closes the ATM** (a `closing ATM`
      print, then the position and the template's brackets are gone); the
      **daily-loss lockout counts ATM PnL** (lower the limit, take a losing ATM
      trade, confirm the lockout — `SystemPerformance` does not see these trades,
      so this is the only thing proving the accumulator works). Then check the
      two refusals: an unknown template name → an error in the log and **no
      trades**; the same setup in the Strategy Analyzer → one warning and **no
      trades**, never a silent fallback to the managed path.

- [ ] **7. At least one full session with `Contracts > 1`.**
      Every partial-fill path in the order layer is unreachable at the default
      of 1 contract, which means the default configuration never exercises them.
      Run one whole session at `Contracts = 2` and confirm no orphan flattens,
      no unprotected-position prints, and brackets whose quantity matches the
      position after each partial.

---

## Phase 3 — Frozen backtest (Strategy Analyzer)

**Question this phase answers:** is there anything here at all? A pass is
permission to spend time on Phase 4, nothing more — backtest numbers on data the
design was shaped around are not evidence of an edge.

Setup, all of it mandatory:

- [ ] **Undo Phases 1 and 2 first.** Both told you to change values and NT8
      keeps them in the strategy template. Reset **`Contracts` → 1**,
      **`TargetMultiple` → 1.0**, **`DailyLossLimitUsd` → 200**,
      **`ZoneProximityAtr` → 0.50** (Phase 1 may have zeroed it to eyeball the
      strict band), **`StopOffsetTicks` → 10**, **`UseTrendFilter` → true** and
      **`TrendLookbackBars` → 60**, then read the **whole
      parameter grid** against the README's parameter table. A leftover
      `TargetMultiple = 0.3` produces a complete, plausible, worthless backtest,
      and the generic "defaults untouched" instruction below will not catch it.
- [ ] Strategy Analyzer, MNQ, the **longest available 1-minute history**.
      (NQ substitution is allowed only as a documented fallback if MNQ history
      is short, with costs kept at MNQ scale — write it in the result log.)
- [ ] **Full ETH session template in the Analyzer's own Data Series dialog.**
      The Analyzer does not inherit a chart's template — it has its own
      selector, and an RTH-only choice here silently deletes 2 of the 6
      permission levels exactly as it would on a chart. After the run, check
      the **Log tab** for `a full RTH session opened with no overnight bars`
      before trusting a single number.
- [ ] **Order Fill Resolution = High, 1 Tick.**
- [ ] **Commission $1.34 per round turn** per contract (spec range $1.24–1.54).
- [ ] **Slippage 2 ticks** ($1.00 on MNQ). Combined assumption:
      **≈ $2.3–2.5 per round turn per contract**.
- [ ] **Defaults untouched.** No optimizer, no walk-forward, no parameter
      search. One run.
- [ ] **`UseAtmStrategy` OFF — always, and this is not optional.** The Strategy
      Analyzer ignores `AtmStrategyCreate` entirely (it is not backtestable), so
      an ATM-mode Analyzer run takes zero trades. Phase 3 measures the managed
      path or it measures nothing.
- [ ] **`AccountWide` OFF, and `DailyProfitTargetUsd` at 0 — both are the
      defaults, confirm them anyway (Amendment 6).** A live profit target
      truncates sessions and makes the run un-comparable with the pre-registered
      table; account-wide keys its shared record by account name, which in the
      Analyzer is one virtual account shared by every optimization iteration.

### The gate table (house table, MNQ-scaled)

Pre-registered. All seven must clear.

| Metric | Minimum | Ideal |
|---|---|---|
| Avg trade, net | ≥ **$5** per contract (≈2× cost) | $8–15 |
| Profit factor | ≥ **1.3** | 1.5–2.0 |
| Expectancy | ≥ **0.10R** | 0.20–0.35R |
| Sample | ≥ **100 trades** | 200–300, multi-regime |
| Sharpe (annualised) | ≥ **1.0** | 1.5–2.5 |
| Commissions / gross | ≤ **30%** | ≤ 20% |
| Equity R² | ≥ **0.65** | ≥ 0.80 |

Report alongside them, not gating but recorded: max drawdown (≤ ⅓ of annual
net), Sortino (≥ 1.5), positive months (≥ 55%), and win rate against its
breakeven.

**On the breakeven — recomputed for Amendment 1, and there is no longer one
number.** The old derivation (reward 1.5×ATR against risk 2.0×ATR → 0.75R,
breakeven 57%) came from a stop that was a pure ATR multiple of the pattern
extreme. Amendment 1 anchors the stop on the pattern's **last defining swing**
plus a **fixed 10 ticks** (2.5 MNQ points), which changes two things:

- Risk is now `(neckline → last defining swing) + 2.5 points`. For a **double**
  that is the full pattern height only when the second top is the higher of the
  two; when the first top is higher it is up to `TopToleranceAtr` (0.30×ATR)
  less, so the table below is a conservative floor for doubles rather than an
  exact figure. For a **triple** it is up to the same tolerance less; for an
  **H&S** it is
  at least `HeadProminenceAtr` (0.30×ATR) less, because the stop anchors the
  right shoulder while the target is measured from the head.
- Because the offset is a fixed tick distance and the height is ATR-scaled,
  **R now depends on the prevailing ATR.** A single closed-form floor across all
  pattern families no longer exists, and claiming one would be dishonest.

At the minimum pattern height (`MinPatternHeightAtr` = 1.5), reward = 1.5×ATR
and, writing A for ATR(1m) in points:

| ATR(1m) | Double: R (breakeven) | H&S: R (breakeven) |
|---|---|---|
| 5 pts | 7.5 / 10.0 = **0.75R** (57.1%) | 7.5 / 8.5 = **0.88R** (53.1%) |
| 10 pts | 15 / 17.5 = **0.86R** (53.8%) | 15 / 14.5 = **1.03R** (49.2%) |
| 15 pts | 22.5 / 25.0 = **0.90R** (52.6%) | 22.5 / 20.5 = **1.10R** (47.7%) |

**Use the double row at the ATR the backtest actually ran at as the reference
case** — doubles are the worst geometry of the three and the most common
pattern. Triples land between the two columns. Every number above is a floor
that improves as patterns get taller, and is a touch optimistic in practice:
entry sits past the neckline rather than on it, which shortens the target and
lengthens the stop. Record the run's median ATR alongside the win rate, or the
comparison is not interpretable.

### Red flags — too good is a defect, not a result

- [ ] Win rate > 70% at R ≥ 1:1
- [ ] Profit factor > 3
- [ ] Sharpe > 3
- [ ] No losing streak longer than 3

Any of these fires: stop and hunt for lookahead, optimistic fills or an
overfit before celebrating anything.

### Base vs adds, reported separately

- [ ] Run once with `EnableFlagAddon = true` and once with it **false**, and
      report both. The flag add-on is a separate hypothesis riding on the same
      entries. **If adds do not improve expectancy or drawdown, the v1.1 default
      for `EnableFlagAddon` flips to false** — pre-registered now so it is not
      a judgment call later.

---

## Phase 4 — Replay forward (THE gate)

**This is the only phase whose result qualifies real money.**

- [ ] **≥ 20–30 RTH sessions the strategy has never seen.** Same standard as
      RlpLong. Sessions burned during Phases 1–3 do not count.
- [ ] Defaults untouched, costs as Phase 3, one pass — no restarting a session
      that went badly.
- [ ] Judge against the same gate table. A Phase 3 pass followed by a Phase 4
      failure is the normal outcome for a strategy with no edge, and it is the
      whole reason this phase exists.

---

## Kill criteria and amendments

- [ ] **Kill:** frozen-defaults expectancy **≤ 0 after costs** → the project
      result is negative. It gets written into the README in plain language, the
      repo stays public, and the `status` badge changes to
      `archived — no edge`.
- [ ] **Amendment, not a tweak:** any re-tune after seeing P&L is written down
      — what changed, why, when — and earns **its own out-of-sample shot**. A
      parameter quietly changed between runs turns every number that follows
      into noise. Silent re-tuning is how the previous four families produced
      results that looked fine and were not.

---

## Known accepted behaviors

Documented deliberately. These are **not** defects to report and **not** things
to fix during validation — they are decisions with reasons.

| Behavior | Why it is accepted |
|---|---|
| A position held **across a session open** loses the flag add-on for the rest of that trade | The session open resets the engine's per-session state and disarms the flag detector. A trade spanning that boundary keeps its brackets and exits normally; it just takes no more adds. |
| **After a rejected add**, the engine takes no more adds for that trade | Fails safe. Re-arming the detector would need an anchor fill price the order layer never received, so the choice is between guessing and stopping. It stops. |
| A **holiday or otherwise bar-less overnight** leaves the *previous* night's range standing as the overnight level, silently | Accepted residual. The overnight accumulators only roll over when overnight bars actually arrive, so a missing night inherits. Rare, and the failure direction is a stale level rather than a wrong one. |
| `PatternOpacityPct` / `ZoneOpacityPct` / `PatternLineWidth` are editable in the strategy dialog but **do not appear in the optimizer** | Deliberate. They are cosmetic dials; dropping `[NinjaScriptProperty]` takes them off the optimizable-parameter list while leaving them grid-editable. Nothing about how the chart looks should ever be searched over. |
| In **ATM mode** the strategy is signals + entry only; everything after the fill belongs to the template | Amendment 5, and it is the whole point of the mode. The ATM template supplies the stop, the target and the position size, so the stop offset/buffer, target multiple and `Contracts` all do nothing, the flag add-on is disabled, and PatternZone's own bracket is never submitted. What PatternZone still owns: which pattern trades, the trading-window flatten, and the daily-loss lockout (fed from the ATM's own realized PnL, since `SystemPerformance` never books an ATM trade). Phase 3 must run with ATM **off**. |
| On a **tick/volume/range** chart, a bar straddling 09:30 counts as overnight | Amendment 4. A bar belongs to the session it *began* in, so a bar whose ticks run 09:29:50 → 09:30:12 is overnight: its post-open ticks count toward the overnight range, and `DayOpen` becomes the open of the first bar that starts fully inside RTH — seconds after the 09:30:00 print. Time charts are unaffected (their bar start is exact). |
| A pattern is permitted whose extreme sits **visibly outside** the drawn zone band | Amendment 1. Permission uses half-width + `ZoneProximityAtr` (1.00×ATR at the defaults); the drawing paints the half-width alone (0.50×ATR). Not a drawing bug and not a permission bug — the two were separated on purpose so near-the-level patterns qualify without fattening every painted band. |
| **Account-wide** reaches this instance only on its **next closed bar** | Amendment 6. PatternZone is `Calculate.OnBarClose` with no tick series, so a breach broadcast by another instrument is acted on at the next bar close — up to 60 s on the 1-minute baseline, and unbounded on a tick/volume/range chart where a bar can take arbitrarily long to close. LatigoBreak reacts within the second because it carries a 1-tick series; adding one here would be a far larger change than this amendment, and the strategy's own limits still act immediately on the event paths (a closing fill, an ATM close). |
| **Account-wide sums PatternZone instances only** — not LatigoBreak, not TBStrategy, not manual trades | Amendment 6, and it is what the mechanism can honestly deliver: the total is built from each instance's own event-ordered numbers rather than the account's aggregates, which double-count a trade the instant its target fills (LatigoBreak, live, 2026-08-10: a $750 target flattened everything at $539 realized). The registry is public (`PatternZoneShell.DailyGovernor`) so other strategies can publish into it later; until they do, the label "all markets" means all of *your PatternZone* markets. |
| With **Account-wide ON** the limits also count **open** P&L; with it OFF they stay **realized-only** | Amendment 6, deliberate asymmetry. Off is the frozen per-strategy behavior, unchanged byte-for-byte. On adds unrealized because a shared governor exists to close everything before an account-level drawdown rule fires and prop firms measure open P&L — so a $1,200 loser still open on another chart has to be visible. Consequence to expect in testing: the same P&L can trip the limit *earlier* with the switch on. |
| **Account-wide in the Strategy Analyzer is untested and should stay OFF** | Amendment 6, stated honestly rather than guessed. The shared record is keyed by `Account.Name`, and in the Analyzer that is one virtual account: if it is non-null there, every concurrently-running optimization iteration would publish into the SAME governor and one iteration's breach would lock out the others — silently corrupting the run. Phase 3 and every optimization must run with `AccountWide` **off** (which is the default). Its home is realtime and Playback. |
| Spec §7's flag condition that **net drift be flat or against the position** is not a separate check in the code | Deliberate, and recorded here pre-P&L so the frozen strategy is unambiguous. Drift is already bounded from both sides: the envelope cap (`FlagRangeMaxAtr` × ATR) caps how far the consolidation can travel, and the no-close-beyond-the-pole-extreme rule restarts the flag the moment price pushes on in favour. The frozen machine ships without the explicit test; adding one later is an amendment, not a fix. |

---

## Result log

Fill in as each phase completes. This section is the audit trail; it stays in
the repo whatever the outcome.

**Phase 1 — Visual QA**

| Item | Value |
|---|---|
| Episode 1 (double top ~29,985) — session date | _pending_ |
| Episode 1 verdict | _pending_ |
| Episode 2 (double bottom ~29,650) — session date | _pending_ |
| Episode 2 verdict | _pending_ |
| Exclusions / notes | |

**Phase 2 — Order-layer exercises**

| # | Exercise | Verdict |
|---|---|---|
| 1 | Engine liveness across a fast target hit | _pending_ |
| 2 | Position size after an add | _pending_ |
| 3 | Orphan-flatten actually submitted | _pending_ |
| 4 | Add resize rejected → flatten | _pending_ |
| 5 | Hand-cancel asymmetry | _pending_ |
| 6 | Daily-loss lockout and re-arm | _pending_ |
| 7 | Full session at `Contracts > 1` | _pending_ |

**Phase 3 — Frozen backtest**

| Field | Value |
|---|---|
| Instrument / period | _pending_ |
| Trades | _pending_ |
| Avg trade net · PF · expectancy | _pending_ |
| Sharpe · commissions/gross · equity R² | _pending_ |
| Base-only vs base+adds | _pending_ |
| Red flags fired | _pending_ |
| Verdict | _pending_ |

**Phase 4 — Replay forward**

| Field | Value |
|---|---|
| Sessions (≥ 20–30, unseen) | _pending_ |
| Trades | _pending_ |
| Gate table result | _pending_ |
| **Verdict** | _pending_ |
