// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

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

    public IReadOnlyList<IAudioRoutedSource> Sources => _sources;

    public IReadOnlyList<IAudioTrackEncoder> Encoders => _encoders;

    public RecordingMetadata Metadata { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var source in _sources)
            _sink.DeactivateSource(source);

        foreach (var encoder in _encoders)
        {
            if (encoder is IDisposable disposable)
                disposable.Dispose();
        }

        foreach (var source in _sources)
        {
            if (source is IDisposable disposable)
                disposable.Dispose();
        }
    }
}
