// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Serilog;
using Tript.Core;

namespace Tript.Recorder;

public sealed class ProcessNameGameDetector : PollingGameDetector, IGameDetector
{
    private const string CallbackThreadName = "Tript process detector callbacks";
    private readonly Func<IReadOnlySet<string>, IReadOnlyList<ProcessSnapshot>> _processProbe;
    private readonly IProcessFiles _processFiles = new ProcProcessFiles();
    private readonly Dictionary<int, DetectedGameProcess> _running = [];
    private Dictionary<int, ProbedIdentity> _probed = [];
    private TargetSet _targets;

    public ProcessNameGameDetector(
        IEnumerable<GameDetectionTarget> targets,
        TimeSpan? pollInterval = null)
        : base(pollInterval ?? DefaultPollInterval, CallbackThreadName)
    {
        _targets = CreateTargetSet(targets);
        _processProbe = ProbeProcesses;
    }

    internal ProcessNameGameDetector(
        IEnumerable<GameDetectionTarget> targets,
        Func<IReadOnlySet<string>, IReadOnlyList<ProcessSnapshot>> processProbe,
        TimeSpan? pollInterval = null)
        : base(pollInterval ?? DefaultPollInterval, CallbackThreadName)
    {
        ArgumentNullException.ThrowIfNull(processProbe);
        _targets = CreateTargetSet(targets);
        _processProbe = processProbe;
    }

    private static TimeSpan DefaultPollInterval => TimeSpan.FromSeconds(5);

    public event Action<DetectedGameProcess>? GameStarted;

    public event Action<DetectedGameProcess>? GameStopped;

    public void UpdateTargets(IEnumerable<GameDetectionTarget> targets)
    {
        var replacement = CreateTargetSet(targets);
        lock (Gate)
        {
            BumpTargetVersionLocked();
            _targets = replacement;
        }
    }

    private protected override void Poll()
    {
        TargetSet targets;
        long targetVersion;
        lock (Gate)
        {
            if (!TryReadTargetVersionLocked(out targetVersion))
                return;
            targets = _targets;
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
        lock (Gate)
        {
            if (IsStaleLocked(targetVersion))
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

        foreach (var process in stopped)
            Enqueue(GameStopped, process, targetVersion);
        foreach (var process in started)
            Enqueue(GameStarted, process, targetVersion);
    }

    private bool TryKeepPathMatch(
        ProcessSnapshot snapshot,
        string executable,
        TargetSet targets,
        out DetectedGameProcess tracked)
    {
        lock (Gate)
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
        lock (Gate)
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

                var linux = OperatingSystem.IsLinux();
                var identity = linux ? ReadLinuxIdentity(processId) : null;
                if (identity is not null)
                    executable = identity.Executable;

                if (!candidates.Contains(NormalizeProcessName(executable)))
                    continue;

                DateTimeOffset? startTime = null;
                try { startTime = process.StartTime.ToUniversalTime(); }
                catch (Exception exception) when (IsInspectionFailure(exception)) { }

                string? path;
                if (linux)
                {
                    path = CanonicalLinuxPath(identity?.ExecutablePath);
                }
                else if (previous.TryGetValue(processId, out var known)
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

        lock (Gate)
            _probed = probed;
        return snapshots;
    }

    private ProcessIdentity? ReadLinuxIdentity(int processId)
    {
        try
        {
            return LinuxProcessIdentity.Read(_processFiles, processId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? CanonicalLinuxPath(string? path)
    {
        if (path is null)
            return null;

        try
        {
            return FilePaths.ResolveLinks(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
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

            var path = NormalizeTargetPath(target.ExecutablePath);
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

    private static string? NormalizeTargetPath(string? path)
    {
        var normalized = NormalizePath(path);
        return OperatingSystem.IsLinux() ? CanonicalLinuxPath(normalized) : normalized;
    }

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
