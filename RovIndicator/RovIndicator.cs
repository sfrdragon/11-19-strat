// Copyright QUANTOWER LLC. © 2017-2023. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.WebSockets;
using TradingPlatform.BusinessLayer;

namespace RovIndicator
{

    /// <summary>
    /// RVOL-based indicator emitting two compact trading flags only:
    ///  - Line 0 (RvolSignal): -1/0/+1 from normalized RVOL momentum vs threshold
    ///  - Line 1 (HMA_Direction): -2/0/+2 from Close vs (classic or ATR-scaled) HMA
    /// Internals compute RVOL short/long and an HMA-driven composite, optionally normalized by ATR.
    /// Uses HistoricalData[1] (previous bar) for non-repainting, close-price context.
    /// </summary>
    public class RvoIndicator : Indicator
    {
        #region Inputs & Fields

        // RVOL settings
        [InputParameter("RVOL Settings", 0)]
        public readonly string _tagRVOL = "#############";
        [InputParameter("Short Length", 1, 2, 200, 1)]
        public int _LenShort = 14;
        [InputParameter("Long Length", 2, 5, 500, 1)]
        public int _LenLong = 60;
        [InputParameter("Slope Threshold (norm.)", 3, 0.0, 100.0, 0.01, 2)]
        public double _ThrSlope = 2.0;

        // HMA settings
        [InputParameter("HMA Settings", 10)]
        public readonly string _tagHMA = "#############";
        [InputParameter("Use Price for HMA", 11, 0, 1, 1)]
        public int _UsePrice = 1;
        [InputParameter("HMA Length (Composite)", 12, 2, 2000, 1)]
        public int _HmaLenComposite = 14;
        [InputParameter("Use ATR-scaled HMA", 13, 0, 1, 1)]
        public int _UseAtrScaledHma = 0;
        [InputParameter("HMA Length (Pure)", 14, 2, 2000, 1)]
        public int _HmaLenPure = 14;
        [InputParameter("Use Composite HMA for Direction", 15, 0, 1, 1)]
        public int _UseCompositeHmaForDirection = 0;

        // ATR settings
        [InputParameter("ATR Settings", 20)]
        public readonly string _tagATR = "#############";
        [InputParameter("Use ATR Normalization", 21, 0, 1, 1)]
        public int _UseAtrNorm = 1;
        [InputParameter("ATR Length (RVOL)", 22, 2, 2000, 1)]
        public int _AtrLenRvol = 14;
        [InputParameter("ATR Length (HMA)", 23, 2, 2000, 1)]
        public int _AtrLenHma = 14;

        #endregion
        // Dependencies and rolling buffers
        private Indicator atrIndRvol;
        private Indicator atrIndHma;
       
        private double prevRvolNormalized = 0;
        private double currentRvoNormalized = 0;
        private double prevsmoothedRvol = 0;
        private double currentsmoothedRvol = 0;
        private bool rvolOkey = false;


        private RingBuffer<double> rvolShortBuf;
        private RingBuffer<double> rvolLongBuf;
        private RingBuffer<double> hullBuffer;

        // Line indices (ordine di AddLineSeries) — SOLO segnali richiesti
        // 0 = RVOL flag (−1/0/+1), 1 = HMA direzione (−2/0/+2)
        private const int LINE_RVOL_SIGNAL = 0;
        private const int LINE_HMA_DIR = 1;

        public RvoIndicator()
        {
            Name = "RVOL (evolved)";
            Description = "Normalized RVOL momentum and HMA direction (classic or ATR‑scaled)";
            SeparateWindow = true;

            // Expose only the compact, discrete signals
            AddLineSeries("RvolSignal", Color.OrangeRed, 1, LineStyle.Solid); // −1/0/+1
            AddLineSeries("HMA_Direction", Color.DarkCyan, 1, LineStyle.Solid);    // −2/0/+2
        }

        protected override void OnInit()
        {
            this.UpdateType = IndicatorUpdateType.OnBarClose;
            
            // Create separate ATR indicators for RVOL normalization and HMA calculations
            this.atrIndRvol = Core.Indicators.BuiltIn.ATR(this._AtrLenRvol, MaMode.SMMA);
            this.atrIndHma = Core.Indicators.BuiltIn.ATR(this._AtrLenHma, MaMode.SMMA);
            
            this.HistoricalData.AddIndicator(this.atrIndRvol);
            this.HistoricalData.AddIndicator(this.atrIndHma);

            this.rvolShortBuf = new RingBuffer<double>(this._LenShort);
            this.rvolLongBuf = new RingBuffer<double>(this._LenLong);
            // Usa un buffer capiente per supportare HMA classica e scalata (fino a 200)
            this.hullBuffer = new RingBuffer<double>(Math.Max(this._HmaLenComposite, this._HmaLenComposite));
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            this.prevsmoothedRvol = this.currentsmoothedRvol;
            this.prevRvolNormalized = this.currentRvoNormalized;

            var vol = this.HistoricalData[0][PriceType.Volume];


            this.rvolShortBuf.Add(vol);
            this.rvolLongBuf.Add(vol);
            if (this._UsePrice != 0)
                this.hullBuffer.Add(this.HistoricalData[0][PriceType.Close]);
            else
                this.hullBuffer.Add(vol);

            if (rvolLongBuf.IsFull && rvolShortBuf.IsFull && hullBuffer.IsFull)
            {
                var avgL = rvolLongBuf.ToArray().Average();
                var avgS = rvolShortBuf.ToArray().Average();
                var currentRvolL = (avgL > 0) ? (vol / avgL) : 0.0;
                var currentRvolS = (avgS > 0) ? (vol / avgS) : 0.0;

                var hullArray = hullBuffer.ToArray();

                // Calcola le due HMA separate: Composite (eventualmente ATR-scalata) e Pure (non scalata)
                // Use dedicated HMA ATR indicator for HMA length scaling
                var atrForHma = this.atrIndHma?.GetValue(0) ?? 0.0;
                int baseLenComp = Math.Max(2, _HmaLenComposite);
                int effLenComp = baseLenComp;
                if (_UseAtrScaledHma != 0)
                {
                    double atrSafe = Math.Max(atrForHma, 1e-9);
                    int scaled = (int)Math.Round(_HmaLenComposite / atrSafe);
                    // clamp alla dimensione disponibile del buffer e range sensato
                    if (scaled < 2) scaled = 2;
                    if (scaled > 200) scaled = 200;
                    effLenComp = Math.Min(scaled, hullArray.Length);
                }

                // HMA per il composito RVOL (può essere scalata da ATR)
                var hmaClassicComp = TA.HMA_Last(hullArray, Math.Min(baseLenComp, hullArray.Length));
                var hmaScaledComp = TA.HMA_Last(hullArray, effLenComp);
                var hmaComposite = _UseAtrScaledHma != 0 ? hmaScaledComp : hmaClassicComp;
                var hmaCompositeUsed = double.IsNaN(hmaComposite) ? hullArray.Average() : hmaComposite;

                // HMA "pura" per la linea di direzione (non scalata da ATR)
                int baseLenPure = Math.Min(Math.Max(2, _HmaLenPure), hullArray.Length);
                var hmaPure = TA.HMA_Last(hullArray, baseLenPure);
                var hmaPureUsed = double.IsNaN(hmaPure) ? hullArray.Average() : hmaPure;

                // Composite interno (non esposto): media tra RVOL S/L e HMA
                this.currentsmoothedRvol = (currentRvolS + currentRvolL + hmaCompositeUsed) / 3.0;

                // Segnale HMA: −2 se close < HMA, +2 se close > HMA, 0 altrimenti
                var directionHma = _UseCompositeHmaForDirection != 0 ? hmaCompositeUsed : hmaPureUsed;
                int hmaDir = 0;
                if (!double.IsNaN(directionHma))
                {
                    var close = this.HistoricalData[0][PriceType.Close];
                    if (close > directionHma) hmaDir = 2;
                    else if (close < directionHma) hmaDir = -2;
                }
                SetValue(hmaDir, LINE_HMA_DIR);
            }

            // Use dedicated RVOL ATR indicator for RVOL normalization
            var atr = this.atrIndRvol?.GetValue(0) ?? 0.0;
            if (_UseAtrNorm != 0 && atr > 0.0 && !double.IsInfinity(atr) && !double.IsNaN(atr))
                this.currentRvoNormalized = this.currentsmoothedRvol / atr;
            else
                this.currentRvoNormalized = this.currentsmoothedRvol;

            if (this.prevRvolNormalized != 0 && this.currentRvoNormalized != 0)
                this.rvolOkey = Math.Abs(prevRvolNormalized - currentRvoNormalized) > this._ThrSlope;
            var signal = 0.0;
            if (rvolOkey)
                signal = this.currentRvoNormalized > this.prevRvolNormalized ? 1.0 : -1.0;
            SetValue(signal, LINE_RVOL_SIGNAL);

        }

        
    }

    public static class TA
    {
        // Ritorna SOLO l'ultimo valore della HMA(n)
        public static double HMA_Last(double[] values, int n)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (values.Length == 0 || n < 2) return double.NaN;

            int half = n / 2;
            int sroot = (int)Math.Round(Math.Sqrt(n));
            if (sroot < 1) sroot = 1;

            var wHalf = new RollingWma(half);
            var wFull = new RollingWma(n);
            var wOut = new RollingWma(sroot);

            double last = double.NaN;

            for (int i = 0; i < values.Length; i++)
            {
                double a = wHalf.Push(values[i]); // WMA(n/2)
                double b = wFull.Push(values[i]); // WMA(n)

                // Quando entrambe le WMA sono pronte, alimenta l'output WMA(sqrt(n)).
                // Evita di pushare NaN: inquinerebbe le somme interne e renderebbe l'output sempre NaN.
                if (!double.IsNaN(a) && !double.IsNaN(b))
                    last = wOut.Push(2.0 * a - b);
            }

            return last; // NaN finché non ci sono abbastanza campioni
        }

        // --- helper interno: WMA rolling O(1) ---
        private sealed class RollingWma
        {
            private readonly int p;
            private readonly double[] buf;
            private int count, idx;
            private double sum, wsum, denom;

            public RollingWma(int period)
            {
                p = Math.Max(1, period);
                buf = new double[p];
                denom = p * (p + 1) / 2.0;
            }

            public double Push(double v)
            {
                if (p == 1) return v;

                if (count < p)
                {
                    buf[count] = v;
                    sum += v;
                    wsum += v * (count + 1);
                    count++;
                    return (count == p) ? (wsum / denom) : double.NaN;
                }

                double old = buf[idx];
                buf[idx] = v;
                idx = (idx + 1) % p;

                wsum = wsum + p * v - sum;
                sum = sum + v - old;

                return wsum / denom;
            }
        }
    }
}

