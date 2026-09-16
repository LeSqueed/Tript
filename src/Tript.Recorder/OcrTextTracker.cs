// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;

namespace Tript.Recorder;

internal sealed class OcrTextTracker
{
    private const float VetoConfidenceFloor = 0.8f;
    private const int VetoMemoryMilliseconds = 1000;
    private const double DefaultMaximumTextDistance = 0.2;
    private const float DefaultMinimumBoundsIou = 0.3f;
    private const int DefaultExpiryMilliseconds = 350;

    private readonly IReadOnlyList<EventDefinition> _definitions;
    private readonly Dictionary<int, List<TextTrack>> _tracks = new();

    internal OcrTextTracker(IEnumerable<EventDefinition> definitions)
    {
        _definitions = definitions.Where(definition => definition.DetectionKind == DetectionKind.Ocr).ToList();
    }

    internal List<EventDefinition> Update(IReadOnlyList<OcrMatch> matches, DateTime now)
    {
        var active = new List<EventDefinition>();
        foreach (var definition in _definitions)
        {
            if (!_tracks.TryGetValue(definition.Id, out var tracks))
            {
                tracks = [];
                _tracks[definition.Id] = tracks;
            }

            Track(definition, tracks, matches, now);

            foreach (var track in tracks)
            {
                if (track.Confirmed || definition.Type != EventType.Trigger)
                    active.Add(definition);
            }
        }

        return active;
    }

    private static void Track(EventDefinition definition, List<TextTrack> tracks, IReadOnlyList<OcrMatch> matches,
        DateTime now)
    {
        var tracking = definition.Ocr?.Tracking ?? new OcrTrackingDefinition();
        var maximumTextDistance = definition.Ocr?.Tracking.MaximumTextDistance ?? DefaultMaximumTextDistance;
        var minimumBoundsIou = definition.Ocr?.Tracking.MinimumBoundsIou ?? DefaultMinimumBoundsIou;

        foreach (var track in tracks)
            track.SeenThisBatch = false;

        var observations = matches
            .Where(match => match.EventId == definition.Id)
            .Select(match => (Match: match, Text: Normalized(match)))
            .DistinctBy(entry => (entry.Match.SegmentId, entry.Text, entry.Match.X, entry.Match.Y,
                entry.Match.Width, entry.Match.Height));
        foreach (var (match, normalized) in observations)
        {
            if (normalized.Length == 0)
                continue;
            if (definition.Type != EventType.Trigger && match.Confidence < VetoConfidenceFloor)
                continue;

            var track = ClosestTrack(tracks, match, normalized, maximumTextDistance, minimumBoundsIou);
            if (track is null)
            {
                track = new TextTrack { FirstSeen = now };
                tracks.Add(track);
            }
            else if (track.ConsecutiveMatches == 0)
            {
                track.FirstSeen = now;
            }

            track.Text = normalized;
            track.LastSeen = now;
            track.X = match.X;
            track.Y = match.Y;
            track.Width = match.Width;
            track.Height = match.Height;
            track.SeenThisBatch = true;
            track.ConsecutiveMatches++;
            if (!track.Confirmed && (track.ConsecutiveMatches >= tracking.ConfirmationFrames
                || now - track.FirstSeen >= TimeSpan.FromMilliseconds(tracking.MinimumStableMilliseconds)))
            {
                track.Confirmed = true;
            }
        }

        foreach (var track in tracks)
        {
            if (!track.SeenThisBatch)
                track.ConsecutiveMatches = 0;
        }

        var expiryMs = definition.Ocr?.Tracking.ExpireAfterMissingMilliseconds ?? DefaultExpiryMilliseconds;
        if (definition.Type != EventType.Trigger)
            expiryMs = Math.Max(expiryMs, VetoMemoryMilliseconds);
        var expiry = TimeSpan.FromMilliseconds(expiryMs);
        tracks.RemoveAll(track => now - track.LastSeen > expiry);
    }

    private static string Normalized(OcrMatch match) =>
        string.IsNullOrWhiteSpace(match.NormalizedText) ? OcrTextNormalizer.Normalize(match.Text) : match.NormalizedText;

    private static TextTrack? ClosestTrack(List<TextTrack> tracks, OcrMatch match, string normalized,
        double maximumTextDistance, float minimumBoundsIou)
    {
        TextTrack? best = null;
        var bestAffinity = double.NegativeInfinity;
        foreach (var candidate in tracks)
        {
            if (candidate.SeenThisBatch || TextDistance(candidate.Text, normalized) > maximumTextDistance)
                continue;

            var affinity = BoundsAffinity(candidate, match, minimumBoundsIou);
            if (affinity > bestAffinity)
            {
                best = candidate;
                bestAffinity = affinity;
            }
        }

        return bestAffinity >= 0 ? best : null;
    }

    internal static double BoundsAffinity(TextTrack track, OcrMatch match, float minimumIou)
    {
        var left = Math.Max(track.X, match.X);
        var top = Math.Max(track.Y, match.Y);
        var right = Math.Min(track.X + track.Width, match.X + match.Width);
        var bottom = Math.Min(track.Y + track.Height, match.Y + match.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = track.Width * track.Height + match.Width * match.Height - intersection;
        if (union > 0 && intersection / union >= minimumIou) return intersection / union;

        var centerDistance = Math.Abs(track.X + track.Width / 2 - match.X - match.Width / 2)
            + Math.Abs(track.Y + track.Height / 2 - match.Y - match.Height / 2);
        return centerDistance <= Math.Max(track.Height, match.Height) * 1.5 ? 0 : -1;
    }

    internal static double TextDistance(string left, string right)
    {
        if (left == right) return 0;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
            }
            (previous, current) = (current, previous);
        }
        return (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }

    internal sealed class TextTrack
    {
        internal string Text { get; set; } = string.Empty;
        internal DateTime FirstSeen { get; set; }
        internal DateTime LastSeen { get; set; }
        internal int ConsecutiveMatches { get; set; }
        internal bool Confirmed { get; set; }
        internal bool SeenThisBatch { get; set; }
        internal float X { get; set; }
        internal float Y { get; set; }
        internal float Width { get; set; }
        internal float Height { get; set; }
    }
}
