// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Media;

namespace Tript.App;

// A slow first ffmpeg start (cold cache, virus scan, a game running) must not disable clips and
// thumbnails until restart: success is kept, a failure is retried after RetryAfter.
internal sealed class LibraryTools
{
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly Func<(string Ffmpeg, string Ffprobe)> _locate;
    private readonly Func<long> _clock;
    private readonly Lock _gate = new();
    private (string Ffmpeg, string Ffprobe)? _tools;
    private string? _failure;
    private long _failedAt;

    internal LibraryTools(Func<(string Ffmpeg, string Ffprobe)>? locate = null, Func<long>? clock = null)
    {
        _locate = locate ?? (() => new FfmpegLocator().Locate());
        _clock = clock ?? (() => Environment.TickCount64);
    }

    internal string Failure
    {
        get
        {
            lock (_gate)
                return _failure ?? FfmpegLocator.NotFoundMessage("ffmpeg");
        }
    }

    internal (string Ffmpeg, string Ffprobe)? Value
    {
        get
        {
            lock (_gate)
            {
                if (_tools is not null)
                    return _tools;
                if (_failure is not null && _clock() - _failedAt < RetryAfter.TotalMilliseconds)
                    return null;

                try
                {
                    _tools = _locate();
                    Log.Information("AppHost: using ffmpeg at {Ffmpeg}", _tools.Value.Ffmpeg);
                    _failure = null;
                }
                catch (FfmpegNotFoundException exception)
                {
                    if (_failure is null)
                        Log.Warning("ffmpeg is unavailable, so clips, thumbnails and durations are too; retrying: {Reason}",
                            exception.Message);
                    else
                        Log.Debug("ffmpeg is still unavailable: {Reason}", exception.Message);
                    _failure = exception.Message;
                    _failedAt = _clock();
                }

                return _tools;
            }
        }
    }
}

internal sealed class KeptOnceAvailable<T>(Func<T?> create) where T : class
{
    private readonly Lock _gate = new();
    private T? _value;

    internal T? Value
    {
        get
        {
            if (Volatile.Read(ref _value) is { } value)
                return value;

            lock (_gate)
                return _value ??= create();
        }
    }
}
