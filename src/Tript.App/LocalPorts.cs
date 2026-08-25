// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

// The three loopback ports, in one place. They were literals in half a dozen files, and the control
// socket's Origin allowlist hard-coded the UI host's port as a string while UiHost owned it as a
// private const — so changing one locked the app out of its own socket, with a 403 at the handshake
// and a UI that reconnects forever.
//
// The frontend has its own copies in src/Tript.Web/src/ipc/endpoints.ts. Nothing can share a constant
// across that boundary; a test pins the two together instead.
internal static class LocalPorts
{
    internal const int Ui = 8892;
    internal const int Content = 8893;
    internal const int ControlSocket = 8894;

    // What a browser presents as the Origin of a page served by the UI host. Both loopback spellings,
    // because which one appears depends on how the user reached the UI.
    internal static readonly string[] UiOrigins =
        [$"http://localhost:{Ui}", $"http://127.0.0.1:{Ui}"];
}
