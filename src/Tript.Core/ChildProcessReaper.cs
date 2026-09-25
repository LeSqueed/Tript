// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.ComponentModel;
using System.Diagnostics;

namespace Tript.Core;

internal sealed class ChildProcessReaper : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Process> _children = [];
    private bool _disposed;

    internal int TrackedCount
    {
        get
        {
            lock (_gate)
                return _children.Count;
        }
    }

    internal void Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        Process child;
        try
        {
            child = Process.GetProcessById(process.Id);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return;
        }

        lock (_gate)
        {
            if (!_disposed)
            {
                ForgetExited();
                _children.Add(child);
                return;
            }
        }

        KillTree(child);
    }

    public void Dispose()
    {
        Process[] children;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            children = [.. _children];
            _children.Clear();
        }

        foreach (var child in children)
            KillTree(child);
    }

    private void ForgetExited() =>
        _children.RemoveAll(child =>
        {
            if (!HasExited(child))
                return false;
            child.Dispose();
            return true;
        });

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or Win32Exception or NotSupportedException
                  or AggregateException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
