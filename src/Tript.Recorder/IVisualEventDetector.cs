// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;

namespace Tript.Recorder;

// The detector seam the detection host drives: start it for a game, stop it, and observe the
// detections it emits. Split out so the host's wiring can be unit-tested against a fake detector
// instead of a real one, which needs a frame source, an ONNX model and a background thread.
public interface IVisualEventDetector : IDisposable
{
    // Raised on the detector's own thread, one batch per detection cycle. Empty batches are
    // significant because the host uses every cycle to update each event's net count.
    event Action<List<DetectionResult>>? DetectionsAvailable;

    void Start(string gameId);

    void Stop();
}

// Adapts the detection pipeline's VisualEventDetector to the seam. The adapter exists so the host
// depends on the seam, not the concrete detector; Tript.Detection is untouched.
internal sealed class VisualEventDetectorAdapter : IVisualEventDetector
{
    private readonly VisualEventDetector _inner;

    internal VisualEventDetectorAdapter(VisualEventDetector inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public event Action<List<DetectionResult>>? DetectionsAvailable
    {
        add => _inner.DetectionsAvailable += value;
        remove => _inner.DetectionsAvailable -= value;
    }

    public void Start(string gameId) => _inner.Start(gameId);

    public void Stop() => _inner.Stop();

    public void Dispose() => _inner.Dispose();
}
