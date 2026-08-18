// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// Turns the times a client asks for into regions the engine can actually cut, or drops them. This is
// the backend's own gate on region bounds, deliberately independent of the frontend's.
//
// Why the backend gates at all: the frontend clamps the timeline selection to the media's duration,
// but that clamping is a UX affordance — it keeps the handles inside the scrubber — not a guarantee
// about what arrives here. The control socket is a trust boundary even though it is local: anything
// that can open a WebSocket to it can send CreateClip, and so can a stale frontend build, a future
// one, or a replayed message. The frontend recently shipped exactly that bug — a recording whose
// metadata carried no endTime fell back to a 120 s placeholder duration, and one path was unbounded
// outright — so regions far past the real end of the file are a shape this surface has already seen.
//
// What ffmpeg does with a bad region, measured on ffmpeg 8.0 with the engine's own argument shape
// (-ss <start> -t <duration> -i <source>, one input per region):
//
//   * A region wholly past the end (-ss 5 -t 3 on a 2 s file): exit code 0, a 261-byte MP4 with no
//     video stream and no reported duration, and nothing on stderr. Silent empty output.
//   * A region straddling the end (-ss 1.5 -t 3 on a 2 s file): exit code 0 and a correct 0.5 s
//     clip. ffmpeg truncates at the real end by itself, so clamping End to the duration matches what
//     ffmpeg would have produced anyway — the value of clamping here is that the request succeeds
//     instead of being refused.
//   * A negative start (-ss -1 -t 3 on a 2 s file): exit code 0 and the whole 2 s file. The seek is
//     clamped to zero but -t is not adjusted, so the clip is longer than the region asked for.
//   * A zero-length region (-t 0): exit code 0 and THE ENTIRE REMAINING FILE. ffmpeg reads -t 0 as
//     "no limit", not as "no output", and the engine's own FormatSeconds ("0.#######") prints any
//     duration below 5e-8 s as "0" — so a degenerate region does not produce a small clip, it
//     produces the full recording.
//   * In combine mode a bad region among good ones is dropped from the concat silently: one 1 s
//     region plus one wholly-past-the-end region gave exit 0 and a 1 s file.
//
// Every one of those is exit code 0 with an empty stderr, so nothing downstream of ffmpeg can tell a
// bad region from a good one. That is why the bound has to be enforced before the process starts.
public static class ClipRegionBounds
{
    // The shortest region worth cutting. 40 ms is one frame at 25 fps, so it is below one frame for
    // every rate the recorder produces, and far below anything a user can deliberately draw on a
    // timeline. Regions shorter than this are dropped rather than cut: the measurements above show a
    // sub-frame region does not yield a short clip but either an empty file or (at -t 0) the whole
    // recording, and a region this short is a client or rounding artefact in every case seen — most
    // often what is left of a region that straddled the end of the file.
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(40);

    // The largest number of seconds TimeSpan can hold. TimeSpan.MaxValue.TotalSeconds is
    // 922337203685.4775 (measured); the check is against the floor of that so no rounding in the
    // conversion can land past the end.
    private const double MaxRepresentableSeconds = 922_337_203_685.0;

    // Converts a client's start/end seconds into a region, or reports that they name no interval at
    // all. This is the double -> TimeSpan boundary, and it is the only place non-finite input can
    // be caught: TimeSpan.FromSeconds(double.NaN) throws ArgumentException and
    // TimeSpan.FromSeconds(double.PositiveInfinity) throws OverflowException, so a raw conversion
    // of wire values throws rather than refusing (measured — and note 1e18, an unremarkable JSON
    // number, overflows too, which is why the bound is on magnitude and not only on finiteness).
    public static bool TryFromSeconds(double start, double end, out ClipRegion region)
    {
        region = default;

        if (!double.IsFinite(start) || !double.IsFinite(end))
            return false;
        if (Math.Abs(start) > MaxRepresentableSeconds || Math.Abs(end) > MaxRepresentableSeconds)
            return false;

        var (low, high) = start <= end ? (start, end) : (end, start);
        region = new ClipRegion(TimeSpan.FromSeconds(low), TimeSpan.FromSeconds(high));
        return true;
    }

    // Fits one region inside [0, sourceDuration], or reports that nothing usable is left of it.
    // Both endpoints are clamped, which is what turns "wholly past the end" into a zero-length
    // region and therefore into a drop: a 5 s - 8 s region on a 2 s file clamps to 2 s - 2 s and
    // fails MinimumDuration.
    public static bool TryClamp(ClipRegion region, double sourceDurationSeconds, out ClipRegion clamped)
    {
        var (start, end) = region.Start <= region.End
            ? (region.Start, region.End)
            : (region.End, region.Start);

        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        if (end < TimeSpan.Zero)
            end = TimeSpan.Zero;

        if (double.IsFinite(sourceDurationSeconds)
            && sourceDurationSeconds > 0
            && sourceDurationSeconds <= MaxRepresentableSeconds)
        {
            var limit = TimeSpan.FromSeconds(sourceDurationSeconds);
            if (start > limit)
                start = limit;
            if (end > limit)
                end = limit;
        }

        clamped = new ClipRegion(start, end);
        return clamped.Duration >= MinimumDuration;
    }

    // Clamps a whole request's regions, keeping timeline order and dropping whatever does not
    // survive. An empty result is the caller's signal to fail the request instead of running
    // ffmpeg — every measured failure mode above is exit code 0, so "run it and see" reports
    // success on an empty file.
    public static IReadOnlyList<ClipRegion> ClampAll(IReadOnlyList<ClipRegion> regions,
        double sourceDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(regions);

        var kept = new List<ClipRegion>(regions.Count);
        foreach (var region in regions)
        {
            if (TryClamp(region, sourceDurationSeconds, out var clamped))
                kept.Add(clamped);
        }

        return kept;
    }
}
