// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace Tript.Recorder;

// Finds an unlisted game only when its foreground window fills a monitor. This is deliberately a
// fallback: the packaged catalogue remains the source of stable identities for supported games.
public sealed class FullscreenGameDetector : IGameDetector
{
    private const uint MonitorDefaultToNearest = 2;
    private readonly HashSet<string> _knownExecutables;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private Timer? _timer;
    private int _activeProcessId;
    private string _activeExecutable = string.Empty;
    private bool _disposed;
    private int _ticking;

    public FullscreenGameDetector(IEnumerable<string> knownExecutables,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(knownExecutables);
        _knownExecutables = knownExecutables
            .Select(ProcessNameGameDetector.NormalizeProcessName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    public event Action<string>? GameStarted;

    public event Action<string>? GameStopped;

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (_gate)
        {
            if (_disposed || _timer is not null)
                return;
            _timer = new Timer(OnTick, null, TimeSpan.Zero, _pollInterval);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0)
            return;

        try
        {
            Poll();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "FullscreenGameDetector: a poll failed; the watch continues.");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private void Poll()
    {
        int activePid;
        lock (_gate)
        {
            if (_disposed)
                return;
            activePid = _activeProcessId;
        }

        if (activePid != 0)
        {
            if (IsProcessRunning(activePid))
                return;

            string stoppedExecutable;
            lock (_gate)
            {
                if (_activeProcessId != activePid)
                    return;

                _activeProcessId = 0;
                stoppedExecutable = _activeExecutable;
                _activeExecutable = string.Empty;
            }
            RaiseStopped(stoppedExecutable);
            return;
        }

        if (!TryGetFullscreenProcess(out var processId, out var executable))
            return;

        if (_knownExecutables.Contains(executable))
            return;

        lock (_gate)
        {
            if (_disposed || _activeProcessId != 0)
                return;
            _activeProcessId = processId;
            _activeExecutable = executable;
        }

        RaiseStarted(executable);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetFullscreenProcess(out int processId, out string executable)
    {
        processId = 0;
        executable = string.Empty;

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window)
            || !IsWindowFullscreen(window))
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var nativeProcessId);
        if (nativeProcessId == 0 || nativeProcessId == Environment.ProcessId)
            return false;

        try
        {
            using var process = Process.GetProcessById((int)nativeProcessId);
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path) || IsSystemExecutable(path))
                return false;

            var name = Path.GetFileName(path);
            var normalized = ProcessNameGameDetector.NormalizeProcessName(name);
            if (normalized.Length == 0)
                return false;

            processId = (int)nativeProcessId;
            executable = normalized;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsSystemExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return true;
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WindowsApps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps"),
        };

        return roots.Any(root => IsUnderDirectory(fullPath, root));
    }

    internal static bool IsUnderDirectory(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowFullscreen(IntPtr window)
    {
        if (!GetWindowRect(window, out var windowRect))
            return false;

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        return NearlyEqual(windowRect.Left, monitorInfo.Monitor.Left)
            && NearlyEqual(windowRect.Top, monitorInfo.Monitor.Top)
            && NearlyEqual(windowRect.Right, monitorInfo.Monitor.Right)
            && NearlyEqual(windowRect.Bottom, monitorInfo.Monitor.Bottom);
    }

    private static bool NearlyEqual(int left, int right) => Math.Abs(left - right) <= 1;

    private void RaiseStarted(string executable)
    {
        try
        {
            GameStarted?.Invoke(executable);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "FullscreenGameDetector: a subscriber threw on game start.");
        }
    }

    private void RaiseStopped(string executable)
    {
        try
        {
            GameStopped?.Invoke(executable);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "FullscreenGameDetector: a subscriber threw on game stop.");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        internal readonly int Left;
        internal readonly int Top;
        internal readonly int Right;
        internal readonly int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal int Flags;
    }
}
