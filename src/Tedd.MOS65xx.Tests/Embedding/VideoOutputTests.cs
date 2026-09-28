using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Tedd.MOS65xx.Embedding;
using Tedd.MOS65xx.Emulator.Video;
using Tedd.MOS65xx.Hosting;

namespace Tedd.MOS65xx.Tests.Embedding;

[TestFixture]
public class VideoOutputTests
{
    /// <summary>A frame whose every pixel is <paramref name="fill"/>.</summary>
    private static VideoFrame Solid(long number, uint fill, int width = 8, int height = 4, VideoSource source = VideoSource.VicII)
    {
        var pixels = new uint[width * height];
        Array.Fill(pixels, fill);
        return new VideoFrame(pixels, number, source, width, height, 0, 0, width, height);
    }

    [Test]
    public void Nothing_To_Acquire_Before_The_First_Frame()
    {
        var output = new VideoOutput();
        Assert.That(output.HasNewFrame, Is.False);
        Assert.That(output.TryAcquire(out var frame), Is.False);
        Assert.That(frame.IsEmpty, Is.True);
        Assert.That(output.Current.IsEmpty, Is.True);
    }

    [Test]
    public void Acquire_Takes_The_Newest_Frame_Once()
    {
        var output = new VideoOutput();
        int events = 0;
        output.FramePresented += () => events++;

        output.PresentFrame(Solid(1, 0xFF000001));
        Assert.That(output.HasNewFrame, Is.True);
        Assert.That(output.TryAcquire(out var frame), Is.True);
        Assert.That((frame.FrameNumber, frame.Width, frame.Height), Is.EqualTo((1L, 8, 4)));
        Assert.That(frame.GetRow(3)[7], Is.EqualTo(0xFF000001));

        Assert.That(output.HasNewFrame, Is.False);
        Assert.That(output.TryAcquire(out var again), Is.False, "nothing new");
        Assert.That(again.FrameNumber, Is.EqualTo(1), "the current frame is handed back for a redraw");

        output.PresentFrame(Solid(2, 0xFF000002));
        output.PresentFrame(Solid(3, 0xFF000003));
        output.PresentFrame(Solid(4, 0xFF000004));
        Assert.That(output.TryAcquire(out var newest), Is.True);
        Assert.That(newest.FrameNumber, Is.EqualTo(4), "the newest frame wins");
        Assert.That(newest.GetRow(0)[0], Is.EqualTo(0xFF000004));
        Assert.That(output.FramesPresented, Is.EqualTo(4));
        Assert.That(output.FramesAcquired, Is.EqualTo(2));
        Assert.That(output.FramesSkipped, Is.EqualTo(2), "frames 2 and 3 were never taken");
        Assert.That(events, Is.EqualTo(4));
    }

    [Test]
    public void An_Acquired_Frame_Is_Not_Touched_By_Later_Frames()
    {
        var output = new VideoOutput();
        output.PresentFrame(Solid(1, 0xFF111111));
        output.TryAcquire(out var held);
        for (int i = 2; i < 10; i++)
            output.PresentFrame(Solid(i, 0xFF000000u | (uint)i));
        Assert.That(held.FrameNumber, Is.EqualTo(1));
        for (int y = 0; y < held.Height; y++)
            Assert.That(held.GetRow(y).ToArray(), Is.All.EqualTo(0xFF111111u));
        Assert.That(output.Current.FrameNumber, Is.EqualTo(1));
    }

    [Test]
    public void Frame_Size_Follows_The_Source()
    {
        var output = new VideoOutput();
        output.PresentFrame(Solid(1, VicII.Palette[6], VicII.VisibleArea.Width, VicII.VisibleArea.Height));
        output.TryAcquire(out var vic);
        Assert.That((vic.Width, vic.Height, vic.Source), Is.EqualTo((384, 272, VideoSource.VicII)));

        output.PresentFrame(Solid(2, Vdc8563.Palette[1], Vdc8563.FrameWidth, Vdc8563.FrameHeight, VideoSource.Vdc));
        output.TryAcquire(out var vdc);
        Assert.That((vdc.Width, vdc.Height, vdc.Source), Is.EqualTo((768, 272, VideoSource.Vdc)));
        var indexes = new byte[vdc.GetSizeInBytes(FramePixelFormat.Indexed8)];
        vdc.CopyTo(indexes, FramePixelFormat.Indexed8);
        Assert.That(indexes, Is.All.EqualTo((byte)1));

        // Larger than anything the machines produce: the buffer grows.
        output.PresentFrame(Solid(3, 0xFF000000, 1000, 300));
        output.TryAcquire(out var big);
        Assert.That((big.Width, big.Height), Is.EqualTo((1000, 300)));
    }

    [Test]
    public void Producer_And_Consumer_On_Two_Threads_Never_See_A_Torn_Frame()
    {
        const int frames = 20000;
        var output = new VideoOutput();
        var producer = Task.Run(() =>
        {
            var pixels = new uint[64 * 32];
            for (int n = 1; n <= frames; n++)
            {
                Array.Fill(pixels, (uint)n);
                output.PresentFrame(new VideoFrame(pixels, n, VideoSource.VicII, 64, 32, 0, 0, 64, 32));
            }
        });

        long last = 0;
        int acquired = 0;
        while (last < frames)
        {
            if (!output.TryAcquire(out var frame))
            {
                if (producer.IsCompleted && !output.HasNewFrame) break;
                Thread.SpinWait(20);
                continue;
            }
            Assert.That(frame.FrameNumber, Is.GreaterThan(last), "frames only move forward");
            for (int y = 0; y < frame.Height; y++)
            {
                var row = frame.GetRow(y);
                for (int x = 0; x < row.Length; x++)
                    if (row[x] != (uint)frame.FrameNumber)
                        Assert.Fail($"frame {frame.FrameNumber} has pixel ({x}, {y}) = {row[x]}");
            }
            last = frame.FrameNumber;
            acquired++;
        }
        producer.Wait();
        Assert.That(last, Is.EqualTo(frames));
        Assert.That(output.FramesAcquired, Is.EqualTo(acquired));
        Assert.That(output.FramesAcquired + output.FramesSkipped, Is.EqualTo(frames), "every frame was either taken or skipped");
    }
}
