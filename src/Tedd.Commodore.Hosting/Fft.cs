using System;

namespace Tedd.Commodore.Hosting;

/// <summary>
/// Small in-place radix-2 FFT with a Hann window, sized once and reused (no allocations per transform).
/// Used by the audio visualizers for a 1024 point magnitude spectrum.
/// </summary>
public sealed class Fft
{
    private readonly int _n;
    private readonly float[] _cos;     // cos(2*pi*k/n),  k < n/2
    private readonly float[] _sin;     // -sin(2*pi*k/n), k < n/2 (forward transform)
    private readonly int[] _reverse;   // bit reversal permutation
    private readonly float[] _window;  // Hann
    private readonly float[] _re;
    private readonly float[] _im;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two", nameof(size));
        _n = size;
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double a = 2.0 * Math.PI * k / size;
            _cos[k] = (float)Math.Cos(a);
            _sin[k] = (float)-Math.Sin(a);
        }
        int bits = 0;
        while ((1 << bits) < size) bits++;
        _reverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _reverse[i] = r;
        }
        _window = new float[size];
        for (int i = 0; i < size; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (size - 1)));
        _re = new float[size];
        _im = new float[size];
    }

    /// <summary>Transform size (number of input samples).</summary>
    public int Size => _n;

    /// <summary>Number of magnitude bins produced (<see cref="Size"/> / 2).</summary>
    public int Bins => _n / 2;

    /// <summary>
    /// Computes the magnitude spectrum of the Hann-windowed 16-bit samples. Magnitudes are normalised so that a
    /// full scale sine (amplitude 32767) gives about 1.0 in its bin.
    /// </summary>
    public void MagnitudeSpectrum(ReadOnlySpan<short> samples, Span<float> magnitudes)
    {
        int n = _n;
        if (samples.Length < n) throw new ArgumentException("Not enough samples", nameof(samples));
        if (magnitudes.Length < n / 2) throw new ArgumentException("Destination too small", nameof(magnitudes));

        for (int i = 0; i < n; i++)
        {
            _re[_reverse[i]] = samples[i] * _window[i] * (1f / 32768f);
            _im[_reverse[i]] = 0f;
        }

        var re = _re;
        var im = _im;
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1;
            int step = n / size;
            for (int start = 0; start < n; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = _sin[k * step];
                    int a = start + k, b = a + half;
                    float tr = re[b] * wr - im[b] * wi;
                    float ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }

        // Hann window coherent gain is 0.5, a sine of amplitude A yields |X| = A * n/2 * 0.5 -> scale by 4/n.
        float scale = 4f / n;
        for (int k = 0; k < n / 2; k++)
            magnitudes[k] = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]) * scale;
    }
}
