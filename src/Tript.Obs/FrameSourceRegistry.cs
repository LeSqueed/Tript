// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public static class FrameSourceRegistry
{
    private static Func<IFrameSource?>? _resolver;

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
