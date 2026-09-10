// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;

namespace Tript.Recorder;

public interface IVisualEventDetector : IDisposable
{
    event Action<DetectionBatch>? DetectionsAvailable;

    void Start(string gameId);

    void Stop();
}

internal sealed class VisualEventDetectorAdapter : IVisualEventDetector
{
    private readonly VisualEventDetector _inner;

    internal VisualEventDetectorAdapter(VisualEventDetector inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public event Action<DetectionBatch>? DetectionsAvailable
    {
        add => _inner.DetectionsAvailable += value;
        remove => _inner.DetectionsAvailable -= value;
    }

    public void Start(string gameId) => _inner.Start(gameId);

    public void Stop() => _inner.Stop();

    public void Dispose() => _inner.Dispose();
}
