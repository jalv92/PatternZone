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
| 1 | Double top | ~29,985 | detected, drawn as an M over the two tops with its neckline, and permitted (a zone band under the tops) |
| 2 | Double bottom / W | ~29,650 | detected, drawn as a W with its neckline, and permitted |

- [ ] **Pin the exact dates first.** Open the chart, find both episodes by
      price, and write the two session dates into the [result log](#result-log)
      below *before* running anything. An episode located after the fact is not
      a test.

### The 6-step checklist

Run once per must-pass episode.

- [ ] **1. Load** — MNQ 1m chart, ETH template, Days to load covering the
      episode date + 2 days before it.
- [ ] **2. Enable** — apply `PatternZoneStrategy` with defaults, plus
      `DrawRejectedPatterns = true` for this phase only (it is the audit tool:
      it shows the patterns the permission gauntlet refused and why).
- [ ] **3. Replay** — Market Replay through the episode at a speed where you
      can watch the bars close.
- [ ] **4. Screenshot** — capture the chart at the moment the pattern is
      complete and drawn. Save it; these captures are also the README's images
      (`docs/assets/hero.png`, `entries.png`, `zones.png`).
- [ ] **5. Compare** — put the capture next to the hand-annotated screenshot.
      Same swings? Same neckline? Break on the same bar?
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

- [ ] The **dashed neckline** is visually distinguishable from the **solid**
      pattern polyline.
- [ ] Pattern and zone opacities are readable on Javier's dark chart theme —
      `PatternOpacityPct` (40) and `ZoneOpacityPct` (10) are free to change,
      they are cosmetic dials.
- [ ] **Zone bands are drawn once per session**, at the RTH open, and span the
      trading window. Bands repeating within one session, or missing at an
      open, is a real defect.
- [ ] No text anywhere on the chart. The design forbids labels, names and
      tables (spec decision #6).

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

- [ ] **6. Daily-loss lockout.**
      Breach `DailyLossLimitUsd` late in a session (lower it temporarily to make
      this reachable). Confirm **no further entries for the rest of that
      session**, and that the **next RTH open (09:30 ET, not the 18:00 overnight
      open) re-arms trading with a fresh baseline** — the limit measures one
      session, not the run.

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

- [ ] **Undo Phase 2 first.** Phase 2 told you to change values and NT8 keeps
      them in the strategy template. Reset **`Contracts` → 1**,
      **`TargetMultiple` → 1.0**, **`DailyLossLimitUsd` → 200**, then read the
      **whole parameter grid** against the README's parameter table. A
      leftover `TargetMultiple = 0.3` produces a complete, plausible, worthless
      backtest, and the generic "defaults untouched" instruction below will not
      catch it.
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
breakeven. **On the breakeven:** at the minimum pattern height the geometry is
reward ≈ 1.5×ATR against risk ≈ 2.0×ATR (height + the 0.5×ATR stop buffer), so
breakeven sits near **57%** before costs — a floor, and a touch higher in
practice, since entry sits past the neckline rather than on it, which shortens
the target and lengthens the stop. It improves as patterns get taller. A
win rate in the low 50s with these defaults is a losing strategy, not a
marginal one.

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
| `PatternOpacityPct` / `ZoneOpacityPct` are editable in the strategy dialog but **do not appear in the optimizer** | Deliberate. They are cosmetic dials; dropping `[NinjaScriptProperty]` takes them off the optimizable-parameter list while leaving them grid-editable. Nothing about how the chart looks should ever be searched over. |

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
