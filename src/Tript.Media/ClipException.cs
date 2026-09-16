// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public class ClipException : Exception
{
    public ClipException(string message) : base(message) { }
    public ClipException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ClipSourceException : ClipException
{
    public ClipSourceException(string message) : base(message) { }
    public ClipSourceException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ClipEncodeException : ClipException
{
    public ClipEncodeException(string message, int exitCode) : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
