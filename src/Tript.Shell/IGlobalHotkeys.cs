// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Shell;

internal interface IGlobalHotkeys : IDisposable
{
    void ApplyBindings(IReadOnlyDictionary<HotkeyAction, HotkeyBinding?> effective);
}
