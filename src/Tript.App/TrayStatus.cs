// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.App;

internal enum TrayActivity
{
    Idle,
    Detected,
    Buffering,
    Recording,
}

internal enum TrayAlert
{
    None,
    Warning,
    Error,
}

internal sealed record TrayStatus(TrayActivity Activity, TrayAlert Alert, string? GameName, string? AlertReason)
{
    internal static TrayStatus Idle { get; } = new(TrayActivity.Idle, TrayAlert.None, null, null);

    internal static TrayStatus From(
        bool recording,
        RecordingMode? activeMode,
        string? gameName,
        bool recordingBlocked,
        StoragePressure pressure,
        string? storageReason,
        StreamShareState shareState,
        bool shareEnabled)
    {
        var activity = recording
            ? activeMode == RecordingMode.ReplayBufferOnly ? TrayActivity.Buffering : TrayActivity.Recording
            : gameName is null ? TrayActivity.Idle : TrayActivity.Detected;

        if (recordingBlocked || pressure == StoragePressure.Critical)
            return new TrayStatus(activity, TrayAlert.Error, gameName, storageReason);

        if (pressure == StoragePressure.Warning)
            return new TrayStatus(activity, TrayAlert.Warning, gameName, storageReason);

        if (shareEnabled && shareState is StreamShareState.Failed or StreamShareState.Unsupported)
        {
            return new TrayStatus(activity, TrayAlert.Warning, gameName,
                shareState == StreamShareState.Unsupported
                    ? "Sharing to OBS is not supported on this PC."
                    : "Sharing to OBS failed.");
        }

        return new TrayStatus(activity, TrayAlert.None, gameName, null);
    }

    // NOTIFYICONDATA.szTip is a ByValTStr of 128, and marshalling a longer string throws.
    internal const int TooltipLimit = 127;

    internal string Tooltip()
    {
        var headline = Activity switch
        {
            TrayActivity.Recording => GameName is null ? "Tript - Recording" : $"Tript - Recording: {GameName}",
            TrayActivity.Buffering => GameName is null ? "Tript - Buffering" : $"Tript - Buffering: {GameName}",
            TrayActivity.Detected => GameName is null ? "Tript" : $"Tript - Ready: {GameName}",
            _ => "Tript",
        };

        headline = Clamp(headline, TooltipLimit);
        if (string.IsNullOrWhiteSpace(AlertReason))
            return headline;

        var room = TooltipLimit - headline.Length - 1;
        return room <= 0 ? headline : $"{headline}\n{Clamp(AlertReason, room)}";
    }

    private static string Clamp(string value, int limit) =>
        value.Length <= limit ? value : value[..Math.Max(0, limit - 1)] + "…";
}
