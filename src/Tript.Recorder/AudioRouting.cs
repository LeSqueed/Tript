// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// The wired audio path the routing service produced: the capture sources it created (routed into
// mixers, with per-source volume applied and marked active) and the audio encoders bound to each
// track's mixer and assigned to the output's slots. The recorder holds this for the life of the
// recording.
public sealed class AudioRouting : IDisposable
{
    private readonly IAudioRoutingSink _sink;

    private readonly IReadOnlyList<IAudioRoutedSource> _sources;

    private readonly IReadOnlyList<IAudioTrackEncoder> _encoders;

    private int _disposed;

    public AudioRouting(
        IAudioRoutingSink sink,
        IReadOnlyList<IAudioRoutedSource> sources,
        IReadOnlyList<IAudioTrackEncoder> encoders,
        RecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(encoders);
        ArgumentNullException.ThrowIfNull(metadata);

        _sink = sink;
        _sources = sources;
        _encoders = encoders;
        Metadata = metadata;
    }

    // The audio capture sources, in routing order.
    public IReadOnlyList<IAudioRoutedSource> Sources => _sources;

    // The audio encoders, one per track, in track order.
    public IReadOnlyList<IAudioTrackEncoder> Encoders => _encoders;

    // The track/source mapping the recording's metadata carries, written at wire time.
    public RecordingMetadata Metadata { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Balance every MarkActive at wire time. A source only produces audio while active, so
        // stopping the recording is exactly when the marks should come back off.
        foreach (var source in _sources)
            _sink.DeactivateSource(source);

        foreach (var encoder in _encoders)
        {
            if (encoder is IDisposable disposable)
                disposable.Dispose();
        }

        // The sources the sink created are this routing's to release, and they are the population
        // that otherwise sits waiting for a finalizer while the OBS context is torn down.
        foreach (var source in _sources)
        {
            if (source is IDisposable disposable)
                disposable.Dispose();
        }
    }
}
