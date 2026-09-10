// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Serilog;
using Tript.Core;

namespace Tript.Recorder;

public sealed class ProcessNameGameDetector : IGameDetector
{
    private readonly TimeSpan _pollInterval;
    private readonly Func<IReadOnlySet<string>, IReadOnlyList<ProcessSnapshot>> _processProbe;
    private readonly object _gate = new();
    private readonly SerializedDetectorCallbackQueue _callbacks =
        new("Tript process detector callbacks");
    private readonly Dictionary<int, DetectedGameProcess> _running = [];
    private Dictionary<int, ProbedIdentity> _probed = [];
    private TargetSet _targets;
    private long _targetVersion;
    private Timer? _timer;
    private bool _disposed;
    private int _ticking;

    public ProcessNameGameDetector(
        IEnumerable<GameDetectionTarget> targets,
        TimeSpan? pollInterval = null)
    {
        _targets = CreateTargetSet(targets);
        _processProbe = ProbeProcesses;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    internal ProcessNameGameDetector(
        IEnumerable<GameDetectionTarget> targets,
        Func<IReadOnlySet<string>, IReadOnlyList<ProcessSnapshot>> processProbe,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(processProbe);
        _targets = CreateTargetSet(targets);
        _processProbe = processProbe;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    public event Action<DetectedGameProcess>? GameStarted;

    public event Action<DetectedGameProcess>? GameStopped;

    public void UpdateTargets(IEnumerable<GameDetectionTarget> targets)
    {
        var replacement = CreateTargetSet(targets);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _targets = replacement;
            _targetVersion++;
        }
    }

    public void Start()
    {
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
            Log.Warning(exception, "ProcessNameGameDetector: a poll failed; the watch continues.");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private void Poll()
    {
        TargetSet targets;
        long targetVersion;
        lock (_gate)
        {
            if (_disposed)
                return;
            targets = _targets;
            targetVersion = _targetVersion;
        }

        var seen = new Dictionary<int, DetectedGameProcess>();
        foreach (var process in _processProbe(targets.CandidateExecutables))
        {
            if (process.ProcessId <= 0)
                continue;

            var executable = NormalizeProcessName(process.Executable);
            var path = NormalizePath(process.ExecutablePath);
            if (executable.Length == 0)
                continue;

            NormalizedTarget? target = null;
            if (path is not null && targets.ByPath.TryGetValue(path, out var pathTarget))
                target = pathTarget;
            else if (targets.ByExecutable.TryGetValue(executable, out var nameTarget))
                target = nameTarget;

            if (target is not null)
                seen[process.ProcessId] = new DetectedGameProcess(
                    target.GameId, process.ProcessId, executable, path ?? string.Empty,
                    process.ProcessStartTime);
            else if (path is null && TryKeepPathMatch(process, executable, targets, out var tracked))
                seen[process.ProcessId] = tracked;
        }

        List<DetectedGameProcess> started;
        List<DetectedGameProcess> stopped;
        lock (_gate)
        {
            if (_disposed || targetVersion != _targetVersion)
                return;

            stopped = _running.Values
                .Where(current => !seen.TryGetValue(current.ProcessId, out var next)
                    || !SameDetection(current, next))
                .ToList();
            started = seen.Values
                .Where(next => !_running.TryGetValue(next.ProcessId, out var current)
                    || !SameDetection(current, next))
                .ToList();

            _running.Clear();
            foreach (var process in seen.Values)
                _running.Add(process.ProcessId, process);
        }

        Enqueue(GameStopped, stopped, targetVersion);
        Enqueue(GameStarted, started, targetVersion);
    }

    private bool TryKeepPathMatch(
        ProcessSnapshot snapshot,
        string executable,
        TargetSet targets,
        out DetectedGameProcess tracked)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(snapshot.ProcessId, out tracked!)
                && SameProcessIdentity(tracked.ProcessStartTime, snapshot.ProcessStartTime)
                && ExecutableComparer.Equals(tracked.Executable, executable)
                && targets.ByPath.TryGetValue(tracked.ExecutablePath, out var target)
                && target.GameId == tracked.GameId)
                return true;
        }

        tracked = null!;
        return false;
    }

    private IReadOnlyList<ProcessSnapshot> ProbeProcesses(IReadOnlySet<string> candidates)
    {
        Dictionary<int, ProbedIdentity> previous;
        lock (_gate)
            previous = _probed;

        var snapshots = new List<ProcessSnapshot>();
        var probed = new Dictionary<int, ProbedIdentity>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string executable;
                int processId;
                try
                {
                    executable = process.ProcessName;
                    processId = process.Id;
                }
                catch (Exception exception) when (IsInspectionFailure(exception))
                {
                    continue;
                }

                if (!candidates.Contains(NormalizeProcessName(executable)))
                    continue;

                DateTimeOffset? startTime = null;
                try { startTime = process.StartTime.ToUniversalTime(); }
                catch (Exception exception) when (IsInspectionFailure(exception)) { }

                string? path;
                if (previous.TryGetValue(processId, out var known)
                    && SameProcessIdentity(known.StartTime, startTime))
                {
                    path = known.Path;
                }
                else
                {
                    path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception exception) when (IsInspectionFailure(exception)) { }
                }

                if (path is not null)
                    probed[processId] = new ProbedIdentity(startTime, path);
                snapshots.Add(new ProcessSnapshot(processId, executable, path, startTime));
            }
        }

        lock (_gate)
            _probed = probed;
        return snapshots;
    }

    private void Enqueue(
        Action<DetectedGameProcess>? handlers,
        IEnumerable<DetectedGameProcess> processes,
        long generation)
    {
        if (handlers is null)
            return;

        foreach (var process in processes)
            _callbacks.Enqueue(() => Raise(handlers, process, generation));
    }

    private void Raise(
        Action<DetectedGameProcess> handlers,
        DetectedGameProcess process,
        long generation)
    {
        foreach (Action<DetectedGameProcess> handler in handlers.GetInvocationList())
        {
            lock (_gate)
            {
                if (_disposed || generation != _targetVersion)
                    return;
            }
            try
            {
                handler(process);
            }
            catch (Exception exception)
            {
                Log.Warning(exception,
                    "ProcessNameGameDetector: a subscriber threw; the watch continues.");
            }
        }
    }

    private static bool IsInspectionFailure(Exception exception)
        => exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException;

    private static bool SameProcessIdentity(DateTimeOffset? left, DateTimeOffset? right)
        => left.HasValue && right.HasValue && left == right;

    private static bool SameDetection(DetectedGameProcess left, DetectedGameProcess right)
        => left.ProcessId == right.ProcessId
            && left.ProcessStartTime == right.ProcessStartTime
            && left.GameId == right.GameId
            && ExecutableComparer.Equals(left.Executable, right.Executable)
            && PathComparer.Equals(left.ExecutablePath, right.ExecutablePath);

    private static TargetSet CreateTargetSet(IEnumerable<GameDetectionTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var byPath = new Dictionary<string, NormalizedTarget>(PathComparer);
        var byExecutable = new Dictionary<string, NormalizedTarget>(ExecutableComparer);
        var candidates = new HashSet<string>(ExecutableComparer);

        foreach (var target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.GameId);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Executable);

            var executable = NormalizeProcessName(target.Executable);
            if (executable.Length == 0)
                throw new ArgumentException("A target executable must contain a file name.", nameof(targets));

            var path = NormalizePath(target.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(target.ExecutablePath) && path is null)
                throw new ArgumentException("A target executable path must be valid.", nameof(targets));

            var normalized = new NormalizedTarget(target.GameId);
            if (path is not null)
            {
                byPath.TryAdd(path, normalized);
                candidates.Add(NormalizeProcessName(Path.GetFileName(path)));
            }
            else
            {
                byExecutable.TryAdd(executable, normalized);
                candidates.Add(executable);
            }
        }

        return new TargetSet(byPath, byExecutable, candidates);
    }

    internal static string? NormalizePath(string? path) => FilePaths.TryGetFullPath(path);

    public static string NormalizeProcessName(string name) => ExecutableNames.Normalize(name);

    private static StringComparer PathComparer => FilePaths.Comparer;

    private static StringComparer ExecutableComparer => ExecutableNames.Comparer;

    private sealed record NormalizedTarget(string GameId);

    private sealed record TargetSet(
        IReadOnlyDictionary<string, NormalizedTarget> ByPath,
        IReadOnlyDictionary<string, NormalizedTarget> ByExecutable,
        IReadOnlySet<string> CandidateExecutables);

    private readonly record struct ProbedIdentity(DateTimeOffset? StartTime, string Path);
}

internal sealed record ProcessSnapshot(
    int ProcessId,
    string Executable,
    string? ExecutablePath = null,
    DateTimeOffset? ProcessStartTime = null);
