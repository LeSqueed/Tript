// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Obs.Interop;

// Tript ships its own pinned OBS runtime rather than binding whatever the machine happens to have,
// so the loader has to be told where that runtime is before the first P/Invoke. The default probing
// path is kept as the last resort because a development box with OBS installed system-wide is the
// only place it is correct.
internal static class ObsLibrary
{
    // The name every LibraryImport in this assembly is declared against. It is resolved through
    // Resolve below, so it never has to match a real file name on either platform.
    internal const string Name = "obs";

    internal const string RuntimeDirectoryVariable = "TRIPT_OBS_RUNTIME_DIR";

    private static readonly Lock Gate = new();
    private static string? _runtimeDirectory;
    private static nint _handle;

    // Called from ObsNative's static constructor, which the runtime guarantees runs before the
    // first generated stub in that class does — and every P/Invoke in the binding lives there.
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(ObsLibrary).Assembly, Resolve);

    // Must be called before anything touches libobs; changing it afterwards cannot move a library
    // that is already mapped, so it throws rather than pretending to take effect.
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

    // SONAME first on Linux: libobs.so is the development symlink and is absent from a runtime
    // bundle, which is exactly the layout Tript ships. On Windows the official OBS bundle ships
    // obs64.dll under bin/64bit, so that is tried before obs.dll (the layout some packaging uses).
    private static string[] CandidateFileNames() =>
        OperatingSystem.IsWindows() ? ["obs64.dll", "obs.dll"] : ["libobs.so.0", "libobs.so"];
}
