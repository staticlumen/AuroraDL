using System;

namespace auroradl;

internal sealed class SpectralAnalyzer
{
    private readonly int _fftSize;
    private readonly double[] _window;
    private readonly double _windowSum;
    private readonly Fft _fft;
    private readonly double[] _re;
    private readonly double[] _im;

    public SpectralAnalyzer(int fftSize)
    {
        _fftSize = fftSize;
        _window = new double[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / fftSize);
        }
        _windowSum = 0;
        for (int i = 0; i < fftSize; i++) _windowSum += _window[i];
        _fft = new Fft(fftSize);
        _re = new double[fftSize];
        _im = new double[fftSize];
    }

    public (double[] magsDb, double peakDb, int peakBin) Analyze(float[] frame)
    {
        int n = _fftSize;
        for (int i = 0; i < n; i++)
        {
            _re[i] = frame[i] * _window[i];
            _im[i] = 0;
        }
        _fft.Transform(_re, _im);

        int bins = n / 2 + 1;
        var magsDb = new double[bins];
        double peakDb = -140;
        int peakBin = 0;
        for (int k = 0; k < bins; k++)
        {
            double mag = Math.Sqrt((_re[k] * _re[k]) + (_im[k] * _im[k]));
            double singleSidedScale = (k == 0 || k == n / 2) ? (1.0 / _windowSum) : (2.0 / _windowSum);
            double amp = mag * singleSidedScale;
            double db = 20 * Math.Log10(Math.Max(amp, 1e-12));
            magsDb[k] = db;
            if (db > peakDb)
            {
                peakDb = db;
                peakBin = k;
            }
        }
        return (magsDb, peakDb, peakBin);
    }
}

