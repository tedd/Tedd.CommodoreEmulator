using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Tedd.MOS65xx.Embedding;

namespace Tedd.MOS65xx.Tests.Embedding;

[TestFixture]
public class AudioOutputTests
{
    private static short[] Ramp(int start, int count) => Enumerable.Range(start, count).Select(i => (short)i).ToArray();

    [Test]
    public void Read_Copies_Mono_To_Every_Channel()
    {
        var audio = new AudioOutput(48000, primeSeconds: 0);
        audio.Write(new short[] { 16384, -32768, 0, 32767 });
        Assert.That(audio.Available, Is.EqualTo(4));
        Assert.That(audio.BufferedSeconds, Is.EqualTo(4 / 48000.0));

        var stereo = new float[2 * 2];
        Assert.That(audio.Read(stereo, 2), Is.EqualTo(2));
        Assert.That(stereo, Is.EqualTo(new[] { 0.5f, 0.5f, -1f, -1f }));

        var quad = new short[3 * 4];
        Array.Fill(quad, (short)7);
        Assert.That(audio.Read(quad, 4), Is.EqualTo(2), "only two samples were left");
        Assert.That(quad[..8], Is.EqualTo(new short[] { 0, 0, 0, 0, 32767, 32767, 32767, 32767 }));
        Assert.That(quad[8..], Is.All.EqualTo((short)7), "Read leaves the rest of the buffer alone");
        Assert.That(audio.Available, Is.EqualTo(0));
        Assert.That(audio.Underruns, Is.EqualTo(0), "Read does not count shortfalls");

        Assert.Throws<ArgumentOutOfRangeException>(() => audio.Read(stereo, 0));
    }

    [Test]
    public void Fill_Pads_With_Silence_And_Counts_The_Shortfall()
    {
        var audio = new AudioOutput(44100, primeSeconds: 0);
        audio.Write(new short[] { 8192, 8192, 8192 });
        var buffer = new float[5 * 2];
        Array.Fill(buffer, 9f);
        Assert.That(audio.Fill(buffer, 2), Is.EqualTo(3));
        Assert.That(buffer[..6], Is.All.EqualTo(0.25f));
        Assert.That(buffer[6..], Is.All.EqualTo(0f));
        Assert.That(audio.Underruns, Is.EqualTo(2));

        var shorts = new short[4];
        Array.Fill(shorts, (short)9);
        Assert.That(audio.Fill(shorts, 1), Is.EqualTo(0));
        Assert.That(shorts, Is.All.EqualTo((short)0));
        Assert.That(audio.Underruns, Is.EqualTo(6));
    }

    [Test]
    public void A_Block_That_Finds_The_Queue_Empty_Is_Primed_With_Silence()
    {
        var audio = new AudioOutput(1000, maxLatencySeconds: 0.5, primeSeconds: 0.01);   // prime = 10 samples
        audio.Write(Ramp(1, 5));
        Assert.That(audio.Available, Is.EqualTo(15));
        var samples = new short[15];
        audio.Read(samples, 1);
        Assert.That(samples, Is.EqualTo(new short[10].Concat(Ramp(1, 5))));

        audio.Write(Ramp(6, 3));
        Assert.That(audio.Available, Is.EqualTo(13), "the queue ran dry, so the next block is primed again");
        audio.Write(Ramp(9, 3));
        Assert.That(audio.Available, Is.EqualTo(16), "not while something is queued");
    }

    [Test]
    public void Blocks_Arriving_Beyond_The_Latency_Limit_Are_Dropped()
    {
        var audio = new AudioOutput(1000, maxLatencySeconds: 0.1, primeSeconds: 0);   // 100 samples
        audio.Write(Ramp(0, 60));
        audio.Write(Ramp(60, 60));
        Assert.That(audio.Available, Is.EqualTo(120), "at the limit (60 queued) a block is still accepted");
        audio.Write(Ramp(120, 60));
        Assert.That(audio.Available, Is.EqualTo(120), "beyond it the whole block is dropped");
        Assert.That(audio.DroppedSamples, Is.EqualTo(60));

        var samples = new short[120];
        audio.Read(samples, 1);
        Assert.That(samples, Is.EqualTo(Ramp(0, 120)), "what was queued comes out in order");
    }

    [Test]
    public void Clear_Discards_What_Was_Queued_Before_It()
    {
        var audio = new AudioOutput(1000, maxLatencySeconds: 0.5, primeSeconds: 0.004);   // prime = 4 samples
        audio.Write(Ramp(1, 20));
        audio.Clear();
        Assert.That(audio.Available, Is.EqualTo(0));
        audio.Write(Ramp(100, 3));
        var samples = new short[7];
        Assert.That(audio.Read(samples, 1), Is.EqualTo(7));
        Assert.That(samples, Is.EqualTo(new short[] { 0, 0, 0, 0, 100, 101, 102 }));
    }

    [Test]
    public void Samples_Survive_Wrapping_Around_The_Ring()
    {
        // The ring holds at least 8192 samples; 2000 rounds push about 40000 through it.
        var audio = new AudioOutput(1000, maxLatencySeconds: 1, primeSeconds: 0);
        int next = 0, expected = 0;
        var random = new Random(1);
        var read = new short[97];
        for (int round = 0; round < 2000; round++)
        {
            int n = random.Next(1, 40);
            audio.Write(Ramp(next, n).Select(s => (short)(s & 0x7FFF)).ToArray());
            next += n;
            int got = audio.Read(read.AsSpan(0, random.Next(1, read.Length)), 1);
            for (int i = 0; i < got; i++, expected++)
                if (read[i] != (short)(expected & 0x7FFF))
                    Assert.Fail($"round {round}: sample {expected} came out as {read[i]}");
        }
        Assert.That(audio.DroppedSamples, Is.EqualTo(0));
    }

    [Test]
    public void One_Writer_And_One_Reader_On_Two_Threads_Keep_Every_Sample_In_Order()
    {
        const int total = 2_000_000;
        var audio = new AudioOutput(48000, primeSeconds: 0);
        var writer = Task.Run(() =>
        {
            var block = new short[957];
            int value = 0;
            while (value < total)
            {
                // Stay below the latency limit so nothing is dropped; this test is about ordering.
                if (audio.Available > 4000)
                {
                    Thread.SpinWait(50);
                    continue;
                }
                int n = Math.Min(block.Length, total - value);
                for (int i = 0; i < n; i++) block[i] = (short)((value + i) & 0x7FFF);
                audio.Write(block.AsSpan(0, n));
                value += n;
            }
        });

        var buffer = new float[512 * 2];
        int expected = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (expected < total && DateTime.UtcNow < deadline)
        {
            int got = audio.Read(buffer, 2);
            for (int i = 0; i < got; i++, expected++)
            {
                float want = (short)(expected & 0x7FFF) / 32768f;
                if (buffer[2 * i] != want || buffer[2 * i + 1] != want)
                    Assert.Fail($"sample {expected}: got {buffer[2 * i]}/{buffer[2 * i + 1]}, expected {want}");
            }
        }
        writer.Wait();
        Assert.That(expected, Is.EqualTo(total));
        Assert.That(audio.DroppedSamples, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_Validates_Its_Arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioOutput(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioOutput(48000, maxLatencySeconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioOutput(48000, maxLatencySeconds: 0.1, primeSeconds: 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioOutput(48000, primeSeconds: -1));
        var audio = new AudioOutput(48000);
        Assert.That(audio.SampleRate, Is.EqualTo(48000));
        Assert.That(audio.MaxLatencySeconds, Is.EqualTo(0.15));
        Assert.That(audio.PrimeSeconds, Is.EqualTo(0.06));
        Assert.That(audio.Capacity, Is.GreaterThan(48000 * 0.21));
    }
}
