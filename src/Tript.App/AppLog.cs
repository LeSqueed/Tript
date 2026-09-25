// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Tript.Core;
using Tript.Settings;

namespace Tript.App;

internal static class AppLog
{
    private const int RetainedFiles = 10;

    private const string LogsFolderName = "logs";

    // Tript runs for days in the background, and pruning only ran at startup, so a single session
    // could grow one file without bound. Rolling at a fixed size keeps disk use capped at roughly
    // RetainedFiles * MaxFileBytes.
    private const long MaxFileBytes = 20L * 1024 * 1024;

    private static int _crashHandlersInstalled;

    private static string? _logDirectoryOverride;

    internal static string LogDirectory =>
        Volatile.Read(ref _logDirectoryOverride) ?? DefaultLogDirectory(OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            () => SettingsFilePaths.ConfigDirectory);

    internal static string DefaultLogDirectory(bool windows, Func<string, string?> environment, string home,
        Func<string> configDirectory)
    {
        if (windows)
            return Path.Combine(configDirectory(), LogsFolderName);

        var stateHome = environment("XDG_STATE_HOME");
        var root = !string.IsNullOrEmpty(stateHome) && Path.IsPathRooted(stateHome)
            ? stateHome
            : Path.Combine(home, ".local", "state");
        return Path.Combine(root, SettingsFilePaths.DirectoryName, LogsFolderName);
    }

    internal static string? CurrentFile { get; private set; }

    // Release builds write Information and above to the file: every UI command logs a Debug line, so
    // Debug by default filled users' logs with noise. Debug builds, or --verbose-log on a release,
    // write Debug too, including libobs's own debug output (module loading, for one).
    internal static LogEventLevel DefaultFileLevel(bool verbose)
    {
#if DEBUG
        return LogEventLevel.Debug;
#else
        return verbose ? LogEventLevel.Debug : LogEventLevel.Information;
#endif
    }

    internal static void Configure(string? logDirectory = null, bool verbose = false,
        LogEventLevel minimum = LogEventLevel.Information)
    {
        Volatile.Write(ref _logDirectoryOverride, string.IsNullOrWhiteSpace(logDirectory) ? null : logDirectory);

        // Replacing Log.Logger does not dispose the previous one, so a second Configure would leak
        // its file handle. CloseAndFlush disposes it and is harmless the first time.
        Log.CloseAndFlush();

        var configuration = new LoggerConfiguration()
            .WriteTo.Sink(new StandardErrorSink(), restrictedToMinimumLevel: minimum);

        var floor = minimum;
        if (TryCreateFileSink() is { } fileSink)
        {
            var fileLevel = DefaultFileLevel(verbose);
            configuration = configuration.WriteTo.Sink(fileSink, restrictedToMinimumLevel: fileLevel);
            if (fileLevel < floor)
                floor = fileLevel;
        }

        Log.Logger = configuration.MinimumLevel.Is(floor).CreateLogger();

        Diagnostics.SetSink(static (level, message, exception) => Log.Write(level switch
        {
            DiagnosticLevel.Error => LogEventLevel.Error,
            DiagnosticLevel.Warning => LogEventLevel.Warning,
            DiagnosticLevel.Information => LogEventLevel.Information,
            _ => LogEventLevel.Debug,
        }, exception, "{Message}", message));
    }

    internal static void Shutdown() => Log.CloseAndFlush();

    // Nothing else in the process catches an exception that escapes a thread, so without these a
    // crash leaves a log that simply stops. Safe to call before Configure: Log is a silent logger
    // until then, and Configure swaps in the real one underneath these handlers.
    internal static void InstallCrashHandlers()
    {
        if (Interlocked.Exchange(ref _crashHandlersInstalled, 1) != 0)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception,
                "Unhandled exception; the process is terminating: {Terminating}", args.IsTerminating);
            if (args.IsTerminating)
                Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "A background task failed and nothing observed the exception");
            args.SetObserved();
        };
    }

    private static FileSink? TryCreateFileSink()
    {
        try
        {
            var directory = LogDirectory;
            Directory.CreateDirectory(directory);
            PruneOldLogs(directory, keep: RetainedFiles - 1);
            return new FileSink(directory, $"tript-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Tript.App [warn]: file logging is disabled: {exception.Message}");
            return null;
        }
    }

    private static void PruneOldLogs(string directory, int keep)
    {
        try
        {
            var stale = new DirectoryInfo(directory)
                .EnumerateFiles("tript-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(keep);
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
        // Flushing every line made each debug message a synchronous disk write, including the
        // detection loop's once-per-tick lines. Anything at Information or above still flushes at
        // once, so the lines that explain a crash reach disk before it; debug lines flush at most
        // once a second, and Shutdown flushes whatever is left.
        private const long DebugFlushIntervalMilliseconds = 1000;

        private readonly string _directory;
        private readonly string _stem;
        private readonly Lock _gate = new();
        private StreamWriter _writer;
        private long _written;
        private int _part;
        private long _lastFlush;

        internal FileSink(string directory, string stem)
        {
            _directory = directory;
            _stem = stem;
            _writer = Open(PathFor(0));
        }

        private string PathFor(int part) =>
            Path.Combine(_directory, part == 0 ? $"{_stem}.log" : $"{_stem}-{part}.log");

        private static StreamWriter Open(string path)
        {
            var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            CurrentFile = path;
            return writer;
        }

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);

            var line = $"{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Format(logEvent)}";
            lock (_gate)
            {
                try
                {
                    if (_written >= MaxFileBytes)
                        Roll();

                    _writer.WriteLine(line);
                    _written += line.Length + Environment.NewLine.Length;
                    if (logEvent.Exception is { } exception)
                    {
                        var text = exception.ToString();
                        _writer.WriteLine(text);
                        _written += text.Length + Environment.NewLine.Length;
                    }

                    var now = Environment.TickCount64;
                    if (logEvent.Level >= LogEventLevel.Information
                        || now - _lastFlush >= DebugFlushIntervalMilliseconds)
                    {
                        _writer.Flush();
                        _lastFlush = now;
                    }
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
            }
        }

        private void Roll()
        {
            _writer.Dispose();
            _part++;
            _written = 0;
            _writer = Open(PathFor(_part));
            PruneOldLogs(_directory, keep: RetainedFiles);
        }

        public void Dispose()
        {
            lock (_gate)
                _writer.Dispose();
        }
    }
}
