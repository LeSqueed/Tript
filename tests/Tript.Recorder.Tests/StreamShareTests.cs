// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

public sealed class StreamShareTests
{
    private readonly List<FakeShare> _started = [];
    private readonly object _capture = new();

    private StreamShare<object> NewShare(bool supported = true, Exception? startFailure = null) =>
        new((capture, name) =>
        {
            if (startFailure is not null)
                throw startFailure;

            var share = new FakeShare(capture, name);
            _started.Add(share);
            return share;
        }, supported);

    [Fact]
    public void ANewShare_IsOffAndStartsNothing()
    {
        using var share = NewShare();
        share.SetObsRunning(true);
        share.SetCapture(_capture);

        Assert.Equal(StreamShareState.Off, share.Status.State);
        Assert.Empty(_started);
        Assert.False(share.WantsObsPresence);
    }

    [Fact]
    public void WhileObsRuns_WaitsForObsBeforeSharing()
    {
        using var share = NewShare();
        share.Configure(true, StreamShareWhen.WhileObsRuns, "Tript");
        share.SetCapture(_capture);

        Assert.Equal(StreamShareState.WaitingForObs, share.Status.State);
        Assert.Empty(_started);
        Assert.True(share.WantsObsPresence);
    }

    [Fact]
    public void ObsOpeningLater_StartsSharingMidRecording()
    {
        using var share = NewShare();
        share.Configure(true, StreamShareWhen.WhileObsRuns, "Tript");
        share.SetCapture(_capture);

        share.SetObsRunning(true);

        var active = Assert.Single(_started);
        Assert.Same(_capture, active.Capture);
        Assert.Equal("Tript", active.Name);
        Assert.Equal(StreamShareState.WaitingForCapture, share.Status.State);
    }

    [Fact]
    public void AFirstFrame_MakesTheShareLive()
    {
        using var share = NewShare();
        using var changed = new ManualResetEventSlim();
        share.StatusChanged += status =>
        {
            if (status.State == StreamShareState.Live)
                changed.Set();
        };
        share.Configure(true, StreamShareWhen.Always, "Tript");
        share.SetCapture(_capture);

        Assert.Single(_started).Deliver(new SharedFrameSize(1920, 1080));

        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(new StreamShareStatus(StreamShareState.Live, "Tript", 1920, 1080), share.Status);
    }

    [Fact]
    public void ObsClosing_StopsSharingButKeepsTheCapture()
    {
        using var share = NewShare();
        share.Configure(true, StreamShareWhen.WhileObsRuns, "Tript");
        share.SetCapture(_capture);
        share.SetObsRunning(true);

        share.SetObsRunning(false);

        Assert.True(Assert.Single(_started).Disposed);
        Assert.Equal(StreamShareState.WaitingForObs, share.Status.State);
    }

    [Fact]
    public void Always_SharesWithoutObs()
    {
        using var share = NewShare();
        share.Configure(true, StreamShareWhen.Always, "Tript");
        share.SetCapture(_capture);

        Assert.Single(_started);
        Assert.True(share.WantsObsPresence);
    }

    [Fact]
    public void TheCaptureGoingAway_StopsSharing()
    {
        using var share = NewShare();
        share.Configure(true, StreamShareWhen.Always, "Tript");
        share.SetCapture(_capture);

        share.SetCapture(null);

        Assert.True(Assert.Single(_started).Disposed);
        Assert.Equal(StreamShareState.WaitingForCapture, share.Status.State);
    }

    [Fact]
    public void ANewCaptureOrName_RestartsTheSender()
    {
        using var share = NewShare();
        share.Configure(true, StreamShareWhen.Always, "Tript");
        share.SetCapture(_capture);

        var rebuilt = new object();
        share.SetCapture(rebuilt);
        share.Configure(true, StreamShareWhen.Always, "Tript Game");

        Assert.Equal(3, _started.Count);
        Assert.True(_started[0].Disposed);
        Assert.True(_started[1].Disposed);
        Assert.Same(rebuilt, _started[2].Capture);
        Assert.Equal("Tript Game", _started[2].Name);
        Assert.False(_started[2].Disposed);
    }

    [Fact]
    public void TurningSharingOff_StopsTheSender()
    {
        using var share = NewShare();
        share.Configure(true, StreamShareWhen.Always, "Tript");
        share.SetCapture(_capture);

        share.Configure(false, StreamShareWhen.Always, "Tript");

        Assert.True(Assert.Single(_started).Disposed);
        Assert.Equal(StreamShareState.Off, share.Status.State);
    }

    [Fact]
    public void AnUnsupportedHost_NeverStartsASender()
    {
        using var share = NewShare(supported: false);
        share.Configure(true, StreamShareWhen.Always, "Tript");
        share.SetCapture(_capture);

        Assert.Empty(_started);
        Assert.Equal(StreamShareState.Unsupported, share.Status.State);
        Assert.False(share.WantsObsPresence);
    }

    [Fact]
    public void ASenderThatWillNotStart_ReportsFailed()
    {
        using var share = NewShare(startFailure: new InvalidOperationException("no shared memory"));
        share.Configure(true, StreamShareWhen.Always, "Tript");
        share.SetCapture(_capture);

        Assert.Equal(StreamShareState.Failed, share.Status.State);
    }

    [Fact]
    public void ARefusedTexture_ReportsFailed()
    {
        using var share = NewShare();
        using var failed = new ManualResetEventSlim();
        share.StatusChanged += status =>
        {
            if (status.State == StreamShareState.Failed)
                failed.Set();
        };
        share.Configure(true, StreamShareWhen.Always, "Tript");
        share.SetCapture(_capture);

        Assert.Single(_started).Fail();

        Assert.True(failed.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Disposing_StopsTheSenderAndIgnoresLaterChanges()
    {
        var share = NewShare();
        share.Configure(true, StreamShareWhen.Always, "Tript");
        share.SetCapture(_capture);

        share.Dispose();
        share.SetCapture(new object());

        Assert.True(Assert.Single(_started).Disposed);
    }

    private sealed class FakeShare(object capture, string name) : IActiveShare
    {
        private SharedFrameSize? _size;
        private bool _failed;

        internal object Capture { get; } = capture;

        internal string Name { get; } = name;

        internal bool Disposed { get; private set; }

        public SharedFrameSize? FrameSize => _size;

        public bool Failed => _failed;

        public event Action? Changed;

        internal void Deliver(SharedFrameSize size)
        {
            _size = size;
            Changed?.Invoke();
        }

        internal void Fail()
        {
            _failed = true;
            Changed?.Invoke();
        }

        public void Dispose() => Disposed = true;
    }
}
