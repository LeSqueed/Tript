// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// There is one video pipeline in the process, and consumers of it are created deep inside features
// that have no path to pass a source down. The resolver is indirect rather than a settable instance
// so the host can hand out whichever source is live at the moment of the call — the pipeline is
// torn down and rebuilt across a settings change, and a captured instance would go stale silently.
public static class FrameSourceRegistry
{
    private static Func<IFrameSource?>? _resolver;

    // Throws rather than returning a do-nothing source: a detector wired up before the video
    // pipeline exists would otherwise sit consuming a stream that never delivers, and report
    // nothing wrong.
    public static IFrameSource Current =>
        Volatile.Read(ref _resolver)?.Invoke()
        ?? throw new InvalidOperationException(
            "No frame source is registered. Call FrameSourceRegistry.SetResolver during startup.");

    public static void SetResolver(Func<IFrameSource?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Volatile.Write(ref _resolver, resolver);
    }

    public static void Reset() => Volatile.Write(ref _resolver, null);
}
