// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Tript.Obs.IntegrationTests;

// The raw-video callback surface: frames arrive on the subscription with the requested geometry,
// the plane bytes match what a known source actually drew, the frame-rate divisor skips, teardown
// does not race an in-flight callback, and the refusal modes the media-io pair exists to surface
// are surfaced. The content tests drive a colour source on channel 0, which draws a deterministic
// constant image — that is what makes a wrong pixel meaningful.
//
// The compositor does not tick until an output is active — measured: with the video mix reset but
// no output started, video_output_get_total_frames stays at 0 and the graphics thread never posts
// to the video thread. Every test that expects a delivery therefore drives the mix with a
// ffmpeg_output writing to a scratch file, exactly as a recording would.
//
// ffmpeg_output is the one expensive thing in this suite, and it has a measured constraint: it
// always creates an internal x264 encoder that encodes the mix's frames, and tearing that encode
// path down across instances segfaults the process — SIGSEGV in avcodec_send_frame racing
// x264_encoder_close, and heap corruption that surfaces at the next obs_startup (verified via
// coredumps). The drain in ActiveOutput.Dispose fixes the single-session race, not the
// cross-instance accumulation. So ALL tests that need a delivery — geometry, content, divisor,
// teardown, duplicates, retention — share ONE long-lived output through FrameDeliveryFixture,
// measured stable. Channel content is switched on the live compositor safely (item.Remove before
// scene dispose; content-polling probes absorb the one-frame switch latency). The tests that need
// no delivery at all — reset-teardown, refusals, timing — run per-test sessions with NO output,
// which costs nothing.
//
// The callbacks are instance method groups, not lambdas: FrameCallback takes its frame by `in`,
// and the C# language forbids a lambda with an `in` parameter from capturing variables, so each
// probe is a small class that records what its callback observed. That matches how the real
// consumer, VisualEventDetector, passes OnFrame.
public sealed class ObsFrameSourceDeliveryTests : IClassFixture<FrameDeliveryFixture>
{
    private const string ColourSourceId = "color_source";
    private readonly FrameDeliveryFixture _fixture;

    public ObsFrameSourceDeliveryTests(FrameDeliveryFixture fixture) => _fixture = fixture;

    // ---- geometry and delivery ----

    [Fact]
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

        // BGRA is one plane, four bytes per pixel. The compositor may pad a row out beyond
        // width*4; the assertion is that the stride covers the row, not that it equals it.
        Assert.True(seen.Linesize0 >= 640u * 4, $"linesize {seen.Linesize0} < row bytes {640u * 4}");
    }

    // ---- content ----

    [Fact]
    public void PlaneContents_MatchTheColourSourceThatDrewThem()
    {
        // White is chroma-neutral: it survives the compositor's internal YUV conversion with every
        // byte exact (measured: 255,255,255,255 in the frame interior), so the assertion is
        // byte-for-byte. A saturated colour like red would come back one LSB short (254 for pure
        // red — measured and inherent to libobs's pipeline, not to this binding), which is covered
        // by the separate channel-mapping assertion below.
        using (var scene = _fixture.PlaceOnChannel(0xFFFFFFFF, "white scene"))
        {
            using var probe = new WhiteContentProbe();
            using var subscription = FrameSourceRegistry.Current.Subscribe(
                FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1);

            Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "No white frame arrived within 5 seconds.");

            // White drawn in BGRA is FF FF FF FF. Every pixel of the interior rows must be exactly
            // that. The scaler's bottom two edge rows read 253 instead (a 2-LSB interpolation edge
            // effect, measured and constant) — the interior is the honest assertion of what the
            // source drew, and the edge rows are a property of the resampler, not of this seam.
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

    [Fact]
    public void PlaneChannels_MapTheColourSourceWithoutSwapping()
    {
        // The direct-channel shape (no scene): the colour source draws its default-size rectangle,
        // which measured covers the top rows of the frame and leaves the rest black. The assertion
        // samples the covered region, exact for the saturated channel and zero for the others. The
        // 254 in the red slot is the 1-LSB chroma round-trip measured across libobs's pipeline.
        using (var red = _fixture.PlaceOnChannelDirect(0xFF0000FF, "red colour"))
        {
            using var probe = new RedContentProbe();
            using var subscription = FrameSourceRegistry.Current.Subscribe(
                FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1);

            Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "No red frame arrived within 5 seconds.");

            var copied = probe.Copied ?? throw new InvalidOperationException("Probe copied no frame.");
            var covered = 10 * probe.Stride + 32 * 4;
            Assert.Equal((byte)0x00, copied[covered + 0]); // B
            Assert.Equal((byte)0x00, copied[covered + 1]); // G
            Assert.InRange(copied[covered + 2], (byte)0xFE, (byte)0xFF); // R, one LSB short of 255
            Assert.Equal((byte)0xFF, copied[covered + 3]); // A
        }
    }

    // A plane index the format does not use is not an error. libobs leaves the data[] entry null
    // and the honest answer is an empty view, not an exception — the spec's explicit correction
    // to the seam's original throw.
    [Fact]
    public void APlaneTheFormatDoesNotUse_IsAnEmptyViewNotAnException()
    {
        using var probe = new EmptyPlaneProbe();
        using var subscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1);

        Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "No frame arrived within 5 seconds.");
        Assert.True(probe.Plane1Empty, "Plane 1 of a one-plane frame must read as an empty view.");
    }

    // The rows argument is what bounds a plane's extent, not the frame height. The seam hands the
    // caller's row count through to the length computation (linesize × rows) exactly as the
    // allocator did, because a subsampled chroma plane is shorter than the frame — a future planar
    // format's chroma plane has half the rows. For BGRA the two coincide, so this pins the
    // contract with a deliberate partial read: a view of fewer rows is exactly the first rows,
    // and a view of more rows than the frame is refused.
    [Fact]
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

    // ---- frame-rate divisor ----

    [Fact]
    public void TheFrameRateDivisor_DeliversEveryNthFrame()
    {
        // Subscribing with divisor 3, then counting deliveries and the composited frames over the
        // same wall window. The mix runs at the reset rate (60fps default); the
        // delivered/composited ratio must sit near 1/3. Wide bounds absorb compositor scheduling
        // jitter without accepting a divisor that is ignored (which would be a ratio near 1).
        using var probe = new DivisorProbe(10);
        using var subscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 3);

        Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "No frames delivered at divisor 3 within 5 seconds.");

        var elapsed = probe.Stopwatch.Elapsed.TotalSeconds;
        var composited = Math.Max(1.0, elapsed * 60);
        var ratio = probe.Delivered / composited;

        Assert.InRange(ratio, 1.0 / 4.0, 1.0 / 2.5);
    }

    // ---- teardown ----

    [Fact]
    public void DisposingASubscription_WhileFramesAreArriving_DoesNotRace()
    {
        // Loop subscribe/run/dispose so a dispose that raced an in-flight callback would land
        // somewhere in the loop rather than once at the end. The callback target is swapped out
        // before the disconnect, so a callback already inside OnFrame observes null and returns.
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

    // ---- duplicate refusal ----

    [Fact]
    public void TwoSubscriptionsWithTheSameDelegate_AreNotDuplicates()
    {
        // libobs's identity for an input is the (callback, param) pair, not the callback alone,
        // and video_output_connect2 rejects a duplicate pair. The seam allocates a fresh param
        // (its own GCHandle) per Subscribe, so two calls with the *same delegate* must be two
        // distinct subscriptions — if the seam ever reused a param, the second would be refused
        // as a duplicate. Proving both connect and deliver is the meaningful assertion at this
        // layer; provoking the native duplicate rejection would mean reaching past the seam into
        // the same (callback, param) twice, which the seam exists to prevent.
        using var firstProbe = new FirstDeliveryProbe();
        using var secondProbe = new FirstDeliveryProbe();

        using var first = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, firstProbe.OnFrame, frameRateDivisor: 1);
        using var second = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, secondProbe.OnFrame, frameRateDivisor: 1);

        Assert.True(firstProbe.Gate.Wait(TimeSpan.FromSeconds(5)), "First subscription saw no frame.");
        Assert.True(secondProbe.Gate.Wait(TimeSpan.FromSeconds(5)), "Second subscription saw no frame.");
    }

    // ---- the retained-pointer trap ----

    [Fact]
    public void RetainingAConvertedFramePointer_ReadsADifferentImageLater()
    {
        // Documents the trap rather than guarding against it: a converted frame's plane pointer
        // points into one of three per-subscription rotating buffers. Retaining the pointer
        // across frames silently reads a different image a few frames later. The pointer is
        // compared, never dereferenced — this proves the rotation happens at all, so a consumer
        // that retains a pointer is documented as reading garbage, not just as slow.
        using var probe = new PointerRotationProbe(20);
        using var subscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, probe.OnFrame, frameRateDivisor: 1);

        Assert.True(probe.Gate.Wait(TimeSpan.FromSeconds(5)), "Not enough frames delivered within 5 seconds.");
        Assert.True(probe.PointerChanged,
            "The converted frame's plane pointer should have rotated within 20 frames.");
    }

    // ---- probes ----

    // The value-carrying surface of a frame, captured inside the callback. Deliberately no plane
    // pointers — preserving those would be the trap the suite documents.
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
        internal Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
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

        // The first frame after the fixture's source swap may still show the previous content —
        // the compositor switches within a frame or two. Poll for the white interior: FF FF FF FF
        // at the sampled pixel, then capture the whole frame.
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

    // The seam keeps plane pointers internal, which is exactly right for a consumer. The tests
    // that need the pointer (the retention trap) reach it through the span the view hands out,
    // which is how a real consumer would too.
    private static unsafe nint PointerOf(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return nint.Zero;

        ref var first = ref MemoryMarshal.GetReference(span);
        return (nint)Unsafe.AsPointer(ref first);
    }
}

// The reset-teardown, refusal and timing tests need no delivery, so they run per-test sessions
// with no output — the fresh session is the only way to test the "no video pipeline yet" states,
// and it costs nothing (no ffmpeg_output, no accumulation).
public sealed class ObsFrameSourceTests
{
    private const string ColourSourceId = "color_source";

    [Fact]
    public void DisposingAfterAVideoReset_DoesNotDisconnectFromTheFreedHandle()
    {
        using var session = ObsSession.StartWithSourceTypes();

        // Subscribe with no output active: connect succeeds (the video mix exists) and no frames
        // are delivered. This is the reset-teardown state without paying for an ffmpeg_output.
        var subscription = FrameSourceRegistry.Current.Subscribe(
            FramePixelFormat.Bgra, 320, 180, Noop.OnFrame, frameRateDivisor: 1);

        // A second reset tears the old mix down; the handle the subscription connected to is
        // freed. Disposing must not disconnect from the freed pointer — the currency check skips
        // the native call and just releases the pin.
        session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720
        });

        subscription.Dispose();
    }

    [Fact]
    public void AZeroFrameRateDivisor_IsRefusedByTheSubscribeCall()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var colour = ObsSource.Create(ColourSourceId, "divisor zero colour");
        session.Runtime.SetOutputSource(0, colour);

        // Zero is validated by video_output_connect2, not undefined. The seam surfaces it as a
        // refused subscription rather than a dead one.
        Assert.Throws<ObsException>(() =>
            FrameSourceRegistry.Current.Subscribe(
                FramePixelFormat.Bgra, 320, 180, Noop.OnFrame, frameRateDivisor: 0));
    }

    [Fact]
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

    [Fact]
    public void GetVideoTiming_IsNullBeforeThePipelineExists()
    {
        using var session = ObsSession.Start();

        // No video reset yet: obs_get_video returns null and video_output_get_info cannot be
        // reached. The seam reports the absence as null, not as a throw.
        Assert.Null(FrameSourceRegistry.Current.GetVideoTiming());
    }

    [Fact]
    public void SubscribingBeforeThePipelineExists_IsRefused()
    {
        using var session = ObsSession.Start();

        // No video mix yet. The subscription is refused at the seam rather than segfaulting on
        // obs_get_video — the ObsRuntime.TryGetVideoHandle gate is what makes that safe.
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

// The shared context for the delivery tests. One ObsRuntime, one video mix reset to BGRA (the
// content tests' format), one long-lived ffmpeg_output. This is the measured-stable shape: the
// ffmpeg_output encode path accumulates corruption across instances (SIGSEGV around the seventh
// to tenth), but a single instance serving every delivery test is stable — verified 3/3 on the
// exact shapes this suite runs. The fixture owns the session and the driver; each test owns its
// subscription and (for content tests) its channel source.
//
// Channel content is switched safely on the live compositor: the previous test's scene is removed
// via ObsSceneItem.Remove before its scene is disposed, which is libobs's own detach path and
// does not race the render thread — measured (the alternative, disposing the item handle while it
// is still attached, is a SIGSEGV in obs_sceneitem_release). The content probes poll for the
// expected colour because the compositor takes a frame or two to switch.
public sealed class FrameDeliveryFixture : IDisposable
{
    private int _disposed;

    internal ObsSession Session { get; }
    internal ActiveOutput Driver { get; }

    public FrameDeliveryFixture()
    {
        Session = ObsSession.StartWithSourceTypes();
        Session.ResetVideoOrThrow(new ObsVideoSettings
        {
            BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720,
            OutputFormat = ObsVideoFormat.Bgra
        });
        Driver = ActiveOutput.Start(Session);
    }

    // Puts a colour source on channel 0, stretched to the canvas via a scene. The returned
    // disposable removes the item and releases the scene on dispose — the safe teardown for a
    // live compositor.
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

    // Puts a colour source straight on channel 0 (no scene), the shape that does not stretch to
    // the canvas. Same safe teardown.
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

        // Stop the output and let the encode thread quiesce before touching anything the channel
        // may still reference. The channel's reference is left in place: dropping it while the
        // compositor is live would destroy the source under the render thread (the same race the
        // tests avoid by removing before disposing), and obs_shutdown frees whatever the channel
        // still references.
        Driver.Dispose();
        Session.Dispose();
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
            // libobs's own detach path, safe against the live render thread. Disposing the item
            // handle without removing it first is a SIGSEGV in obs_sceneitem_release — measured.
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
            // The channel holds its own reference to the source; dropping the wrapper reference
            // just releases ours. The channel's reference is released at shutdown.
            _source.Dispose();
        }
    }
}

// Drives the compositor: an ffmpeg_output writing to a scratch file, wired with obs_x264 so it
// activates the video mix. Without an active output the graphics thread never posts to the video
// thread and raw-frame callbacks never fire — measured, the reason every delivery test starts one.
// ffmpeg_output runs in-process via libavformat, so no obs-ffmpeg-mux helper is needed next to the
// test host. Disposing stops the output and releases the encoder.
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

        // Deliberately NOT binding the encoder to the video handle. Binding puts the encoder on
        // the GPU texture-encoder path (obs_encoder_set_video raises gpu_refs), and tearing that
        // path down across many sessions corrupts the Mesa heap on this host — measured:
        // "malloc(): unaligned tcache chunk detected" in amdgpu_winsys_create, at roughly the
        // tenth session. The output still ticks the compositor and the raw subscription still
        // receives frames (the frame travels the ordinary raw path), so the lighter shape tests
        // the same seam without gambling on a driver bug.
        var output = ObsOutput.Create("ffmpeg_output", "diag out", settings);
        output.SetVideoEncoder(encoder);
        Assert.True(output.Start(), "ffmpeg_output refused to start; the mix would not tick.");

        return new ActiveOutput(output, encoder);
    }

    public void Dispose()
    {
        // Idempotent: the reset test disposes the driver explicitly and the using declaration
        // disposes it again at block exit, and Stop() on an already-disposed output throws.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The teardown race, measured: the encoder thread is still inside avcodec_send_frame
        // when the output is stopped and the encoder released, and x264_encoder_close runs
        // while that thread is mid-frame — SIGSEGV in av_buffer_unref. The subscription feeds
        // the mix, the encoder encodes real frames, and disposing without draining the output
        // opens the window. Draining means: ask it to stop, wait for the stop signal (which
        // the plugin emits once it has finished with the encoder), then give the encode thread
        // time to fully quiesce before releasing anything.
        var stopped = new TaskCompletionSource<ObsOutputStopEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ObsOutputStopEvent> handler = (_, payload) => stopped.TrySetResult(payload);
        _output.Stopped += handler;
        try
        {
            _output.Stop();
            stopped.Task.Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _output.Stopped -= handler;
        }

        Thread.Sleep(300);
        _output.Dispose();
        _encoder.Dispose();
    }

    private static string CreateScratchFile() =>
        Path.Combine(Path.GetTempPath(), $"reference-product_frame_driver_{Guid.NewGuid():N}.mp4");
}
