<div align="center">

<h1>PatternZone</h1>

<p>
  <b>A NinjaTrader 8 strategy that trades classic reversal chart patterns on 1-minute MNQ — but only where the pattern forms on a long-memory support or resistance level.</b><br>
  Reversal patterns on their own are dead on NQ intraday; this workspace killed them four times. PatternZone exists to test the one version of the idea those studies left standing.
</p>

<p>
  <a href="#the-result">The result</a> ·
  <a href="#the-hypothesis">The hypothesis</a> ·
  <a href="#how-it-trades">How it trades</a> ·
  <a href="#install">Install</a> ·
  <a href="#limits">Limits</a>
</p>

<p>
  <img src="https://img.shields.io/badge/status-research-orange?style=flat-square" alt="status: research">
  <img src="https://img.shields.io/badge/edge-not%20measured-lightgrey?style=flat-square" alt="edge: not measured">
  <img src="https://img.shields.io/badge/platform-NinjaTrader%208-1f6feb?style=flat-square" alt="platform: NinjaTrader 8">
  <img src="https://img.shields.io/badge/instrument-MNQ%201m-f7931a?style=flat-square" alt="instrument: MNQ 1-minute">
  <img src="https://img.shields.io/badge/tests-141%20passing-brightgreen?style=flat-square" alt="tests: 141 passing">
  <img src="https://img.shields.io/badge/license-MIT-blue?style=flat-square" alt="license: MIT">
</p>

<p><em>Chart captures land here after Phase 1 of <a href="docs/validation.md">docs/validation.md</a> — hero.png (a double top on its zone band), entries.png (a double bottom at the neckline break), zones.png (a session's bands).</em></p>

</div>

---

## The result

**There is no result yet, and no measured edge.** The strategy is built,
compiles, and is deployed; not one of the four validation phases has been run.
Nothing on this page is a claim about profitability.

| Phase | What it establishes | Status |
|---|---|---|
| Unit tests | The detection and decision core behaves as specified | **141 passing** |
| 1 — Visual QA | The detector sees the patterns a human sees | Pending |
| 2 — Order-layer exercises | The order plumbing survives the events that break NT8 strategies | Pending |
| 3 — Frozen backtest | Whether there is anything here at all | Not run |
| 4 — Replay forward, ≥ 20–30 unseen sessions | **The only phase whose result qualifies real money** | Not run |

The protocol, its thresholds and its kill criteria were written down **before**
any P&L existed: [`docs/validation.md`](docs/validation.md). If the strategy
fails, the negative result gets published here and the repo stays up.

Run the tests yourself:

```console
$ dotnet run --project tests
  ...
  ALL PASS
```

## The hypothesis

Classic reversal patterns — double and triple tops and bottoms, head and
shoulders — carry no edge on NQ intraday when taken on their own. That is not a
suspicion; it is the accumulated result of four falsified strategy families in
this workspace: an unconditioned price-pattern search across ~128 trials, a
break-and-retest study whose signal proved placebo-identical, a 15-minute
trendline-retest class with negative expectancy, and a pullback strategy
archived at profit factor 1.06 with an average trade the size of its
commission. Patterns alone are a dead hypothesis and this project does not
revisit it.

What those studies did **not** kill was the level. The break-retest work found
that same-session pivots carry zero information, but it never tested
**long-memory** levels — prior-day high and low, the overnight range, the prior
close, the day's open, round hundreds — the prices that thousands of
participants can see and have been looking at since yesterday.

So the hypothesis under test is narrow and falsifiable: **a reversal pattern is
worth trading only when its defining extremes sit on a long-memory level.** The
pattern supplies the trigger and the geometry; the level supplies the reason
anyone would defend that price. Take the level away and the design predicts its
own failure.

## How it trades

Detection runs on **confirmed swings only** and every decision is made on a
**closed 1-minute bar** — no intrabar triggers, which is where the break-retest
study found its lookahead.

**Entry.** A pattern arms when its swings complete. It fires when a 1-minute bar
closes beyond the neckline by at least 2 ticks; entry is at market on the next
bar's open. Top-family patterns go short, bottom-family long.

**A reversal needs something to reverse.** A pattern only qualifies if its first
defining extreme is the extreme of the 60 bars behind it — a double top after an
up-leg, a double bottom after a down-leg. Without that test the strategy will
happily short an M that formed halfway down a decline, which is not a reversal
at all; it did exactly that during the first visual QA, and this gate is the fix.

**Permission — the thesis.** Before a fired pattern becomes a trade, its
extremes must sit inside one band around a level — the level ± 0.5 × ATR(14),
plus another 0.5 × ATR of proximity allowance:

- Both tops of a double, at least 2 of 3 extremes of a triple, or the head of a
  head-and-shoulders.
- Levels: prior-day high/low, overnight high/low, prior RTH close, day open,
  round 100s. Round 50s exist as a toggle and are **off** — on MNQ they fire
  often enough to make the permission close to no filter at all.
- The band **drawn** on the chart is the half-width only. Permission reaches
  the proximity allowance further, so an extreme can sit outside the painted
  band and still qualify. That gap is deliberate, not a rendering bug.

A pattern that fires away from every level is drawn (with
`DrawRejectedPatterns`) and never traded. With that switch on, every refusal
also prints its reason — `busy`, `session_cap`, `height`, `trend`, `zone` or
`stop` — to the Output window, since the chart deliberately carries no text.

**Stop and target.** The stop sits 10 ticks beyond the pattern's **last
defining swing** — the second top or bottom, the third extreme of a triple, or
the **right shoulder** of a head-and-shoulders. Not the head: the head is what
invalidates the pattern while it is still forming, but the right shoulder is the
level the breakdown has to hold, and anchoring there makes an H&S stop
materially tighter than the geometry it targets. Target is the classic measured
move: the pattern height projected from the break point, ×1.0. Patterns shorter
than 1.5 × ATR are rejected as noise.

Worth stating plainly: near the minimum pattern height this can risk more than
it makes, and **how much more depends on the ATR**, because the stop offset is a
fixed tick distance while the height is ATR-scaled. At ATR(1m) = 5 points a
minimum-height double is 0.75R (breakeven ≈ 57%); at ATR = 10 it is 0.86R
(≈ 54%). Head-and-shoulders is better on both counts — around 1.03R at ATR = 10
— because its stop anchors the shoulder while its target is measured from the
head. There is no single R floor across the pattern families any more; the full
table is in [`docs/validation.md`](docs/validation.md).

**Adds — continuation patterns, never standalone.** While a position is open, a
bull or bear flag can add one tranche: a pole of ≥ 2 × ATR within 8 bars of the
last fill, then 3–10 bars consolidating inside 1 × ATR, then a close out of the
flag in the trade's favour. The aggregate stop moves up to the flag's far edge;
**the target does not move** — every tranche exits at one price, through one
stop and one target covering the whole position. A flag detected with no open
position does nothing at all.

**Risk.** One position at a time, flat to flat. Three base entries per session
(adds do not count). A realised daily loss of $200 flattens and locks out until
the next RTH open. Forced flat at 15:55 ET.

**On the chart.** Every traded pattern draws its own geometry over the candles —
the M or W polyline **including the legs into and out of it**, the flag's pole
and rails, faint zone bands. Without those legs a double bottom renders as a V
with a roof; with them the shape reads the way a human draws it. No neckline (it
was drawn in the first build and taken out after looking at it), and no text
anywhere: the chart shows *why* it entered without becoming a dashboard.

## Bar types

The strategy is **bar-type agnostic** — 1-minute, 30-second, 150-tick, volume or
range charts all work. Detection never referred to clock time, and since
Amendment 4 the session bookkeeping derives each bar's start from the previous
bar's close, which is exact for any bar type.

What does not transfer is the dials. Bar-count dials (trend lookback, max pattern
span, the pole and flag budgets) count **this chart's** bars, and the ATR-scaled
dials breathe with whatever range this chart's bars have. 60 bars of 150-tick MNQ
is not 60 minutes.

So keep one **NT8 Strategy Template per bar type** — Save As `PZ-1m`,
`PZ-150tick`, `PZ-30s` — and let each carry its own dials. For a 150-tick MNQ
chart, `TrendLookbackBars` and `MaxPatternBars` probably want to go **up**, since
each bar covers less time than a 1-minute bar in active trade. That is a
suggestion and **entirely unvalidated**: the validated baseline is 1 Minute, and
[`docs/validation.md`](docs/validation.md) treats each bar-type variant as its own
strategy that has to earn its own evidence.

## Install

1. Copy **both** files to `Documents\NinjaTrader 8\bin\Custom\Strategies\`:
   - `ninjascript/PatternZoneStrategy.cs`
   - `ninjascript/PatternZoneCore.cs`

   Both go in `Strategies\`. NT8 compiles everything under `bin\Custom` into a
   single assembly, so the partner file simply belongs beside the strategy it
   serves — `PatternZoneCore.cs` declares its own `PatternZoneCore` namespace,
   so the usual "the namespace decides the folder" rule does not apply to it.
   That namespace is also deliberately free of any `NinjaTrader` reference, so
   the same file compiles inside NT8 and under the .NET 8 test runner.
2. Press **F5** in the NinjaScript Editor.
3. Apply `PatternZoneStrategy` to a chart meeting these requirements — each one
   fails silently rather than with an error:

| Requirement | Why |
|---|---|
| **MNQ**, front month | Costs, gates and the daily-loss default are at MNQ scale |
| **1-Minute** primary series | Every ATR gate and bar budget counts 1-minute bars |
| The instrument's **full ETH** session template | Overnight high/low are two of the six levels; an RTH-only template deletes them (the strategy logs a warning at the second session open) |
| NT8 time zone = **US Eastern** | Every `HHMM` parameter and the 09:30 / 16:00 / 18:00 boundaries are ET wall clock |
| Days to load = window **+ 2 days** | Prior-day levels come from the session before; the first loaded day has none |

The first trade is possible on the 15th bar (ATR warmup).

## Parameters

**Statistical dials — frozen.** These were fixed before any P&L was seen.
Changing one is a pre-registered amendment that earns its own out-of-sample run,
not a tweak. Three amendments have been made so far — the stop rule and zone
proximity allowance, the prior-trend gate, and the completed drawing legs — all on 2026-08-12, all from
watching the detector draw, and all while no P&L existed. They are written up
in [`docs/design.md`](docs/design.md#amendments).

| Group | Parameters | Defaults |
|---|---|---|
| Detection | Swing strength · top tolerance · head prominence · max pattern span · neckline break · min pattern height · prior-trend gate · trend lookback | 3 · 0.30 ATR · 0.30 ATR · 60 bars · 2 ticks · 1.5 ATR · on · 60 bars |
| Zones | Zone half-width · zone proximity · six level toggles | 0.50 ATR · 0.50 ATR · all on except round 50s |
| Entry | Stop offset · stop buffer (add-on stop only) · target multiple | 10 ticks · 0.50 ATR · 1.0 × height |
| Add-on | Enable · pole min/max · flag min/max bars · flag max range · min room to target · max adds | on · 2.0 ATR / 8 bars · 3–10 bars · 1.0 ATR · 1.5 ATR · 1 |
| Risk | Contracts · max trades/session · window · daily loss limit | 1 · 3 · 09:30–15:55 ET · $200 |

**Cosmetic dials — free.** Zone drawing, the three brushes, pattern and zone
opacity (65% and 10%), pattern line width (4), and rejected-pattern drawing can
be changed at any time without touching the pre-registration. The two opacity
dials and the line width are adjustable in the strategy dialog but
**deliberately excluded from the optimizer** — nothing about how the chart looks
should ever be searched over.

The ATR period (14) is not a parameter. It is pinned by the unit tests to cap
degrees of freedom.

## Limits

- **No measured edge.** See [The result](#the-result). Do not run this on real
  money before Phase 4 of [`docs/validation.md`](docs/validation.md) passes.
- **v1 scope.** Wedges, pennants, cup-and-handle, diamonds, islands and rounding
  patterns are not implemented. Continuation patterns are add-only by design and
  will never open a position.
- **No PropSim mirror**, so there are no placebo or excursion controls. All the
  epistemic honesty this project has lives in the validation protocol: frozen
  defaults, out-of-sample only, forward replay as the gate, kill criteria
  written in advance.
- **No prop-firm envelope.** Sized for a personal account; the drawdown rules of
  funded accounts are not modelled.
- **Single instrument, single series.** Not tested on anything but MNQ.
- Not financial advice. Trading futures can lose more than you deposit.

## License

MIT — see [LICENSE](LICENSE).
