// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// Reserved for the calls that report failure without saying why — obs_startup returning false,
// obs_reset_audio returning false. Anything with a failure vocabulary returns its code instead;
// mapping a rich result onto a bare exception is how the reason gets lost.
public sealed class ObsException : Exception
{
    public ObsException(string message) : base(message)
    {
    }

    public ObsException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
