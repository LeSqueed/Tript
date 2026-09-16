// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public enum ObsSourceType
{
    Input = 0,
    Filter,
    Transition,
    Scene
}

[Flags]
public enum ObsSourceOutputFlags : uint
{
    None = 0,
    Video = 1 << 0,
    Audio = 1 << 1,

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

    RequiresCanvas = 1 << 17
}
