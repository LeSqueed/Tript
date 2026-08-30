// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Recorder;

public sealed record GameDetectionTarget(string GameId, string Executable, string? ExecutablePath = null);

public sealed record DetectedGameProcess(
    string GameId,
    int ProcessId,
    string Executable,
    string ExecutablePath,
    DateTimeOffset? ProcessStartTime = null);

public interface IGameDetector : IDisposable
{
    event Action<DetectedGameProcess>? GameStarted;

    event Action<DetectedGameProcess>? GameStopped;

    void Start();
}

internal sealed class SerializedDetectorCallbackQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Action> _callbacks = [];
    private readonly Thread _worker;
    private bool _disposed;
    private bool _active;

    internal SerializedDetectorCallbackQueue(string name)
    {
        _worker = new Thread(Run) { IsBackground = true, Name = name };
        _worker.Start();
    }

    internal void Enqueue(Action callback)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _callbacks.Enqueue(callback);
            Monitor.Pulse(_gate);
        }
    }

    internal void WaitUntilIdle()
    {
        lock (_gate)
        {
            while (!_disposed && (_active || _callbacks.Count > 0))
                Monitor.Wait(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _callbacks.Clear();
            Monitor.PulseAll(_gate);
        }

        if (Thread.CurrentThread != _worker)
            _worker.Join();
    }

    private void Run()
    {
        while (true)
        {
            Action callback;
            lock (_gate)
            {
                while (!_disposed && _callbacks.Count == 0)
                    Monitor.Wait(_gate);
                if (_disposed)
                    return;
                callback = _callbacks.Dequeue();
                _active = true;
            }

            callback();

            lock (_gate)
            {
                _active = false;
                if (_callbacks.Count == 0)
                    Monitor.PulseAll(_gate);
            }
        }
    }
}
