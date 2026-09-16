// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public sealed class ObsException : Exception
{
    public ObsException(string message) : base(message)
    {
    }

    public ObsException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
