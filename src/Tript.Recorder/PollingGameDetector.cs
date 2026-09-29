// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;

namespace Tript.Recorder;

public abstract class PollingGameDetector : IDisposable
{
    private readonly TimeSpan _pollInterval;
    private readonly SerializedDetectorCallbackQueue _callbacks;
    private long _targetVersion;
    private Timer? _timer;
    private bool _disposed;
    private int _ticking;
    private bool _pollFailing;

    private protected PollingGameDetector(TimeSpan pollInterval, string callbackThreadName)
    {
        _pollInterval = pollInterval;
        _callbacks = new SerializedDetectorCallbackQueue(callbackThreadName);
    }

    private protected object Gate { get; } = new();

    private protected virtual bool CanPoll => true;

    private string Name => GetType().Name;

    public void Start()
    {
        if (!CanPoll)
            return;

        lock (Gate)
        {
            if (_disposed || _timer is not null)
                return;
            _timer = new Timer(OnTick, null, TimeSpan.Zero, _pollInterval);
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _callbacks.Dispose();
        OnDisposed();
    }

    internal void PollOnce() => OnTick(null);

    internal void WaitForCallbacks() => _callbacks.WaitUntilIdle();

    internal bool IsDisposed
    {
        get { lock (Gate) return _disposed; }
    }

    private protected abstract void Poll();

    private protected virtual void OnDisposed()
    {
    }

    private protected void BumpTargetVersionLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _targetVersion++;
    }

    private protected bool TryReadTargetVersionLocked(out long targetVersion)
    {
        targetVersion = _targetVersion;
        return !_disposed;
    }

    private protected bool IsStaleLocked(long targetVersion) => _disposed || targetVersion != _targetVersion;

    private protected void Enqueue<T>(Action<T>? handlers, T item, long targetVersion)
    {
        if (handlers is not null)
            _callbacks.Enqueue(() => Raise(handlers, item, targetVersion));
    }

    private void Raise<T>(Action<T> handlers, T item, long targetVersion)
    {
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            lock (Gate)
            {
                if (IsStaleLocked(targetVersion))
                    return;
            }
            try
            {
                handler(item);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "{Detector}: a subscriber threw; the watch continues.", Name);
            }
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0)
            return;

        try
        {
            Poll();
            _pollFailing = false;
        }
        catch (Exception exception)
        {
            if (_pollFailing)
                Log.Debug(exception, "{Detector}: a poll failed again; the watch continues.", Name);
            else
                Log.Warning(exception, "{Detector}: a poll failed; the watch continues.", Name);
            _pollFailing = true;
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }
}
