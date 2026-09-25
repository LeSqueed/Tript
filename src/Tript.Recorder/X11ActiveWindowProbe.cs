// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Serilog;
using Tript.Core;

namespace Tript.Recorder;

internal sealed class X11ActiveWindowProbe(
    Func<IX11Desktop?> connect,
    IProcessFiles files,
    Func<int, DateTimeOffset?> processStartTime,
    Func<string, string> resolveLinks,
    TimeProvider time,
    int ownProcessId) : IDisposable
{
    internal static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);
    private const int EdgeTolerance = 1;

    private static readonly string[] SystemRoots = ["/usr", "/opt/google", "/snap", "/app"];
    private static readonly string[] SteamRuntimeFolders = ["ubuntu12_32", "ubuntu12_64"];
    private static readonly string[] WineSystemFolders = ["/drive_c/windows/", "/dosdevices/c:/windows/"];
    private static readonly HashSet<string> SteamClientNames = new(StringComparer.Ordinal) { "steam", "steamwebhelper" };

    private readonly object _gate = new();
    private IX11Desktop? _desktop;
    private DateTimeOffset? _reconnectAt;
    private bool _disposed;

    internal static X11ActiveWindowProbe ForThisMachine() =>
        new(NativeX11Desktop.Open, new ProcProcessFiles(), ProcessStartTime, FilePaths.ResolveLinks,
            TimeProvider.System, Environment.ProcessId);

    internal FullscreenGameCandidate? Probe()
    {
        X11ActiveWindow? window;
        lock (_gate)
        {
            if (_disposed)
                return null;
            window = ReadActiveWindow();
        }

        return window is null ? null : CandidateFor(window);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Disconnect();
        }
    }

    internal static bool IsFullscreen(
        ScreenRect window,
        IReadOnlyCollection<ulong> stateAtoms,
        ulong fullscreenAtom,
        IReadOnlyList<ScreenRect> monitors)
        => (fullscreenAtom != 0 && stateAtoms.Contains(fullscreenAtom))
            || monitors.Any(monitor => Covers(window, monitor));

    internal static bool IsNeverAGame(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            return true;

        if (SystemRoots.Any(root => FilePaths.IsUnder(path, root)))
            return true;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => SteamRuntimeFolders.Contains(segment, StringComparer.Ordinal)))
            return true;

        if (WineSystemFolders.Any(folder => path.Contains(folder, StringComparison.OrdinalIgnoreCase)))
            return true;

        var executable = ExecutableNames.Normalize(path);
        return executable.Length == 0
            || SteamClientNames.Contains(executable)
            || LinuxGameProcessProbe.IsHelper(executable);
    }

    private X11ActiveWindow? ReadActiveWindow()
    {
        var desktop = ConnectedDesktop();
        if (desktop is null)
            return null;

        try
        {
            var window = desktop.ReadActiveWindow();
            if (desktop.IsConnected)
                return window;

            Log.Information("X11 fullscreen detection lost its X server connection; retrying in {Delay}.", ReconnectDelay);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "X11 fullscreen detection could not read the active window; retrying in {Delay}.", ReconnectDelay);
        }

        Disconnect();
        _reconnectAt = time.GetUtcNow() + ReconnectDelay;
        return null;
    }

    private IX11Desktop? ConnectedDesktop()
    {
        if (_desktop is not null)
            return _desktop;

        if (_reconnectAt is { } retry && time.GetUtcNow() < retry)
            return null;

        try
        {
            _desktop = connect();
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "X11 fullscreen detection could not reach an X server.");
            _desktop = null;
        }

        _reconnectAt = _desktop is null ? time.GetUtcNow() + ReconnectDelay : null;
        return _desktop;
    }

    private void Disconnect()
    {
        var desktop = _desktop;
        _desktop = null;
        try
        {
            desktop?.Dispose();
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "X11 fullscreen detection could not close its X server connection.");
        }
    }

    private FullscreenGameCandidate? CandidateFor(X11ActiveWindow window)
    {
        if (window.ProcessId is not { } processId || processId == ownProcessId)
            return null;

        if (!window.WindowRects.Any(rect => IsFullscreen(rect, window.StateAtoms, window.FullscreenAtom, window.Monitors)))
            return null;

        var reported = LinuxProcessIdentity.Read(files, processId)?.ExecutablePath;
        if (reported is null)
            return null;

        var path = ResolvedOrReported(reported);
        if (IsNeverAGame(path))
            return null;

        return new FullscreenGameCandidate(processId, ExecutableNames.Normalize(path), path, processStartTime(processId));
    }

    private string ResolvedOrReported(string path)
    {
        try
        {
            return resolveLinks(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    private static bool Covers(ScreenRect window, ScreenRect monitor)
        => monitor.Width > 0 && monitor.Height > 0
            && window.X <= monitor.X + EdgeTolerance
            && window.Y <= monitor.Y + EdgeTolerance
            && window.Right >= monitor.Right - EdgeTolerance
            && window.Bottom >= monitor.Bottom - EdgeTolerance;

    private static DateTimeOffset? ProcessStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
