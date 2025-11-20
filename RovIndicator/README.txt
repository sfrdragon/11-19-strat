RVOL (evolved) — Flag-Only Indicator

Overview
- Purpose: emits two compact trading signals derived from Relative Volume (RVOL) momentum and an HMA-based trend filter.
- Non‑repainting: computes on the previous bar (HistoricalData[1]) to avoid mid‑bar flips.
- Robustness: guards against division by zero and handles warm‑up before emitting meaningful values.

Inputs
- RVOL Settings:
  - Short Length: window for short RVOL (volume / avg volume).
  - Long Length: window for long RVOL (volume / avg volume).
  - Slope Threshold (norm.): minimum absolute change in normalized RVOL between consecutive bars to consider the signal “valid”.
- HMA Settings:
  - Use Price for HMA: if true, HMA is computed on Close; if false, on Volume.
  - HMA Length: base period for Hull Moving Average.
  - Use ATR‑scaled HMA: if true, the effective HMA period is HMA Length / ATR (clamped to [2..200]).
- ATR Settings:
  - Use ATR Normalization: if true, the composite signal is normalized by ATR before thresholds.
  - ATR Length: period for ATR.

Output Lines (Flag‑Only)
- Line 0 — RvolSignal (−1/0/+1):
  - +1: threshold passed AND normalized RVOL is rising vs previous bar.
  - −1: threshold passed AND normalized RVOL is falling vs previous bar.
  - 0: threshold not passed.
- Line 1 — HMA_Direction (−2/0/+2):
  - +2: Close > selected HMA (classic or ATR‑scaled according to settings).
  - −2: Close < selected HMA.
  - 0: neutral or still warming up.

Internal Logic (Summary)
- RVOL short = vol / avg(vol over Short Length)
- RVOL long = vol / avg(vol over Long Length)
- HMA used = HMA(Price or Volume, effective period = base or base/ATR)
- Composite = average(RVOL short, RVOL long, HMA used)
- Normalized = Composite / ATR (if enabled)
- Threshold check = |Normalized[t] − Normalized[t−1]| ≥ Slope Threshold (norm.)


