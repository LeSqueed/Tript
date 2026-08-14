// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// enum obs_source_type. One libobs type covers inputs, filters, transitions and scenes, and this is
// how an instance says which it is.
public enum ObsSourceType
{
    Input = 0,
    Filter,
    Transition,
    Scene
}

// The capability bits a source *type* declares, from obs_source_info.output_flags. Not to be
// confused with the two per-instance source flags: these describe what the type can do and are
// read-only.
[Flags]
public enum ObsSourceOutputFlags : uint
{
    None = 0,
    Video = 1 << 0,
    Audio = 1 << 1,

    // Never set on its own by a source that means "async video": the header pairs it with Video.
    Async = 1 << 2,
    AsyncVideo = Async | Video,
    CustomDraw = 1 << 3,
    Interaction = 1 << 5,
    Composite = 1 << 6,
    DoNotDuplicate = 1 << 7,
    Deprecated = 1 << 8,
    DoNotSelfMonitor = 1 << 9,
    CapDisabled = 1 << 10,
    MonitorByDefault = 1 << 11,
    Submix = 1 << 12,
    ControllableMedia = 1 << 13,
    Cea708 = 1 << 14,
    SRgb = 1 << 15,
    CapDontShowProperties = 1 << 16,

    // Added after 30.1.1. Reading it back from an older runtime simply never sees the bit set, which
    // is why the whole set is a flags enum rather than a switch over known values.
    RequiresCanvas = 1 << 17
}
