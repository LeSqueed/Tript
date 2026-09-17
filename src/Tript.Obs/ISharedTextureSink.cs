// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

internal interface ISharedTextureSink
{
    void Publish(uint sharedHandle, uint width, uint height);

    bool TryBeginFrame();

    void EndFrame();

    void Withdraw();
}
