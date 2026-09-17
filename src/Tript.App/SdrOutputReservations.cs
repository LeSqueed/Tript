// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

internal sealed class SdrOutputReservations
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _outputs = new(StringComparer.OrdinalIgnoreCase);

    internal bool TryReserve(string source, out string output)
    {
        lock (_gate)
        {
            if (!_sources.Add(source))
            {
                output = string.Empty;
                return false;
            }

            output = NextOutputPath(source);
            _outputs.Add(output);
            return true;
        }
    }

    internal void Release(string source, string output)
    {
        lock (_gate)
        {
            _sources.Remove(source);
            _outputs.Remove(output);
        }
    }

    private string NextOutputPath(string source)
    {
        var directory = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(source) + "-sdr";
        var candidate = Path.Combine(directory, stem + ".mp4");
        for (var suffix = 2; File.Exists(candidate) || _outputs.Contains(candidate); suffix++)
            candidate = Path.Combine(directory, $"{stem}-{suffix}.mp4");
        return candidate;
    }
}
