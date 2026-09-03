// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// Extracts one still frame from a video file, for the library's thumbnail cards. The seam exists so
// the caching layer above can be tested without ffmpeg: a fake extractor counts calls, which is the
// only way to assert "a cache hit does not re-run ffmpeg" without reading mtimes.
public interface IThumbnailExtractor
{
    // Writes a single-frame image to destinationPath. Returns false when no usable image could be
    // produced — a missing or corrupt source, an ffmpeg that failed or overran its timeout. False is
    // a normal outcome, not an error: the library falls back to a placeholder card.
    bool TryExtract(string sourcePath, string destinationPath);
}

// The ffmpeg implementation. One process per extraction, bounded by a timeout, output verified on
// disk rather than trusted from the exit code.
public sealed class FfmpegThumbnailExtractor : IThumbnailExtractor
{
    // How far into the file the frame is taken when the duration is unknown. Frame 0 of a game
    // capture is usually useless: the capture source's first composited frames are commonly black
    // (game capture hooks before the first present, and libobs renders the scene's clear colour
    // until a frame arrives), and titles fade in over the first moments.
    private static readonly TimeSpan SeekOffset = TimeSpan.FromSeconds(1);

    // With a known duration the frame is taken this far into the file, never earlier than
    // SeekOffset: well past loading screens on a long recording, and still inside a short clip.
    private const double SeekFraction = 0.3;

    // The frame's width; the height follows the source's aspect ratio. 480 is a card in the
    // library grid at 2x, and keeps a JPEG in the tens of kilobytes. A source narrower than this is
    // scaled up rather than left alone — harmless, because every real capture is at least 1280 wide.
    private const int Width = 480;

    // A single-frame decode of a local file is tens of milliseconds. The cap is generous by three
    // orders of magnitude because its job is not to be tight, it is to guarantee that a wedged
    // ffmpeg cannot hold an HTTP request (and therefore a listener worker) open forever.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _ffmpegPath;
    private readonly string? _ffprobePath;
    private readonly Lazy<MediaProbe?> _probe;

    public FfmpegThumbnailExtractor(string ffmpegPath, string? ffprobePath = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _probe = new Lazy<MediaProbe?>(
            () => _ffprobePath is null ? null : new MediaProbe(_ffprobePath),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // At most two ffmpeg runs: the seek point chosen from the probed duration, then frame 0 as the
    // deterministic fallback. Frame 0 is accepted even when black, so an all-black recording still
    // gets a thumbnail rather than a blank card.
    public bool TryExtract(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
            return false;

        return TryExtractAt(sourcePath, destinationPath, ChosenOffset(sourcePath), rejectBlack: true)
            || TryExtractAt(sourcePath, destinationPath, TimeSpan.Zero, rejectBlack: false);
    }

    private TimeSpan ChosenOffset(string sourcePath)
    {
        try
        {
            var duration = _probe.Value?.Probe(sourcePath).DurationSeconds;
            if (duration is > 0 and var seconds && double.IsFinite(seconds))
                return TimeSpan.FromSeconds(Math.Max(SeekOffset.TotalSeconds, seconds * SeekFraction));
        }
        catch (Exception)
        {
            // A file still being written, or one ffprobe cannot read yet, gets the fixed offset.
        }

        return SeekOffset;
    }

    private bool TryExtractAt(string sourcePath, string destinationPath, TimeSpan offset, bool rejectBlack)
    {
        var arguments = new List<string>
        {
            // -nostdin: ffmpeg's interactive key handler reads stdin; a host process with an
            // inherited stdin can otherwise block it.
            "-nostdin",
            "-y",
            // blackframe reports the percentage of black pixels at info level. It is only a
            // conservative rejection signal; the JPEG remains the source of truth.
            "-loglevel", "info",
        };

        if (offset > TimeSpan.Zero)
        {
            // Input seeking (-ss before -i): ffmpeg seeks the container to the nearest keyframe and
            // decodes forward from there, instead of decoding the whole file and discarding frames.
            // The frame is then not exactly at the offset, which does not matter for a card.
            arguments.Add("-ss");
            arguments.Add(offset.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.AddRange([
            "-i", sourcePath,
            // The first video stream only: a recording carries several audio tracks, and the image
            // muxer must not be handed any of them.
            "-map", "0:v:0",
            "-frames:v", "1",
            "-an", "-sn", "-dn",
            // -2 keeps the aspect ratio and rounds the height to an even number, which the JPEG
            // encoder's chroma subsampling requires.
            "-vf", $"blackframe=amount=98:threshold=32,scale={Width}:-2",
            // -update: image2 writes an image sequence by default and warns that the file name
            // carries no "%03d" pattern; -update tells it this is deliberately one file.
            "-f", "image2", "-update", "1",
            "-c:v", "mjpeg",
            "-q:v", "4",
            destinationPath,
        ]);

        // No -pix_fmt: the mjpeg encoder already converts a limited-range recording to JPEG's full
        // range on its own. Measured on the same frame of a color_range=tv capture, with and
        // without an explicit -pix_fmt yuvj420p: byte-identical output, tagged yuvj420p/pc, and the
        // frame's average luma moved 32.9 -> 19.7, exactly the 16..235 -> 0..255 expansion.

        var outcome = FfmpegRunner.RunBounded(_ffmpegPath, arguments, Timeout);

        // The output file is the authority, not the exit code (see above). A zero-length file is
        // also a failure: it is what a killed ffmpeg leaves behind.
        if (outcome.Succeeded && HasContent(destinationPath)
            && (!rejectBlack || !outcome.StandardError.Contains("blackframe", StringComparison.OrdinalIgnoreCase)))
            return true;

        // A failed attempt must not leave a partial file where the caller might publish it.
        TryDelete(destinationPath);
        return false;
    }

    private static bool HasContent(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the caller treats the extraction as failed either way.
        }
    }
}
