// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Models;

internal static class LegacyGameIds
{
    internal const string Overwatch = "5JWDDE307Z5127JK7KM4YCB1XW";

    // Ids minted before Overwatch went through the resolver, newest first: when folders for both
    // survive, the newer one has to be migrated first so its data wins over the stale one.
    internal static readonly IReadOnlyList<KeyValuePair<string, string>> NewestFirst =
    [
        new("57ZZVAZ0PJK8VQGPKB728QE57C", Overwatch),
        new("Overwatch", Overwatch),
    ];

    internal static readonly IReadOnlyDictionary<string, string> Map =
        NewestFirst.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
