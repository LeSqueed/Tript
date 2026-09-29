// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Core;

public enum DiagnosticLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

// Lets the logging-free libraries report failures; the host installs the sink, and without one reports are dropped.
public static class Diagnostics
{
    private static Action<DiagnosticLevel, string, Exception?>? _sink;

    public static void SetSink(Action<DiagnosticLevel, string, Exception?>? sink) =>
        Volatile.Write(ref _sink, sink);

    // Must never throw: it runs inside libobs callbacks, where an escaping exception kills the process.
    public static void Report(DiagnosticLevel level, string message, Exception? exception = null)
    {
        var sink = Volatile.Read(ref _sink);
        if (sink is null)
            return;

        try
        {
            sink(level, message, exception);
        }
        catch (Exception)
        {
        }
    }

    // For per-frame callbacks: report only the first failure so a repeating one cannot flood the log.
    public static void ReportFirst(ref int reported, DiagnosticLevel level, string message,
        Exception? exception = null)
    {
        if (Interlocked.Exchange(ref reported, 1) == 0)
            Report(level, message, exception);
    }
}
