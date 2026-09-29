// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog.Events;

namespace Tript.Detection;

internal sealed class RepeatedFailure
{
    private int _failing;

    public LogEventLevel NextLevel() =>
        Interlocked.Exchange(ref _failing, 1) == 0 ? LogEventLevel.Warning : LogEventLevel.Debug;

    public void Recovered() => Volatile.Write(ref _failing, 0);
}
