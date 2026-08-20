// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// The enumeration surface that appears in persisted settings and in recording metadata. Member
// names and ordering are a compatibility surface — they were for the settings Tript reimplements,
// and they are for ours: renaming or reordering a member quietly breaks a file written by an
// earlier version.
namespace Tript.Settings;

// The three recording modes. Hybrid (both at once) is the default globally and per game, and is
// designed for but deferred in alpha: the mode is a first-class model from the start so the
// two-output shape slots in without a refactor.
public enum RecordingMode
{
    Session,
    Buffer,
    Hybrid
}

// How the video encoder is told to spend its bits. The four members are the modes the encoder
// families actually accept: CRF is x264's constant-quality mode and
// exists nowhere else, CQP is the hardware families' constant-quantiser mode, and CBR/VBR are the
// rate-targeted modes every documented family accepts.
public enum RateControlMode
{
    // Constant quantiser, the hardware families' spelling of constant quality.
    Cqp,

    // Constant quality, x264's spelling of the same idea. The quality profile picks the quantiser.
    Crf,

    // Constant bitrate: the encoder holds the configured kbps whatever the picture costs.
    Cbr,

    // Variable bitrate: the configured kbps is the target, with a ceiling where the family has a
    // key for one.
    Vbr
}

// What a recording, clip, highlight or buffer entry is — the content-type discriminator carried
// by each content record. Highlight is load-only for Tript: nothing produces it, and whether it
// is still recognised on load is a separate decision.
public enum ContentType
{
    Recording,
    Clip,
    Highlight,
    Buffer
}

// What the app shows when it starts. The windows exist because capture configuration is per
// window; whether each member survives is a frontend question, not a model one.
public enum StartupWindowMode
{
    Library,
    Settings,
    Record
}

// What closing the window does while something is being recorded. Quit drops the in-flight
// recording on the floor; keep-it-recording lets it continue (an orphaned file the recovery
// prompt then offers to recover on next start).
public enum CloseButtonAction
{
    Quit,
    KeepRecording
}

// Which capture path supplies the picture.
public enum DisplayCaptureMethod
{
    Auto,
    Game,
    Display
}

// How the app's own audio output is handled while recording. Game audio is a distinct source
// type in the multi-track model (an output source), not the system output.
public enum AudioOutputMode
{
    Normal,
    Mute,
    Disable
}

// Where a captured audio source comes from. Inputs are mics and other capture devices; outputs
// are speakers, system playback and game audio. Both are routable into any track.
public enum AudioSourceKind
{
    Input,
    Output
}

// The audio page's routing surface: how many tracks a recording has, and which sources (each
// with its own volume) are merged into each. A track is a destination in the output file, not a
// device; one track may carry several merged sources.
public sealed class AudioTrack
{
    // A stable identifier so a track survives reordering in the settings UI.
    //
    // It is NOT persisted per recording: RecordingMetadata's AudioTrackLayout carries Index, Name
    // and Sources, so nothing downstream can resolve this Guid against a file that already exists.
    // Anything keyed to a recording's tracks — the clip engine's adjustments, the library's
    // ContentItem.AudioTracks — uses the track's position in the file instead, which every
    // recording already has. Persist the Id here too before relying on it outside settings.
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public List<AudioSource> Sources { get; set; } = [];
}

public sealed class AudioSource
{
    public string Name { get; set; } = string.Empty;

    public AudioSourceKind Kind { get; set; }

    public float Volume { get; set; } = 1.0f;

    // The WASAPI device id this source captures, or null for the platform default device. The
    // selection is persisted by id (serialized as deviceId) so it survives a settings round trip
    // and stays selected even while the device is absent; the recorder's sink turns the id into
    // the win-wasapi capture source's "device_id" setting.
    public string? DeviceId { get; set; }
}
