// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Core;
using Tript.Recorder;
using Tript.Settings;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

// Backs the global (OS-level) hotkeys Tript.Shell registers — see WindowsHotkeys. These are plain
// AppHost methods so they can be unit tested the same way every other host action is, independent
// of the native Win32 registration that fires them.
internal sealed partial class AppHost
{
    internal void ToggleRecording()
    {
        if (IsRecording)
            StopRecordingOrReport();
        else
            StartRecordingOrReport(_currentGameId);
    }

    internal void AddLiveBookmark()
    {
        var session = _sessionTracker.Active;
        if (!IsRecording || session is null)
        {
            PushError("Start a recording before adding a bookmark.");
            return;
        }

        if (_activeRecordingMode?.RecordsSession() != true)
        {
            PushError(
                "Manual bookmarks aren't available in Replay Buffer Only mode. Try Quick Clip instead.");
            return;
        }

        var bookmark = new Bookmark
        {
            Type = BookmarkType.Manual,
            Time = DateTime.UtcNow - session.StartTimeUtc,
        };
        session.AddBookmark(bookmark);
        // No-ops unless Recording.AutomaticClipsEnabled + a replay buffer are both active for this
        // session — same gate every detected bookmark already goes through.
        RememberAutomaticClipBookmark(bookmark);
        PushContent();
    }

    internal void CreateQuickClipFromBuffer()
    {
        if (!IsRecording || _sessionTracker.Active is null)
        {
            PushError("Start a recording before creating a clip.");
            return;
        }

        if (_activeRecordingMode?.UsesReplayBuffer() != true)
        {
            PushError("Quick Clip needs a replay buffer. Enable it in Recording settings.");
            return;
        }

        var recorder = _recorder;
        var sourcePath = _activeSessionPath;
        if (recorder is null || sourcePath is null)
        {
            PushError("Quick Clip could not find the active recording.");
            return;
        }

        var quickClipSeconds = Math.Max(1, _settingsStore.Load().Hotkeys.QuickClipSeconds);
        var nowElapsed = (DateTime.UtcNow - _recordingStartUtc).TotalSeconds;
        var region = new LiveHighlightRegion
        {
            Start = TimeSpan.FromSeconds(Math.Max(0, nowElapsed - quickClipSeconds)),
            End = TimeSpan.FromSeconds(nowElapsed),
        };

        var sourceSessionPath = Path.GetRelativePath(EffectiveRoot, sourcePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var replayDirectory = Path.Combine(Path.GetTempPath(), "Tript", "replay");
        Directory.CreateDirectory(replayDirectory);
        var accepted = recorder.SaveReplayBuffer(replayDirectory,
            "tript-replay-%CCYY-%MM-%DD-%hh-%mm-%ss",
            replayPath =>
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        CreateLiveAutomaticHighlight(region, sourceSessionPath, sourcePath, replayPath, nowElapsed);
                    }
                    catch (Exception exception)
                    {
                        Log.Error(exception, "AppHost: quick clip failed for {SourcePath}", sourcePath);
                    }
                });
            });

        if (!accepted)
            PushError("Quick Clip could not be created because the replay buffer was not ready.");
    }

    internal static bool ValidateHotkeys(SettingsModel settings, out string? failure)
    {
        failure = null;

        var bindings = new (HotkeyAction Action, HotkeyBinding Binding)[]
        {
            (HotkeyAction.ToggleRecording, settings.Hotkeys.ToggleRecording),
            (HotkeyAction.ManualBookmark, settings.Hotkeys.ManualBookmark),
            (HotkeyAction.QuickClip, settings.Hotkeys.QuickClip),
        };

        var seen = new Dictionary<string, HotkeyAction>(StringComparer.Ordinal);
        foreach (var (action, binding) in bindings)
        {
            if (string.IsNullOrEmpty(binding.Key))
                continue;

            if (binding.Modifiers.Count(modifier => !string.IsNullOrWhiteSpace(modifier)) == 0)
            {
                failure = $"'{action}' needs at least one modifier key (Ctrl, Shift, Alt, or Win); " +
                    "a bare key would be captured globally for every application.";
                return false;
            }

            var normalized = string.Join('+', binding.Modifiers
                .Select(modifier => modifier.Trim())
                .Where(modifier => modifier.Length > 0)
                .Select(modifier => modifier.ToLowerInvariant())
                .OrderBy(modifier => modifier, StringComparer.Ordinal)) + '+' + binding.Key.Trim().ToLowerInvariant();

            if (seen.TryGetValue(normalized, out var existing))
            {
                failure = $"'{action}' and '{existing}' can't share the same hotkey.";
                return false;
            }

            seen[normalized] = action;
        }

        return true;
    }
}
