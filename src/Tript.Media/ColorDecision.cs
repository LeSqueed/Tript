// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// The outcome of the clip extraction decision: three inputs — is the source HDR, can the target
// codec carry 10-bit, and (for multi-segment clips) do all segments share one transfer.
public enum ColorDecision
{
    // The source is HDR, the target codec can carry 10-bit, and every segment shares the same
    // transfer. The output keeps the HDR volume: 10-bit pixel format, colour space bt2020nc,
    // primaries bt2020, and the source's own transfer carried through. HEVC also needs main10.
    Preserve,

    // Everything else: the source is HDR but preservation is impossible, or segments disagree on
    // transfer. The output goes through the five-stage tone-map chain and is SDR BT.709.
    ToneMap
}

// What the pipeline will do with colour for a given clip, resolved before extraction so the ffmpeg
// invocation can be built once. "Do all segments share one transfer" is checked before extraction
// begins, across all segments.
public sealed class ColorPlan
{
    public required ColorDecision Decision { get; init; }

    // True when the clip is being preserved and the output must be tagged bt2020nc / bt2020 with the
    // source transfer carried through. When false and the output is SDR, it is tagged BT.709.
    public required bool PreservingHdr { get; init; }

    // The transfer to carry through when preserving (the source's own). Null when tone-mapping.
    public string? TransferToCarry { get; init; }

    // True when the tone-map chain is applied. Kept separate from Decision so an SDR output that
    // still needs an explicit yuv420p (mixed segments) is distinguishable.
    public required bool ToneMapping { get; init; }

    // True when the output must force an explicit pixel format — the tone-map chain's yuv420p, or
    // SDR-with-mixed-segments. VAAPI supplies its own format handling and skips this.
    public required bool ForcesPixelFormat { get; init; }

    // The encoder family the target codec maps to; decides which pixel-format and hardware-upload
    // rules apply. "vaapi" is the only family with special handling on the software path.
    public required string EncoderFamily { get; init; }

    public bool IsVaapi => EncoderFamily == "vaapi";
}
