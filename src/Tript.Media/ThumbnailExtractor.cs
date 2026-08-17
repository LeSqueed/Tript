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
    // How far into the file the frame is taken. Frame 0 of a game capture is usually useless: the
    // capture source's first composited frames are commonly black (game capture hooks before the
    // first present, and libobs renders the scene's clear colour until a frame arrives), and titles
    // fade in over the first moments. A second in is past that on every real capture and is still
    // inside the shortest recording a user can produce by pressing stop immediately.
    //
    // A fraction of the duration was the alternative and was rejected: it needs the duration, which
    // means an ffprobe per thumbnail — a second process on the request path to place a frame that
    // one second already places well.
    private static readonly TimeSpan SeekOffset = TimeSpan.FromSeconds(1);

    // The frame's width; the height follows the source's aspect ratio. 480 is a card in the
    // library grid at 2x, and keeps a JPEG in the tens of kilobytes. A source narrower than this is
    // scaled up rather than left alone — harmless, because every real capture is at least 1280 wide.
    private const int Width = 480;

    // A single-frame decode of a local file is tens of milliseconds. The cap is generous by three
    // orders of magnitude because its job is not to be tight, it is to guarantee that a wedged
    // ffmpeg cannot hold an HTTP request (and therefore a listener worker) open forever.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _ffmpegPath;

    public FfmpegThumbnailExtractor(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
    }

    public bool TryExtract(string sourcePath, string destinationPath)
    {
        // A recording shorter than the seek offset produces no image, and the retry at frame 0 is
        // what covers it — a two-second recording is a real thing a user can produce, and a
        // half-second one is possible if they stop instantly.
        //
        // Measured on ffmpeg 8.x (a 0.5s H.264 file, -ss 1): the seek lands past the end, the filter
        // graph passes no frame on, and the *exit code depends on the argument shape* — 234 for this
        // command ("Nothing was written into output file, because at least one of its streams
        // received no packets"), and 0 with an empty stderr if the pixel format is pinned instead.
        // What never varies is that no output file appears. So the file on disk is the only usable
        // signal; the exit code is checked but is never sufficient.
        if (TryExtractAt(sourcePath, destinationPath, SeekOffset))
            return true;

        return TryExtractAt(sourcePath, destinationPath, TimeSpan.Zero);
    }

    private bool TryExtractAt(string sourcePath, string destinationPath, TimeSpan offset)
    {
        var arguments = new List<string>
        {
            // -nostdin: ffmpeg's interactive key handler reads stdin; a host process with an
            // inherited stdin can otherwise block it.
            "-nostdin",
            "-y",
            "-loglevel", "error",
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
            "-vf", $"scale={Width}:-2",
            // -update: image2 writes an image sequence by default and warns that the file name
            // carries no "%03d" pattern; -update tells it this is deliberately one file.
            "-f", "image2", "-update", "1",
            "-c:v", "mjpeg",
            "-q:v", "4",
            destinationPath,
        ]);

        // No -pix_fmt: the mjpeg encoder already converts a limited-range recording to JPEG's full
        // range on its own. Measured on the same frame of a color_range=tv capture, with and without
        // an explicit -pix_fmt yuvj420p: byte-identical output, tagged yuvj420p/pc, and the frame's
        // average luma moved 32.9 -> 19.7, exactly the 16..235 -> 0..255 expansion. Pinning the
        // format only adds a deprecated-pixel-format warning.
        //
        // No tone-map either: a PQ or HLG source yields a flat, washed-out card. Tone-mapping costs a
        // filter chain the clip engine already carries for the encode path, and it is not worth it
        // for a 480px still.

        var outcome = FfmpegRunner.RunBounded(_ffmpegPath, arguments, Timeout);

        // The output file is the authority, not the exit code (see above). A zero-length file is
        // also a failure: it is what a killed ffmpeg leaves behind.
        if (outcome.Succeeded && HasContent(destinationPath))
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
