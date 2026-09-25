// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using Tript.App.Content;

namespace Tript.App.Tests;

internal static class AmpleStorage
{
    internal const long FreeBytes = 1024L * 1024 * 1024 * 1024;

    internal static string FreeBytesText => FreeBytes.ToString(CultureInfo.InvariantCulture);

    internal static IStorageProbe Probe => new FixedStorageProbe(FreeBytes);
}
