// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

internal static class ObsLibrary
{
    internal const string Name = "obs";

    internal const string RuntimeDirectoryVariable = "TRIPT_OBS_RUNTIME_DIR";

    private static readonly Lock Gate = new();
    private static string? _runtimeDirectory;
    private static nint _handle;

    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(ObsLibrary).Assembly, Resolve);

    internal static void SetRuntimeDirectory(string? directory)
    {
        lock (Gate)
        {
            if (_handle != nint.Zero && !string.Equals(_runtimeDirectory, directory, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The OBS runtime is already loaded; its directory cannot be changed for this process.");

            _runtimeDirectory = directory;
        }
    }

    internal static nint EnsureLoaded()
    {
        lock (Gate)
        {
            if (_handle == nint.Zero)
                _handle = Load();

            return _handle;
        }
    }

    private static nint Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == Name ? EnsureLoaded() : nint.Zero;

    private static nint Load()
    {
        var directory = _runtimeDirectory ?? Environment.GetEnvironmentVariable(RuntimeDirectoryVariable);
        var attempted = new List<string>();

        foreach (var fileName in CandidateFileNames())
        {
            if (!string.IsNullOrEmpty(directory))
            {
                var path = Path.Combine(directory, fileName);
                attempted.Add(path);
                if (NativeLibrary.TryLoad(path, out var fromDirectory))
                    return fromDirectory;

                continue;
            }

            attempted.Add(fileName);
            if (NativeLibrary.TryLoad(fileName, typeof(ObsLibrary).Assembly, DllImportSearchPath.SafeDirectories, out var probed))
                return probed;
        }

        throw new DllNotFoundException(
            $"Could not load the OBS runtime. Tried: {string.Join(", ", attempted)}. " +
            $"Set {RuntimeDirectoryVariable} or call ObsRuntime.SetRuntimeDirectory to point at the bundled runtime.");
    }

    private static string[] CandidateFileNames() =>
        OperatingSystem.IsWindows() ? ["obs64.dll", "obs.dll"] : ["libobs.so.0", "libobs.so"];
}
