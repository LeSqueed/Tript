// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Tript.Settings;

namespace Tript.App;

internal static class AppLog
{
    private const int RetainedFiles = 10;

    internal static void Configure(LogEventLevel minimum = LogEventLevel.Information)
    {
        var configuration = new LoggerConfiguration()
            .WriteTo.Sink(new StandardErrorSink(), restrictedToMinimumLevel: minimum);

        var floor = minimum;
        if (TryCreateFileSink() is { } fileSink)
        {
            configuration = configuration.WriteTo.Sink(fileSink, restrictedToMinimumLevel: LogEventLevel.Debug);
            if (LogEventLevel.Debug < floor)
                floor = LogEventLevel.Debug;
        }

        Log.Logger = configuration.MinimumLevel.Is(floor).CreateLogger();
    }

    private static FileSink? TryCreateFileSink()
    {
        try
        {
            var directory = Path.Combine(SettingsFilePaths.ConfigDirectory, "logs");
            Directory.CreateDirectory(directory);
            PruneOldLogs(directory);
            var path = Path.Combine(
                directory, $"tript-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            return new FileSink(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Tript.App [warn]: file logging is disabled: {exception.Message}");
            return null;
        }
    }

    private static void PruneOldLogs(string directory)
    {
        try
        {
            var stale = new DirectoryInfo(directory)
                .EnumerateFiles("tript-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(RetainedFiles - 1);
            foreach (var file in stale)
            {
                try { file.Delete(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Format(LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose or LogEventLevel.Debug => "debug",
            LogEventLevel.Information => "info",
            LogEventLevel.Warning => "warn",
            _ => "error",
        };

        return $"Tript.App [{level}]: {logEvent.RenderMessage()}";
    }

    private sealed class StandardErrorSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);

            Console.Error.WriteLine(Format(logEvent));
            if (logEvent.Exception is { } exception)
                Console.Error.WriteLine($"Tript.App [error]: {exception}");
        }
    }

    private sealed class FileSink : ILogEventSink, IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Lock _gate = new();

        internal FileSink(string path)
        {
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
        }

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);

            var line = $"{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Format(logEvent)}";
            lock (_gate)
            {
                try
                {
                    _writer.WriteLine(line);
                    if (logEvent.Exception is { } exception)
                        _writer.WriteLine(exception);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
                _writer.Dispose();
        }
    }
}
