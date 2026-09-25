// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;

namespace Tript.App.Tests;

internal static class TestHostThreadPool
{
    [ModuleInitializer]
    internal static void StartWithEnoughWorkersForBlockingTests()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), completionPorts);
    }
}
