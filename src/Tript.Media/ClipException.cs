// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// The engine's failure vocabulary. A bad source path, a region out of bounds, a missing ffmpeg
// binary, a failed encode — all surface as one of these, never as a silent empty file.
public class ClipException : Exception
{
    public ClipException(string message) : base(message) { }
    public ClipException(string message, Exception inner) : base(message, inner) { }
}

// The source file itself was the problem: it does not exist, ffprobe could not read it, or the
// request names a region outside the recording.
public sealed class ClipSourceException : ClipException
{
    public ClipSourceException(string message) : base(message) { }
    public ClipSourceException(string message, Exception inner) : base(message, inner) { }
}

// ffmpeg was found but the encode itself failed. The message carries the trimmed stderr.
public sealed class ClipEncodeException : ClipException
{
    public ClipEncodeException(string message, int exitCode) : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
