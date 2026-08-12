<div align="center">
  <h1>PatternZone</h1>
  <p><b>Reversal chart patterns gated by long-memory support/resistance zones on 1-minute MNQ.</b> Strategy hypothesis: patterns alone have no edge on NQ intraday, but patterns conditioned on session-level zones test an open theory from the 2026-08 research.</p>
  <p>
    <a href="#overview">Overview</a> ·
    <a href="#status--validation">Status</a> ·
    <a href="#design-spec">Design</a>
  </p>
  <p>
    <img alt="status" src="https://img.shields.io/badge/status-research-blue?style=flat-square">
    <img alt="license" src="https://img.shields.io/badge/license-MIT-green?style=flat-square">
  </p>
</div>

---

## Overview

Automated NinjaTrader 8 strategy that detects and trades classic reversal patterns (double/triple top & bottom, head & shoulders) on 1-minute MNQ, but only when the pattern forms at a long-memory zone (prior-day H/L, overnight H/L, prior close, day open, round numbers). Flag patterns add to open positions only.

## Status & Validation

**Current phase:** Scaffold + test harness. No strategy code; validation pending.

- **Validation protocol:** Visual QA on Market Replay → Frozen backtest (gate: PF ≥ 1.3, avg trade ≥ $5/contract net) → Replay forward ≥ 20–30 sessions.
- **Kill criteria:** If frozen-defaults expectancy ≤ 0 after costs, result published as archived (house tradition).

## Design Spec

Full specification: [`docs/design.md`](docs/design.md)
