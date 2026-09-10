// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

public sealed class AudioRoutingPlan
{
    public required IReadOnlyList<PlannedAudioTrack> Tracks { get; init; }

    public int TrackCount => Tracks.Count;
}

public sealed class PlannedAudioTrack
{
    public required string Name { get; init; }

    public required IReadOnlyList<PlannedAudioSource> Sources { get; init; }

    public int MixerIndex { get; init; }

    public uint MixerMask => 1u << MixerIndex;
}

public sealed class PlannedAudioSource
{
    public required string Name { get; init; }

    public AudioSourceKind Kind { get; init; }

    public float Volume { get; init; }

    public string? DeviceId { get; init; }
}
