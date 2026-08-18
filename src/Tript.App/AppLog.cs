// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Tript.App;

// Points Serilog at the host's stderr. Without this Log.Logger stays Serilog's silent default and
// every Log.* call in the recorder and the detection host is discarded — including the game-capture
// hook diagnostics, which are the only view into why a capture did or did not attach.
//
// A custom sink rather than Serilog.Sinks.Console: the rest of the host already reports through
// Console.Error with a "Tript.App:" prefix, and matching that keeps one stream to read instead of
// two formats interleaved.
internal static class AppLog
{
    internal static void Configure(LogEventLevel minimum = LogEventLevel.Information)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimum)
            .WriteTo.Sink(new StandardErrorSink())
            .CreateLogger();
    }

    private sealed class StandardErrorSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);

            var level = logEvent.Level switch
            {
                LogEventLevel.Verbose or LogEventLevel.Debug => "debug",
                LogEventLevel.Information => "info",
                LogEventLevel.Warning => "warn",
                _ => "error",
            };

            Console.Error.WriteLine($"Tript.App [{level}]: {logEvent.RenderMessage()}");
            if (logEvent.Exception is { } exception)
                Console.Error.WriteLine($"Tript.App [{level}]: {exception}");
        }
    }
}
