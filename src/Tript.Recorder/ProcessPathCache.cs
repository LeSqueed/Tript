// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Recorder;

internal sealed class ProcessPathCache
{
    private readonly Lock _gate = new();
    private int _processId;
    private DateTimeOffset _startTime;
    private string? _path;

    internal string? Resolve(int processId, DateTimeOffset startTime, Func<string?> readPath)
    {
        ArgumentNullException.ThrowIfNull(readPath);

        lock (_gate)
        {
            if (_path is not null && _processId == processId && _startTime == startTime)
                return _path;
        }

        var path = readPath();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        lock (_gate)
        {
            _processId = processId;
            _startTime = startTime;
            _path = path;
        }

        return path;
    }
}
