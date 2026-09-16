// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public enum ColorDecision
{
    Preserve,

    ToneMap
}

public sealed class ColorPlan
{
    public required ColorDecision Decision { get; init; }

    public required bool PreservingHdr { get; init; }

    public string? TransferToCarry { get; init; }

    public required bool ToneMapping { get; init; }

    public required bool ForcesPixelFormat { get; init; }

    public required string EncoderFamily { get; init; }

    public bool IsVaapi => EncoderFamily == "vaapi";
}
