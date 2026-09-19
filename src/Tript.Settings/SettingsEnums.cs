// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Settings;

public enum RecordingMode
{
    Session,
    SessionWithReplayBuffer,

    Buffer = SessionWithReplayBuffer,
    Hybrid = 2,

    ReplayBufferOnly = 3,
}

public enum RateControlMode
{
    Cqp,

    Crf,

    Cbr,

    Vbr
}

public enum ContentType
{
    Recording,
    Clip,
    Highlight,
    Buffer
}

public enum StartupVisibility
{
    Window,
    Minimized,
    Tray
}

public enum MinimizeBehavior
{
    Taskbar,
    Tray
}

public enum CloseBehavior
{
    Exit,
    HideToTray
}

public enum StreamShareWhen
{
    WhileObsRuns,
    Always
}

public enum StorageFullAction
{
    PauseRecording,
    ReclaimOldest
}

public enum DisplayCaptureMethod
{
    Auto,
    Game,
    Display
}

public enum AudioOutputMode
{
    Normal,
    Mute,
    Disable
}

public enum AudioSourceKind
{
    Input,
    Output
}

public sealed class AudioTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public List<AudioSource> Sources { get; set; } = [];
}

public sealed class AudioSource
{
    public string Name { get; set; } = string.Empty;

    public AudioSourceKind Kind { get; set; }

    public float Volume { get; set; } = 1.0f;

    public string? DeviceId { get; set; }
}
