// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;

namespace Tript.App;

internal sealed class RecordingDeferral
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Action> _pending = new(StringComparer.Ordinal);
    private readonly Action<Action> _dispatch;
    private bool _recording;

    internal RecordingDeferral(Action<Action>? dispatch = null)
    {
        _dispatch = dispatch ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
    }

    internal bool IsRecording
    {
        get
        {
            lock (_gate)
                return _recording;
        }
    }

    internal void Run(string name, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            if (_recording)
            {
                _pending[name] = work;
                return;
            }
        }

        Invoke(name, work);
    }

    internal void SetRecording(bool recording)
    {
        List<KeyValuePair<string, Action>> due;
        lock (_gate)
        {
            _recording = recording;
            if (recording || _pending.Count == 0)
                return;

            due = [.. _pending];
            _pending.Clear();
        }

        foreach (var (name, work) in due)
            _dispatch(() => Invoke(name, work));
    }

    private static void Invoke(string name, Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "AppHost: deferred {Work} failed.", name);
        }
    }
}
