// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Photino.NET;
using Tript.App;

namespace Tript.Shell;

internal static class NativePickers
{
    internal static string? PickRecordingFolder(PhotinoWindow window, AppHost host)
    {
        var configured = host.SettingsStore.Load().Recording.OutputDirectory;
        var defaultPath = string.IsNullOrWhiteSpace(configured)
            ? Tript.Settings.RecordingLocations.DefaultDirectory()
            : configured;

        return PickFolder(window, "Select recordings folder", ExistingFolderOrParent(defaultPath));
    }

    internal static string? PickTrainingFolder(PhotinoWindow window)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return PickFolder(window, "Select training data folder",
            ExistingFolderOrParent(Path.Combine(documents, "Tript", "training")));
    }

    private static string ExistingFolderOrParent(string path)
    {
        var candidate = Path.GetFullPath(path);
        while (!Directory.Exists(candidate))
        {
            var parent = Directory.GetParent(candidate)?.FullName;
            if (parent is null || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidate = parent;
        }
        return candidate;
    }

    private static string? PickFolder(PhotinoWindow window, string title, string defaultPath)
    {
        string? result = null;
        using var completed = new ManualResetEventSlim(false);
        window.Invoke(() =>
        {
            try
            {
                var picked = window.ShowOpenFolder(title, defaultPath, multiSelect: false);
                if (picked.Length > 0)
                    result = picked[0];
            }
            finally
            {
                completed.Set();
            }
        });

        if (!completed.Wait(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("The native folder picker did not return.");
        return result;
    }

    internal static string? PickExecutable(PhotinoWindow window)
    {
        string? result = null;
        using var completed = new ManualResetEventSlim(false);
        window.Invoke(() =>
        {
            try
            {
                var picked = window.ShowOpenFile("Select game executable",
                    ExistingFolderOrParent(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                    multiSelect: false);
                if (picked.Length > 0)
                    result = picked[0];
            }
            finally
            {
                completed.Set();
            }
        });

        if (!completed.Wait(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("The native executable picker did not return.");
        return result;
    }
}
