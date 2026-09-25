// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Serilog;

namespace Tript.App;

public sealed class TerminationSignals : IDisposable
{
    private static readonly PosixSignal[] Handled = [PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT];

    private readonly Action _terminate;
    private readonly List<PosixSignalRegistration> _registrations = [];
    private int _requested;

    private TerminationSignals(Action terminate) => _terminate = terminate;

    public static TerminationSignals Register(Action terminate)
    {
        ArgumentNullException.ThrowIfNull(terminate);

        var signals = new TerminationSignals(terminate);
        if (OperatingSystem.IsWindows())
            return signals;

        foreach (var signal in Handled)
            signals._registrations.Add(PosixSignalRegistration.Create(signal, signals.Handle));
        return signals;
    }

    internal static TerminationSignals ForTesting(Action terminate) => new(terminate);

    internal void Handle(PosixSignalContext context)
    {
        context.Cancel = true;
        if (Interlocked.Exchange(ref _requested, 1) != 0)
            return;

        Log.Information("Tript: {Signal} received; finishing the recording and exiting", context.Signal);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _terminate();
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Tript: the graceful exit after {Signal} failed", context.Signal);
            }
        });
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration.Dispose();
        _registrations.Clear();
    }
}
