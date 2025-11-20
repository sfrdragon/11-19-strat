using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RovIndicator
{
    public static class FastHMA
    {
        // Rolling WMA con update O(1)
        private sealed class RollingWma
        {
            private readonly int period;
            private readonly double[] buf;
            private int count, idx;
            private double sum;    // somma semplice della finestra
            private double wsum;   // somma pesata 1..period

            public RollingWma(int period)
            {
                if (period < 1) throw new ArgumentException("period must be >= 1");
                this.period = period;
                this.buf = new double[period];
            }

            // Inserisce un nuovo valore e ritorna la WMA corrente oppure double.NaN se finestra incompleta.
            public double Push(double value)
            {
                if (count < period)
                {
                    // fase di warm-up: costruisco sum/wsum da zero
                    buf[count] = value;
                    count++;

                    // wsum = v1*1 + v2*2 + ... + vk*k (k = count)
                    wsum += value * count;
                    sum += value;

                    if (count < period) return double.NaN;

                    // La prima WMA quando count==period è già corretta.
                    return wsum / (period * (period + 1) / 2.0);
                }
                else
                {
                    // finestra piena: faccio sliding
                    double old = buf[idx];
                    buf[idx] = value;
                    idx = (idx + 1) % period;

                    // update O(1):
                    // wsum' = wsum + period*new - sum
                    // sum'  = sum + new - old
                    wsum = wsum + period * value - sum;
                    sum = sum + value - old;

                    return wsum / (period * (period + 1) / 2.0);
                }
            }
        }

        // HMA(n) = WMA( 2*WMA(values, n/2) - WMA(values, n), sqrt(n) )
        public static double[] HMA(double[] values, int n, bool roundSqrt = true)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (n < 2) throw new ArgumentException("n must be >= 2");

            int half = n / 2; // standard: floor
            int sroot = roundSqrt ? (int)Math.Round(Math.Sqrt(n)) : (int)Math.Floor(Math.Sqrt(n));
            if (sroot < 1) sroot = 1;

            var wmaHalf = new RollingWma(half);
            var wmaFull = new RollingWma(n);
            var wmaOut = new RollingWma(sroot);

            double[] outHma = new double[values.Length];

            for (int i = 0; i < values.Length; i++)
            {
                double a = wmaHalf.Push(values[i]);  // WMA(n/2)
                double b = wmaFull.Push(values[i]);  // WMA(n)

                double diff = (double.NaN);

                if (!double.IsNaN(a) && !double.IsNaN(b))
                    diff = 2.0 * a - b;

                double h = double.NaN;
                if (!double.IsNaN(diff))
                    h = wmaOut.Push(diff);
                else
                    _ = wmaOut.Push(double.NaN); // avanza la finestra con NaN

                outHma[i] = h;
            }

            return outHma;
        }
    }
}
