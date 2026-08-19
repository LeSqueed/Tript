// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Tests;

// Child-host tests must not bind production ports: a developer's running shell should never make
// the test suite fail, and the test process must not intercept the user's app traffic.
internal static class TestPorts
{
    internal const int Ui = 32282;
    internal const int Content = 32222;
    internal const int ControlSocket = 34030;
}
