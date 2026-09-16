// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Media;

namespace Tript.App.Content;

internal sealed class LibraryProbe
{
    internal const int ProbesPerListing = 12;

    private readonly Lazy<MediaProbe?> _probe;
    private readonly HashSet<string> _unprobeable = new(StringComparer.Ordinal);

    internal LibraryProbe(Func<string?> locateFfprobe)
    {
        _probe = new Lazy<MediaProbe?>(
            () => locateFfprobe() is { } ffprobe ? new MediaProbe(ffprobe) : null,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal MediaProbe? Probe => _probe.Value;

    internal bool CanProbe(string absolutePath)
    {
        if (Probe is null)
            return false;

        lock (_unprobeable)
            return !_unprobeable.Contains(absolutePath);
    }

    internal double? ReadDuration(string absolutePath, string wirePath)
    {
        double seconds;
        try
        {
            seconds = Probe!.Probe(absolutePath).DurationSeconds;
        }
        catch (Exception exception)
        {
            Log.Warning("could not read the duration of {Path}: {Reason}", wirePath, exception.Message);
            MarkUnprobeable(absolutePath);
            return null;
        }

        if (double.IsFinite(seconds) && seconds > 0)
            return seconds;

        MarkUnprobeable(absolutePath);
        return null;
    }

    internal bool? ReadHdr(string absolutePath, string wirePath)
    {
        try
        {
            return Probe!.Probe(absolutePath).IsHdr;
        }
        catch (Exception exception)
        {
            Log.Warning("could not read HDR metadata of {Path}: {Reason}", wirePath, exception.Message);
            MarkUnprobeable(absolutePath);
            return null;
        }
    }

    private void MarkUnprobeable(string absolutePath)
    {
        lock (_unprobeable)
            _unprobeable.Add(absolutePath);
    }
}
