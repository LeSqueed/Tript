// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Content;

internal static class SharingViolationRetry
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000),
    ];

    internal static bool IsSharingViolation(IOException exception) =>
        exception.HResult is ErrorSharingViolation or ErrorLockViolation;

    internal static void Run(Action action) => Run(action, DefaultDelays);

    internal static void Run(Action action, IReadOnlyList<TimeSpan> delays)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception) && attempt < delays.Count)
            {
                Thread.Sleep(delays[attempt]);
            }
        }
    }
}
