// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Serilog.Events;

namespace Tript.Detection;

internal sealed class DetectionLoopStats
{
    private const long IntervalMilliseconds = 60_000;

    private long _since = Environment.TickCount64;
    private int _frames;
    private int _withDetections;
    private TimeSpan _inference;
    private int _width;
    private int _height;

    public int Missed { get; set; }

    public int Dark { get; set; }

    public void Reset()
    {
        _since = Environment.TickCount64;
        _frames = 0;
        _withDetections = 0;
        _inference = TimeSpan.Zero;
        Missed = 0;
        Dark = 0;
    }

    public void Record(TimeSpan inference, bool hadDetections, int width, int height)
    {
        _frames++;
        _inference += inference;
        if (hadDetections)
            _withDetections++;
        _width = width;
        _height = height;
    }

    public void LogIfDue(string? gameId)
    {
        var elapsed = Environment.TickCount64 - _since;
        if (elapsed < IntervalMilliseconds)
            return;

        if (Log.IsEnabled(LogEventLevel.Debug))
        {
            Log.Debug("DetectionLoop: {GameId} in the last {Seconds:0}s: {Frames} frames at {Width}x{Height}, "
                + "{WithDetections} with detections, {Dark} dark, {Missed} missed, {AverageMs:0.0} ms average inference",
                gameId, elapsed / 1000.0, _frames, _width, _height, _withDetections, Dark, Missed,
                _frames == 0 ? 0 : _inference.TotalMilliseconds / _frames);
        }

        Reset();
    }
}
