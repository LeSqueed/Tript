// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.ComponentModel;
using System.Diagnostics;

namespace Tript.App;

internal sealed record ObsPresence(bool Running, string? Version)
{
    internal static ObsPresence Absent { get; } = new(false, null);
}

internal sealed class ObsProcessWatcher : IDisposable
{
    internal static readonly string[] ProcessNames = ["obs64", "obs32"];

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly Func<ObsPresence> _probe;
    private readonly Action<ObsPresence> _changed;
    private readonly TimeSpan _interval;
    private readonly Lock _gate = new();
    private Timer? _timer;
    private ObsPresence _current = ObsPresence.Absent;
    private int _generation;
    private bool _disposed;

    internal ObsProcessWatcher(Action<ObsPresence> changed, Func<ObsPresence>? probe = null,
        TimeSpan? interval = null)
    {
        _changed = changed;
        _probe = probe ?? Probe;
        _interval = interval ?? DefaultInterval;
    }

    internal ObsPresence Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    internal bool IsWatching
    {
        get
        {
            lock (_gate)
                return _timer is not null;
        }
    }

    internal void SetWatching(bool watching)
    {
        Timer? stopped = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            if (watching && _timer is null)
            {
                var generation = ++_generation;
                _timer = new Timer(_ => Poll(generation), null, TimeSpan.Zero, _interval);
            }
            else if (!watching && _timer is not null)
            {
                stopped = _timer;
                _timer = null;
                _generation++;
                _current = ObsPresence.Absent;
            }
        }

        stopped?.Dispose();
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    internal void Poll() => Poll(null);

    private void Poll(int? generation)
    {
        ObsPresence next;
        try
        {
            next = _probe();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || next == _current || (generation is { } expected && expected != _generation))
                return;

            _current = next;
        }

        _changed(next);
    }

    internal static ObsPresence Probe()
    {
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                    return new ObsPresence(true, ReadVersion(processes[0]));
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        return ObsPresence.Absent;
    }

    private static string? ReadVersion(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return path is null ? null : FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or NotSupportedException or FileNotFoundException)
        {
            return null;
        }
    }
}
