// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using Tript.Core;

namespace Tript.Recorder;

public sealed record FullscreenGameCandidate(
    int ProcessId,
    string Executable,
    string ExecutablePath,
    DateTimeOffset? ProcessStartTime = null);

public sealed class FullscreenGameDetector : IDisposable
{
    private const uint MonitorDefaultToNearest = 2;
    private readonly TimeSpan _pollInterval;
    private readonly Func<FullscreenGameCandidate?> _candidateProbe;
    private readonly object _gate = new();
    private readonly SerializedDetectorCallbackQueue _callbacks =
        new("Tript fullscreen detector callbacks");
    private KnownTargetSet _knownTargets;
    private long _targetVersion;
    private Timer? _timer;
    private FullscreenGameCandidate? _activeCandidate;
    private FullscreenGameCandidate? _pendingCandidate;
    private int _pendingPolls;
    private bool _disposed;
    private int _ticking;

    public FullscreenGameDetector(
        IEnumerable<GameDetectionTarget> knownTargets,
        TimeSpan? pollInterval = null)
        : this(knownTargets, ProbeCandidate, pollInterval)
    {
    }

    internal FullscreenGameDetector(
        IEnumerable<GameDetectionTarget> knownTargets,
        Func<FullscreenGameCandidate?> candidateProbe,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(candidateProbe);
        _knownTargets = CreateKnownTargetSet(knownTargets);
        _candidateProbe = candidateProbe;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    public event Action<FullscreenGameCandidate>? CandidateFound;

    public event Action<FullscreenGameCandidate>? CandidateCleared;

    public void UpdateKnownTargets(IEnumerable<GameDetectionTarget> knownTargets)
    {
        var replacement = CreateKnownTargetSet(knownTargets);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _knownTargets = replacement;
            _targetVersion++;
        }
    }

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
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _callbacks.Dispose();
    }

    internal void PollOnce() => OnTick(null);

    internal void WaitForCallbacks() => _callbacks.WaitUntilIdle();

    internal bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
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
        long targetVersion;
        lock (_gate)
        {
            if (_disposed)
                return;
            targetVersion = _targetVersion;
        }

        var probed = NormalizeCandidate(_candidateProbe());
        FullscreenGameCandidate? cleared = null;
        FullscreenGameCandidate? found = null;

        lock (_gate)
        {
            if (_disposed || targetVersion != _targetVersion)
                return;

            if (probed is not null && IsKnown(probed, _knownTargets))
                probed = null;

            if (_activeCandidate is not null && SameCandidate(_activeCandidate, probed))
            {
                _pendingCandidate = null;
                _pendingPolls = 0;
                return;
            }

            if (_activeCandidate is not null)
            {
                cleared = _activeCandidate;
                _activeCandidate = null;
            }

            if (probed is null)
            {
                _pendingCandidate = null;
                _pendingPolls = 0;
            }
            else if (SameCandidate(_pendingCandidate, probed))
            {
                _pendingPolls++;
                if (_pendingPolls >= 2)
                {
                    found = probed;
                    _activeCandidate = probed;
                    _pendingCandidate = null;
                    _pendingPolls = 0;
                }
            }
            else
            {
                _pendingCandidate = probed;
                _pendingPolls = 1;
            }
        }

        if (cleared is not null)
            Enqueue(CandidateCleared, cleared, targetVersion);
        if (found is not null)
            Enqueue(CandidateFound, found, targetVersion);
    }

    private static FullscreenGameCandidate? NormalizeCandidate(FullscreenGameCandidate? candidate)
    {
        if (candidate is null || candidate.ProcessId <= 0)
            return null;

        var path = ProcessNameGameDetector.NormalizePath(candidate.ExecutablePath);
        if (path is null || IsSystemExecutable(path))
            return null;

        var executable = ExecutableNames.Normalize(path);

        return executable.Length == 0
            ? null
            : new FullscreenGameCandidate(
                candidate.ProcessId, executable, path, candidate.ProcessStartTime);
    }

    private static bool IsKnown(FullscreenGameCandidate candidate, KnownTargetSet targets)
        => targets.Paths.Contains(candidate.ExecutablePath)
            || targets.Executables.Contains(candidate.Executable);

    private static KnownTargetSet CreateKnownTargetSet(IEnumerable<GameDetectionTarget> knownTargets)
    {
        ArgumentNullException.ThrowIfNull(knownTargets);
        var paths = new HashSet<string>(PathComparer);
        var executables = new HashSet<string>(ExecutableNames.Comparer);

        foreach (var target in knownTargets)
        {
            ArgumentNullException.ThrowIfNull(target);
            var path = ProcessNameGameDetector.NormalizePath(target.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(target.ExecutablePath) && path is null)
                throw new ArgumentException("A target executable path must be valid.", nameof(knownTargets));

            if (path is not null)
                paths.Add(path);
            else
            {
                var executable = ProcessNameGameDetector.NormalizeProcessName(target.Executable);
                if (executable.Length > 0)
                    executables.Add(executable);
            }
        }

        return new KnownTargetSet(paths, executables);
    }

    private static FullscreenGameCandidate? ProbeCandidate()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window) || !IsWindowFullscreen(window))
            return null;

        GetWindowThreadProcessId(window, out var nativeProcessId);
        if (nativeProcessId == 0 || nativeProcessId == Environment.ProcessId)
            return null;

        try
        {
            using var process = Process.GetProcessById((int)nativeProcessId);
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return new FullscreenGameCandidate(
                (int)nativeProcessId,
                ExecutableNames.Normalize(path),
                path,
                process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }

    private void Enqueue(
        Action<FullscreenGameCandidate>? handlers,
        FullscreenGameCandidate candidate,
        long generation)
    {
        if (handlers is null)
            return;

        _callbacks.Enqueue(() => Raise(handlers, candidate, generation));
    }

    private void Raise(
        Action<FullscreenGameCandidate> handlers,
        FullscreenGameCandidate candidate,
        long generation)
    {
        foreach (Action<FullscreenGameCandidate> handler in handlers.GetInvocationList())
        {
            lock (_gate)
            {
                if (_disposed || generation != _targetVersion)
                    return;
            }
            try
            {
                handler(candidate);
            }
            catch (Exception exception)
            {
                Log.Warning(exception,
                    "FullscreenGameDetector: a candidate subscriber threw; the watch continues.");
            }
        }
    }

    private static bool SameCandidate(
        FullscreenGameCandidate? left,
        FullscreenGameCandidate? right)
        => ReferenceEquals(left, right)
            || left is not null && right is not null
                && left.ProcessId == right.ProcessId
                && left.ProcessStartTime == right.ProcessStartTime
                && ExecutableNames.Comparer.Equals(left.Executable, right.Executable)
                && PathComparer.Equals(left.ExecutablePath, right.ExecutablePath);

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

        return roots.Any(root => FilePaths.IsUnder(fullPath, root));
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

    private static StringComparer PathComparer => FilePaths.Comparer;

    private sealed record KnownTargetSet(HashSet<string> Paths, HashSet<string> Executables);

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
