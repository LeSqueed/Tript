// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

internal readonly record struct ThrottleDecision(bool Write, long SuppressedBefore, bool SuppressionStarts);

internal sealed class RepeatedLogThrottle
{
    internal const int AllowedPerWindow = 5;
    internal const int MaxTrackedMessages = 256;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Func<long> _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal RepeatedLogThrottle(Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
    }

    internal ThrottleDecision Check(string message)
    {
        var now = _clock();
        lock (_gate)
        {
            if (!_entries.TryGetValue(message, out var entry))
            {
                if (_entries.Count >= MaxTrackedMessages && !EvictExpired(now))
                    return new ThrottleDecision(true, 0, false);

                _entries[message] = new Entry(now, 1, 0);
                return new ThrottleDecision(true, 0, false);
            }

            if (now - entry.WindowStart >= Window.TotalMilliseconds)
            {
                _entries[message] = new Entry(now, 1, 0);
                return new ThrottleDecision(true, entry.Suppressed, false);
            }

            var count = entry.Count + 1;
            if (count <= AllowedPerWindow)
            {
                _entries[message] = entry with { Count = count };
                return new ThrottleDecision(true, 0, count == AllowedPerWindow);
            }

            _entries[message] = entry with { Count = count, Suppressed = entry.Suppressed + 1 };
            return new ThrottleDecision(false, 0, false);
        }
    }

    private bool EvictExpired(long now)
    {
        var expired = _entries
            .Where(pair => now - pair.Value.WindowStart >= Window.TotalMilliseconds)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in expired)
            _entries.Remove(key);
        return expired.Count > 0;
    }

    private readonly record struct Entry(long WindowStart, int Count, long Suppressed);
}
