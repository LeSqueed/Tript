// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;
using Tript.Core;

namespace Tript.Settings;

public sealed class RecordingSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public RecordingMode Mode { get; set; } = RecordingMode.SessionWithReplayBuffer;

    public int ResolutionWidth { get; set; } = 1920;

    public int ResolutionHeight { get; set; } = 1080;

    public int Fps { get; set; } = 60;

    public string Encoder { get; set; } = "x264";

    public int Quality { get; set; } = 10;

    public RateControlMode RateControl { get; set; } = RateControlMode.Cqp;

    public int BitrateKbps { get; set; } = 15_000;

    public int MaxBitrateKbps { get; set; }

    public bool EnableHdr { get; set; } = true;

    public string? OutputDirectory { get; set; }

    public bool AutomaticClipsEnabled { get; set; }

    public int AutomaticClipBeforeSeconds { get; set; } = 5;

    public int AutomaticClipAfterSeconds { get; set; } = 8;

    public bool DeleteLinkedHighlightsByDefault { get; set; }

    public int TrashRetentionHours { get; set; } = 24;
}

public sealed class BufferSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public bool Enabled { get; set; }

    [JsonConverter(typeof(SecondsTimeSpanConverter))]
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(30);

    public long MaxSizeBytes { get; set; } = 4L * 1024 * 1024 * 1024;
}

public sealed class AudioSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public AudioOutputMode OutputMode { get; set; } = AudioOutputMode.Normal;

    public List<AudioTrack> Tracks { get; set; } = [];

    public List<AudioDeviceSetting> Devices { get; set; } = [];

    public AudioDeviceSetting? Mic { get; set; }

    public AudioDeviceSetting? Desktop { get; set; }
}

public sealed class AudioDeviceSetting
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public AudioSourceKind Direction { get; set; }
}

public sealed class CaptureSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public DisplayCaptureMethod Method { get; set; } = DisplayCaptureMethod.Auto;

    public string? Display { get; set; }

    public string? DisplayLabel { get; set; }
}

public sealed class GameSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    [JsonConverter(typeof(SecondsTimeSpanConverter))]
    public TimeSpan GameCaptureTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public List<GameSetting> GameList { get; set; } = [new() { Id = Ulid.Derive("builtin:Overwatch"), Name = "Overwatch" }];

    public bool AutoRecordDetectedGames { get; set; } = true;

    public List<string> IgnoredApplications { get; set; } = [];
}

public sealed class StreamingSettings
{
    public const string DefaultSenderName = "Tript";

    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public bool ShareEnabled { get; set; }

    public StreamShareWhen ShareWhen { get; set; } = StreamShareWhen.WhileObsRuns;

    public string SenderName { get; set; } = DefaultSenderName;
}

public sealed class GeneralSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public bool StartWithWindows { get; set; }

    public StartupVisibility StartupVisibility { get; set; } = StartupVisibility.Window;

    public MinimizeBehavior MinimizeBehavior { get; set; } = MinimizeBehavior.Taskbar;

    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Exit;

    public bool ConvertHdrClipsToSdr { get; set; }

    public bool CheckForUpdatesAutomatically { get; set; } = true;

    public NotificationSettings Notifications { get; set; } = new();
}

public sealed class NotificationSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public bool RecordingStarted { get; set; } = true;

    public bool RecordingStartedSound { get; set; } = true;

    public bool RecordingStopped { get; set; } = true;

    public bool RecordingStoppedSound { get; set; } = true;

    public bool Errors { get; set; } = true;

    public bool ErrorsSound { get; set; } = true;
}
