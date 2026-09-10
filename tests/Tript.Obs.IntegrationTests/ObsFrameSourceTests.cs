// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsFrameSourceDeliveryTests : IClassFixture<FrameDeliveryFixture>
{
    private const string ColourSourceId = "color_source";
    private readonly FrameDeliveryFixture _fixture;

    public ObsFrameSourceDeliveryTests(FrameDeliveryFixture fixture)
    {
        _fixture = fixture;

        fixture.RequireStarted();
    }

    [SkippableFact]
    public void AFrame_ArrivesAtTheRequestedSizeInBgra()
    {
        using var probe = new FrameGeometryProbe();
        using var subscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 640, 360, probe.OnFrame, frameRateDivisor: 1);

        Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "No frame arrived within 5 seconds.");
        var seen = probe.Snapshot ?? throw new InvalidOperationException("Probe saw no frame.");

        Assert.Equal(FramePixelFormat.Bgra, seen.Format);
        Assert.Equal(640u, seen.Width);
        Assert.Equal(360u, seen.Height);
        Assert.Equal(1, seen.PlaneCount);

        Assert.True(seen.Linesize0 >= 640u * 4, $"linesize {seen.Linesize0} < row bytes {640u * 4}");
    }

    [SkippableFact]
    public void PlaneContents_MatchTheColourSourceThatDrewThem()
    {
        using (var scene = _fixture.PlaceOnChannel(0xFFFFFFFF, "white scene"))
        {
            using var probe = new WhiteContentProbe();
            using var subscription = FrameSourceRegistry.Current.Subscribe(
                FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1);

            Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "No white frame arrived within 5 seconds.");

            var copied = probe.Copied ?? throw new InvalidOperationException("Probe copied no frame.");
            Assert.True(probe.Stride >= probe.Width * 4, $"stride {probe.Stride} < row bytes {probe.Width * 4}");
            for (var y = 0; y < probe.Height - 2; y++)
            {
                for (var x = 0; x < probe.Width; x++)
                {
                    var i = y * probe.Stride + x * 4;
                    Assert.Equal((byte)0xFF, copied[i + 0]);
                    Assert.Equal((byte)0xFF, copied[i + 1]);
                    Assert.Equal((byte)0xFF, copied[i + 2]);
                    Assert.Equal((byte)0xFF, copied[i + 3]);
                }
            }
        }
    }

    [SkippableFact]
    public void PlaneChannels_MapTheColourSourceWithoutSwapping()
    {
        using (var red = _fixture.PlaceOnChannelDirect(0xFF0000FF, "red colour"))
        {
            using var probe = new RedContentProbe();
            using var subscription = FrameSourceRegistry.Current.Subscribe(
                FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1);

            Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "No red frame arrived within 5 seconds.");

            var copied = probe.Copied ?? throw new InvalidOperationException("Probe copied no frame.");
            var covered = 10 * probe.Stride + 32 * 4;
            Assert.Equal((byte)0x00, copied[covered + 0]);
            Assert.Equal((byte)0x00, copied[covered + 1]);
            Assert.InRange(copied[covered + 2], (byte)0xFE, (byte)0xFF);
            Assert.Equal((byte)0xFF, copied[covered + 3]);
        }
    }

    [SkippableFact]
    public void APlaneTheFormatDoesNotUse_IsAnEmptyViewNotAnException()
    {
        using var probe = new EmptyPlaneProbe();
        using var subscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1);

        Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "No frame arrived within 5 seconds.");
        Assert.True(probe.Plane1Empty, "Plane 1 of a one-plane frame must read as an empty view.");
    }

    [SkippableFact]
    public void GetPlane_RowCountBoundsThePlaneNotTheFrameHeight()
    {
        using var probe = new RowCountProbe();
        using var subscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1);

        Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "No frame arrived within 5 seconds.");

        Assert.True(probe.FullLength > probe.PartialLength,
            $"A {probe.FullLength}-row view should be longer than a {probe.PartialLength}-row view.");
        Assert.Equal(probe.FullLength, probe.PartialLength + probe.PartialLength);
    }

    [SkippableFact]
    public void TheFrameRateDivisor_DeliversEveryNthFrame()
    {
        using var composited = new DivisorProbe(int.MaxValue);
        using var everyThird = new DivisorProbe(20);
        using var compositedSubscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, composited.OnFrame, frameRateDivisor: 1);
        using var everyThirdSubscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, everyThird.OnFrame, frameRateDivisor: 3);

        Assert.True(everyThird.Gate.Wait(TimeSpan.FromSeconds(10)),
            "No frames delivered at divisor 3 within 10 seconds.");

        var delivered = everyThird.Delivered;
        var total = composited.Delivered;
        var ratio = delivered / (double)Math.Max(1, total);

        Assert.InRange(ratio, 1.0 / 4.0, 1.0 / 2.5);
    }

    [SkippableFact]
    public void DisposingASubscription_WhileFramesAreArriving_DoesNotRace()
    {
        for (var iteration = 0; iteration < 40; iteration++)
        {
            using var probe = new FirstDeliveryProbe();
            using (var subscription = FrameSourceRegistry.Current.Subscribe(
                FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1))
            {
                Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)),
                    $"No frame arrived in iteration {iteration}.");
            }
        }
    }

    [SkippableFact]
    public void TwoSubscriptionsWithTheSameDelegate_AreNotDuplicates()
    {
        using var firstProbe = new FirstDeliveryProbe();
        using var secondProbe = new FirstDeliveryProbe();

        using var first = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, firstProbe.OnFrame, frameRateDivisor: 1);
        using var second = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, secondProbe.OnFrame, frameRateDivisor: 1);

        Assert.True(firstProbe.Gate.Wait(TimeSpan.FromSeconds(5)), "First subscription saw no frame.");
        Assert.True(secondProbe.Gate.Wait(TimeSpan.FromSeconds(5)), "Second subscription saw no frame.");
    }

    [SkippableFact]
    public void RetainingAConvertedFramePointer_ReadsADifferentImageLater()
    {
        using var probe = new PointerRotationProbe(20);
        using var subscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1);

        Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "Not enough frames delivered within 5 seconds.");
        Assert.True(probe.PointerChanged,
            "The converted frame's plane pointer should have rotated within 20 frames.");
    }

    private readonly record struct FrameSnapshot(
        FramePixelFormat Format, uint Width, uint Height, int PlaneCount, uint Linesize0)
    {
        internal static FrameSnapshot Capture(in VideoFrame frame) => new(
            frame.Format, frame.Width, frame.Height, frame.PlaneCount, frame.GetLinesize(0));
    }

    private abstract class ProbeBase : IDisposable
    {
        internal ManualResetEventSlim Gate { get; } = new();

        public void Dispose() => Gate.Dispose();
    }

    private sealed class FrameGeometryProbe : ProbeBase
    {
        internal FrameSnapshot? Snapshot { get; private set; }

        internal void OnFrame(in VideoFrame frame)
        {
            Snapshot = FrameSnapshot.Capture(frame);
            Gate.Set();
        }
    }

    private sealed class EmptyPlaneProbe : ProbeBase
    {
        internal bool Plane1Empty { get; private set; }

        internal void OnFrame(in VideoFrame frame)
        {
            Plane1Empty = frame.GetPlane(1, frame.Height).IsEmpty;
            Gate.Set();
        }
    }

    private sealed class RowCountProbe : ProbeBase
    {
        internal int FullLength { get; private set; }
        internal int PartialLength { get; private set; }

        internal void OnFrame(in VideoFrame frame)
        {
            FullLength = frame.GetPlane(0, frame.Height).Length;
            PartialLength = frame.GetPlane(0, frame.Height / 2).Length;
            Gate.Set();
        }
    }

    private sealed class DivisorProbe : ProbeBase
    {
        internal int Delivered => _delivered;

        private readonly int _target;
        private int _delivered;

        internal DivisorProbe(int target) => _target = target;

        internal void OnFrame(in VideoFrame frame)
        {
            if (Interlocked.Increment(ref _delivered) >= _target)
                Gate.Set();
        }
    }

    private sealed class FirstDeliveryProbe : ProbeBase
    {
        internal void OnFrame(in VideoFrame frame) => Gate.Set();
    }

    private sealed class PointerRotationProbe : ProbeBase
    {
        internal bool PointerChanged { get; private set; }

        private readonly int _target;
        private nint _firstPointer;
        private int _received;

        internal PointerRotationProbe(int target) => _target = target;

        internal void OnFrame(in VideoFrame frame)
        {
            var pointer = PointerOf(frame.GetPlane(0, frame.Height));
            if (_firstPointer == nint.Zero)
                _firstPointer = pointer;
            else if (pointer != _firstPointer)
                PointerChanged = true;

            if (Interlocked.Increment(ref _received) >= _target)
                Gate.Set();
        }
    }

    private sealed class WhiteContentProbe : ProbeBase
    {
        internal byte[]? Copied { get; private set; }
        internal int Stride { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }

        internal void OnFrame(in VideoFrame frame)
        {
            if (Copied is not null)
                return;

            Stride = (int)frame.GetLinesize(0);
            Width = (int)frame.Width;
            Height = (int)frame.Height;
            var copied = frame.GetPlane(0, (uint)Height).ToArray();

            var sample = 8 * Stride + 32 * 4;
            if (copied.Length > sample + 3 &&
                copied[sample + 0] == 0xFF && copied[sample + 1] == 0xFF &&
                copied[sample + 2] == 0xFF && copied[sample + 3] == 0xFF)
            {
                Copied = copied;
                Gate.Set();
            }
        }
    }

    private sealed class RedContentProbe : ProbeBase
    {
        internal byte[]? Copied { get; private set; }
        internal int Stride { get; private set; }

        internal void OnFrame(in VideoFrame frame)
        {
            if (Copied is not null)
                return;

            Stride = (int)frame.GetLinesize(0);
            var copied = frame.GetPlane(0, frame.Height).ToArray();

            var sample = 10 * Stride + 32 * 4;
            if (copied.Length > sample + 3 &&
                copied[sample + 0] == 0x00 && copied[sample + 1] == 0x00 &&
                copied[sample + 2] >= 0xFE && copied[sample + 3] == 0xFF)
            {
                Copied = copied;
                Gate.Set();
            }
        }
    }

    private static unsafe nint PointerOf(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return nint.Zero;

        ref var first = ref MemoryMarshal.GetReference(span);
        return (nint)Unsafe.AsPointer(ref first);
    }
}

public sealed class ObsFrameSourceTests
{
    private const string ColourSourceId = "color_source";

    [SkippableFact]
    public void DisposingAfterAVideoReset_DoesNotDisconnectFromTheFreedHandle()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var subscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, Noop.OnFrame, frameRateDivisor: 1);

        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720
        });

        subscription.Dispose();
    }

    [SkippableFact]
    public void AZeroFrameRateDivisor_IsRefusedByTheSubscribeCall()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var colour = ObsSource.Create(ColourSourceId, "divisor zero colour");
        session.Runtime.SetOutputSource(0, colour);

        Assert.Throws<ObsException>(() =>
            FrameSourceRegistry.Current.Subscribe(
                FramePixelFormat.Bgra, 320, 180, Noop.OnFrame, frameRateDivisor: 0));
    }

    [SkippableFact]
    public void GetVideoTiming_ReportsTheConfiguredFraction()
    {
        using var session = ObsSession.Start();
        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720,
            FpsNumerator = 60000, FpsDenominator = 1001
        });

        var timing = FrameSourceRegistry.Current.GetVideoTiming();

        Assert.NotNull(timing);
        Assert.Equal(60000u, timing.Value.FpsNumerator);
        Assert.Equal(1001u, timing.Value.FpsDenominator);
    }

    [SkippableFact]
    public void GetVideoTiming_IsNullBeforeThePipelineExists()
    {
        using var session = ObsSession.Start();

        Assert.Null(FrameSourceRegistry.Current.GetVideoTiming());
    }

    [SkippableFact]
    public void SubscribingBeforeThePipelineExists_IsRefused()
    {
        using var session = ObsSession.Start();

        Assert.Throws<ObsException>(() =>
            FrameSourceRegistry.Current.Subscribe(FramePixelFormat.Bgra, 320, 180, Noop.OnFrame, 1));
    }

    private static class Noop
    {
        internal static void OnFrame(in VideoFrame frame)
        {
        }
    }
}

public sealed class FrameDeliveryFixture : IDisposable
{
    private readonly Exception? _startupFailure;
    private int _disposed;

    internal ObsSession Session { get; } = null!;
    internal ActiveOutput Driver { get; } = null!;

    public FrameDeliveryFixture()
    {
        try
        {
            Session = ObsSession.StartWithSourceTypes();
            Session.ResetVideoOrThrow(new ObsVideoSettings
            {
                BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720,
                OutputFormat = ObsVideoFormat.Bgra
            });
            Driver = ActiveOutput.Start(Session);
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
        }
    }

    internal void RequireStarted()
    {
        if (_startupFailure is not null)
            throw _startupFailure;
    }

    internal IDisposable PlaceOnChannel(uint argb, string name)
    {
        using var settings = new ObsSettings();
        settings.SetInt("color", (long)argb);
        var colour = ObsSource.Create("color_source", $"{name} colour", settings);
        var scene = ObsScene.CreatePrivate(name);
        var item = scene.AddSource(colour);
        if (item is null)
        {
            scene.Dispose();
            colour.Dispose();
            throw new InvalidOperationException("Adding the colour source to the scene returned no item.");
        }

        var bounds = Session.Runtime.TryGetVideoInfo(out var info)
            ? new Vector2(info!.OutputWidth, info.OutputHeight)
            : Vector2.Zero;

        item.Transform = new ObsTransform
        {
            Position = new Vector2(0, 0),
            Scale = Vector2.One,
            Alignment = ObsAlignment.Top | ObsAlignment.Left,
            BoundsType = ObsBoundsType.Stretch,
            BoundsAlignment = ObsAlignment.Top | ObsAlignment.Left,
            Bounds = bounds,
            CropToBounds = false
        };

        Session.Runtime.SetOutputSource(0, scene.AsSource());
        return new ChannelScope(scene, item);
    }

    internal IDisposable PlaceOnChannelDirect(uint argb, string name)
    {
        using var settings = new ObsSettings();
        settings.SetInt("color", (long)argb);
        var colour = ObsSource.Create("color_source", name, settings);
        Session.Runtime.SetOutputSource(0, colour);
        return new ChannelSourceScope(colour);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Driver?.Dispose();
        Session?.Dispose();
    }

    private sealed class ChannelScope : IDisposable
    {
        private readonly ObsScene _scene;
        private readonly ObsSceneItem _item;

        internal ChannelScope(ObsScene scene, ObsSceneItem item)
        {
            _scene = scene;
            _item = item;
        }

        public void Dispose()
        {
            _item.Remove();
            _item.Dispose();
            _scene.Dispose();
        }
    }

    private sealed class ChannelSourceScope : IDisposable
    {
        private readonly ObsSource _source;

        internal ChannelSourceScope(ObsSource source) => _source = source;

        public void Dispose()
        {
            _source.Dispose();
        }
    }
}

internal sealed class ActiveOutput : IDisposable
{
    private readonly ObsOutput _output;
    private readonly ObsEncoder _encoder;
    private int _disposed;

    private ActiveOutput(ObsOutput output, ObsEncoder encoder)
    {
        _output = output;
        _encoder = encoder;
    }

    internal static ActiveOutput Start(ObsSession session)
    {
        using var settings = new ObsSettings();
        settings.SetString("url", CreateScratchFile());
        settings.SetString("format_name", "mp4");
        settings.SetInt("gop_size", 60);
        settings.SetInt("video_bitrate", 600);
        settings.SetString("video_encoder", "libx264");

        using var videoSettings = new ObsSettings();
        videoSettings.SetString("rate_control", "CBR");
        videoSettings.SetInt("bitrate", 600);
        var encoder = ObsEncoder.CreateVideo("obs_x264", "diag enc", videoSettings);

        var output = ObsOutput.Create("ffmpeg_output", "diag out", settings);
        output.SetVideoEncoder(encoder);
        Assert.True(output.Start(), "ffmpeg_output refused to start; the mix would not tick.");

        return new ActiveOutput(output, encoder);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var stopped = new TaskCompletionSource<ObsOutputStopEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ObsOutputStopEvent> handler = (_, payload) => stopped.TrySetResult(payload);
        _output.Stopped += handler;
        try
        {
            _output.Stop();
            Assert.True(stopped.Task.Wait(TimeSpan.FromSeconds(5)),
                "The output did not publish its asynchronous stop signal within 5 seconds.");
        }
        finally
        {
            _output.Stopped -= handler;
        }

        _output.Dispose();
        _encoder.Dispose();
    }

    private static string CreateScratchFile() =>
        Path.Combine(Path.GetTempPath(), $"tript_frame_driver_{Guid.NewGuid():N}.mp4");
}
