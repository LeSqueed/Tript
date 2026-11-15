// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

// libobs rebuilds a removed D3D11 device only when a swap-chain Present fails, and Tript has no swap
// chain, so after a driver reset this logged line is the only sign the process can no longer record.
internal sealed class GraphicsDeviceMonitor
{
    internal static GraphicsDeviceMonitor Shared { get; } = new();

    private const string DeviceRemovedMarker = "Device Removed Reason";

    private int _lost;

    internal event Action? Lost;

    internal bool IsLost => Volatile.Read(ref _lost) != 0;

    internal static bool IsDeviceLossMessage(string message) =>
        message.Contains(DeviceRemovedMarker, StringComparison.Ordinal);

    // Runs on libobs threads, including the graphics thread: handlers must hand work off, not block.
    internal void Observe(string message)
    {
        if (!IsDeviceLossMessage(message) || Interlocked.Exchange(ref _lost, 1) != 0)
            return;

        Lost?.Invoke();
    }
}
