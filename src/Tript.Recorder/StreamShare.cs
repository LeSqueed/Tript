// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

public enum StreamShareState
{
    Off,
    Unsupported,
    WaitingForObs,
    WaitingForCapture,
    Live,
    Failed
}

public sealed record StreamShareStatus(StreamShareState State, string SenderName, uint Width, uint Height)
{
    public static StreamShareStatus Initial { get; } =
        new(StreamShareState.Off, StreamingSettings.DefaultSenderName, 0, 0);
}

public interface IActiveShare : IDisposable
{
    SharedFrameSize? FrameSize { get; }

    bool Failed { get; }

    event Action? Changed;
}

public sealed class StreamShare<TCapture> : IDisposable where TCapture : class
{
    private readonly Func<TCapture, string, IActiveShare> _start;
    private readonly bool _supported;
    private readonly Lock _gate = new();

    private bool _enabled;
    private StreamShareWhen _when = StreamShareWhen.WhileObsRuns;
    private string _senderName = StreamingSettings.DefaultSenderName;
    private bool _obsRunning;
    private TCapture? _capture;

    private IActiveShare? _active;
    private TCapture? _activeCapture;
    private string? _activeName;
    private bool _startFailed;
    private bool _disposed;
    private StreamShareStatus _status = StreamShareStatus.Initial;

    public StreamShare(Func<TCapture, string, IActiveShare> start, bool supported)
    {
        ArgumentNullException.ThrowIfNull(start);
        _start = start;
        _supported = supported;
    }

    public event Action<StreamShareStatus>? StatusChanged;

    public StreamShareStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public bool WantsObsPresence
    {
        get
        {
            lock (_gate)
                return _supported && _enabled;
        }
    }

    public void Configure(bool enabled, StreamShareWhen when, string senderName)
    {
        ArgumentNullException.ThrowIfNull(senderName);
        Change(() =>
        {
            _enabled = enabled;
            _when = when;
            _senderName = senderName;
        });
    }

    public void SetObsRunning(bool running) => Change(() => _obsRunning = running);

    public void SetCapture(TCapture? capture) => Change(() => _capture = capture);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            StopActive();
        }
    }

    private void Change(Action apply)
    {
        StreamShareStatus? changed;
        lock (_gate)
        {
            if (_disposed)
                return;

            apply();
            _startFailed = false;
            changed = Reconcile();
        }

        Raise(changed);
    }

    // Runs on a pool thread, where an escaping exception would terminate the process.
    private void OnActiveChanged() => ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            Refresh();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "StreamShare: refreshing the shared capture failed");
        }
    });

    private void Refresh()
    {
        StreamShareStatus? changed;
        lock (_gate)
        {
            if (_disposed)
                return;

            changed = Reconcile();
        }

        Raise(changed);
    }

    private void Raise(StreamShareStatus? changed)
    {
        if (changed is not null)
            StatusChanged?.Invoke(changed);
    }

    private StreamShareStatus? Reconcile()
    {
        var wanted = _supported && _enabled && _capture is not null &&
                     (_when == StreamShareWhen.Always || _obsRunning);

        if (_active is not null &&
            (!wanted || !ReferenceEquals(_activeCapture, _capture) || _activeName != _senderName))
        {
            StopActive();
        }

        if (wanted && _active is null && !_startFailed)
            StartActive();

        var next = ComputeStatus();
        if (next == _status)
            return null;

        _status = next;
        return next;
    }

    private void StartActive()
    {
        try
        {
            var active = _start(_capture!, _senderName);
            active.Changed += OnActiveChanged;
            _active = active;
            _activeCapture = _capture;
            _activeName = _senderName;
            Log.Information("StreamShare: sharing the game picture as Spout sender {Sender}", _senderName);
        }
        catch (Exception exception)
        {
            _startFailed = true;
            Log.Warning(exception, "StreamShare: the Spout sender {Sender} could not start.", _senderName);
        }
    }

    private void StopActive()
    {
        var active = _active;
        if (active is null)
            return;

        _active = null;
        _activeCapture = null;
        _activeName = null;
        active.Changed -= OnActiveChanged;
        try
        {
            active.Dispose();
            Log.Information("StreamShare: stopped the Spout sender");
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "StreamShare: the Spout sender did not shut down cleanly.");
        }
    }

    private StreamShareStatus ComputeStatus()
    {
        StreamShareStatus Of(StreamShareState state, SharedFrameSize? size = null) =>
            new(state, _senderName, size?.Width ?? 0, size?.Height ?? 0);

        if (!_enabled)
            return Of(StreamShareState.Off);
        if (!_supported)
            return Of(StreamShareState.Unsupported);
        if (_when == StreamShareWhen.WhileObsRuns && !_obsRunning)
            return Of(StreamShareState.WaitingForObs);
        if (_capture is null)
            return Of(StreamShareState.WaitingForCapture);
        if (_startFailed || _active is null || _active.Failed)
            return Of(StreamShareState.Failed);

        return _active.FrameSize is { } size
            ? Of(StreamShareState.Live, size)
            : Of(StreamShareState.WaitingForCapture);
    }
}

public static class ObsStreamShare
{
    public static StreamShare<ObsSource> Create(bool realRecorder) =>
        new(Start, realRecorder && OperatingSystem.IsWindows());

    private static IActiveShare Start(ObsSource source, string senderName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Spout sharing is only available on Windows.");

        var sender = new Tript.Obs.Spout.SpoutSender(senderName);
        try
        {
            return new ActiveObsShare(ObsSourceShare.Start(source, sender), sender);
        }
        catch
        {
            sender.Dispose();
            throw;
        }
    }

    private sealed class ActiveObsShare : IActiveShare
    {
        private readonly ObsSourceShare _share;
        private readonly IDisposable _sender;
        private int _refusalLogged;

        internal ActiveObsShare(ObsSourceShare share, IDisposable sender)
        {
            _share = share;
            _sender = sender;
            _share.FrameSizeChanged += OnFrameSizeChanged;
        }

        public SharedFrameSize? FrameSize => _share.FrameSize;

        public bool Failed => _share.TextureRefused;

        public event Action? Changed;

        public void Dispose()
        {
            _share.FrameSizeChanged -= OnFrameSizeChanged;
            _share.Dispose();
            _sender.Dispose();
        }

        private void OnFrameSizeChanged()
        {
            if (_share.TextureRefused && Interlocked.Exchange(ref _refusalLogged, 1) == 0)
                Log.Warning("StreamShare: the graphics device refused a shared texture, so nothing is shared.");

            Changed?.Invoke();
        }
    }
}
