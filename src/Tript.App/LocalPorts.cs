// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

internal static class LocalPorts
{
    internal const int Ui = 8892;
    internal const int Content = 8893;
    internal const int ControlSocket = 8894;

    internal static readonly string[] UiOrigins =
        [$"http://localhost:{Ui}", $"http://127.0.0.1:{Ui}"];
}
