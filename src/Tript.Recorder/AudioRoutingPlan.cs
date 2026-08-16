// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// The pure, libobs-free account of what the audio routing must produce. A track in the settings
// model (spec/recorder.md, "Multi-track audio") is a destination in the output file, not a device;
// the plan maps each track onto the three joints the binding proved (spec/obs-binding.md, "Audio
// routing and tracks"):
//
//   1. Source to mixer — each source on a track feeds that track's mixer bit.
//   2. Mixer to audio encoder — one encoder per track, bound to that mixer at creation.
//   3. Encoder to output slot — each encoder assigned to the output slot matching the track.
//
// Track n in the resulting file is output slot n, which draws mixer n. Two sources merged into one
// track share the same mixer bit; their volumes stay per-source.
//
// The plan is a pure value type so the mapping is unit-testable without a live libobs context; the
// AudioRoutingService is what applies it to the binding.

public sealed class AudioRoutingPlan
{
    public required IReadOnlyList<PlannedAudioTrack> Tracks { get; init; }

    public int TrackCount => Tracks.Count;
}

// One track in the plan. MixerIndex and OutputSlot are the same value by construction — the
// track's ordinal — kept as two properties because they name two different joints of the wiring
// and a reader should not have to remember why they coincide.
public sealed class PlannedAudioTrack
{
    public required string Name { get; init; }

    public required IReadOnlyList<PlannedAudioSource> Sources { get; init; }

    // Which of libobs's audio mixers this track draws from; also the encoder's mixer index and the
    // output slot. Zero-based, bounded by MaxAudioTracks.
    public int MixerIndex { get; init; }

    // The single mixer bit every source on this track is routed to: bit n means mixer n.
    public uint MixerMask => 1u << MixerIndex;
}

// One source routed into a track, with the volume it is recorded at. Volume is per-source, not
// per-track: two merged sources keep their own gains.
public sealed class PlannedAudioSource
{
    public required string Name { get; init; }

    public AudioSourceKind Kind { get; init; }

    public float Volume { get; init; }
}
