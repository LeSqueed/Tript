// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using Tript.Core;

namespace Tript.Recorder;

internal sealed record RunningProcess(int ProcessId, long ResidentBytes, DateTimeOffset? StartTime);

internal sealed class LinuxGameProcessProbe(
    Func<IReadOnlyList<string>> installRoots,
    IProcessFiles files,
    Func<IEnumerable<RunningProcess>> runningProcesses,
    Func<string, string> resolveLinks)
{
    private static readonly string[] HelperNames =
    [
        "UnityCrashHandler64", "UnityCrashHandler32", "CrashReportClient", "CrashReporter", "crashpad_handler",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_EOS_Setup", "start_protected_game", "BEService",
        "BEService_x64", "vc_redist.x64", "vc_redist.x86", "VC_redist.x64", "VC_redist.x86", "dxsetup", "DXSETUP",
        "UE4PrereqSetup_x64", "UEPrereqSetup_x64", "dotNetFx40_Full_setup",
    ];

    internal static LinuxGameProcessProbe ForThisMachine(Func<IReadOnlyList<string>> installRoots) =>
        new(installRoots, new ProcProcessFiles(), EnumerateRunningProcesses, FilePaths.ResolveLinks);

    internal FullscreenGameCandidate? Probe()
    {
        var roots = installRoots()
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => resolveLinks(root.TrimEnd('/')))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0)
            return null;

        FullscreenGameCandidate? best = null;
        long bestSize = -1;
        foreach (var process in runningProcesses())
        {
            if (process.ProcessId == Environment.ProcessId)
                continue;

            var reported = LinuxProcessIdentity.Read(files, process.ProcessId)?.ExecutablePath;
            if (reported is null)
                continue;

            var path = resolveLinks(reported);
            if (!roots.Any(root => FilePaths.IsAtOrUnder(path, root)))
                continue;

            var executable = ExecutableNames.Normalize(path);
            if (executable.Length == 0 || IsHelper(executable) || process.ResidentBytes <= bestSize)
                continue;

            best = new FullscreenGameCandidate(process.ProcessId, executable, path, process.StartTime);
            bestSize = process.ResidentBytes;
        }

        return best;
    }

    internal static bool IsHelper(string executable) =>
        HelperNames.Contains(executable, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<RunningProcess> EnumerateRunningProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                RunningProcess? running = null;
                try
                {
                    running = new RunningProcess(process.Id, process.WorkingSet64, process.StartTime.ToUniversalTime());
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception)
                {
                }

                if (running is not null)
                    yield return running;
            }
        }
    }
}
