using System;

namespace auroradl;

internal sealed class Fft
{
    private readonly int _size;
    private readonly int _levels;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public Fft(int size)
    {
        if ((size & (size - 1)) != 0) throw new ArgumentException("FFT size must be power of two.");
        _size = size;
        _levels = (int)Math.Log2(size);
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            double a = 2 * Math.PI * i / size;
            _cos[i] = Math.Cos(a);
            _sin[i] = Math.Sin(a);
        }
    }

    public void Transform(double[] re, double[] im)
    {
        int n = _size;
        for (int i = 0; i < n; i++)
        {
            int j = ReverseBits(i, _levels);
            if (j <= i) continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1;
            int step = n / size;
            for (int i = 0; i < n; i += size)
            {
                for (int j = i, k = 0; j < i + half; j++, k += step)
                {
                    int l = j + half;
                    double tpre = re[l] * _cos[k] + im[l] * _sin[k];
                    double tpim = -re[l] * _sin[k] + im[l] * _cos[k];
                    re[l] = re[j] - tpre;
                    im[l] = im[j] - tpim;
                    re[j] += tpre;
                    im[j] += tpim;
                }
            }
        }
    }

    private static int ReverseBits(int x, int bits)
    {
        int y = 0;
        for (int i = 0; i < bits; i++)
        {
            y = (y << 1) | (x & 1);
            x >>= 1;
        }
        return y;
    }
}

