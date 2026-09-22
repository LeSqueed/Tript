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

// The binding and media libraries deliberately take no logging dependency, which left their catch
// blocks with nowhere to report to, so failures there vanished. The host installs a sink at startup
// and these libraries report through it. With no sink installed, reports are dropped: a library
// used on its own behaves exactly as before.
public static class Diagnostics
{
    private static Action<DiagnosticLevel, string, Exception?>? _sink;

    public static void SetSink(Action<DiagnosticLevel, string, Exception?>? sink) =>
        Volatile.Write(ref _sink, sink);

    // Called from catch blocks and from native callbacks that libobs invokes, where an escaping
    // exception terminates the process, so this must never throw.
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

    // For callbacks libobs invokes every frame or audio tick. A failure there repeats at that rate,
    // and reporting each one would flood the log and roll out everything that explains it. The first
    // report carries the stack, which is the useful part.
    public static void ReportFirst(ref int reported, DiagnosticLevel level, string message,
        Exception? exception = null)
    {
        if (Interlocked.Exchange(ref reported, 1) == 0)
            Report(level, message, exception);
    }
}
