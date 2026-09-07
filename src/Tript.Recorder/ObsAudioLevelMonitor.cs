// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

internal readonly record struct AudioLevelSource(AudioSourceKind Kind, string DeviceId);

internal interface IAudioLevelProbe : IDisposable
{
    float Peak { get; }
}

internal sealed class ObsAudioLevelMonitor : IDisposable
{
    private static long _nextSourceId;
    private readonly object _gate = new();
    private readonly Func<AudioLevelSource, IAudioLevelProbe> _createProbe;
    private readonly Dictionary<AudioLevelSource, IAudioLevelProbe> _probes = [];
    private readonly Dictionary<AudioLevelSource, DateTime> _retryAfter = [];
    private bool _disposed;

    internal ObsAudioLevelMonitor(Func<AudioLevelSource, IAudioLevelProbe>? createProbe = null) =>
        _createProbe = createProbe ?? (source => new Probe(source));

    internal IReadOnlyDictionary<string, float> Read(IEnumerable<AudioLevelSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var desired = sources
                .Where(source => !string.IsNullOrWhiteSpace(source.DeviceId))
                .Distinct()
                .ToHashSet();

            foreach (var source in _probes.Keys.Where(source => !desired.Contains(source)).ToList())
            {
                _probes.Remove(source, out var probe);
                probe?.Dispose();
            }
            foreach (var source in _retryAfter.Keys.Where(source => !desired.Contains(source)).ToList())
                _retryAfter.Remove(source);

            var now = DateTime.UtcNow;
            foreach (var source in desired)
            {
                if (_probes.ContainsKey(source) ||
                    (_retryAfter.TryGetValue(source, out var retryAfter) && retryAfter > now))
                    continue;

                try
                {
                    _probes[source] = _createProbe(source);
                    _retryAfter.Remove(source);
                }
                catch (Exception)
                {
                    _retryAfter[source] = now.AddSeconds(5);
                }
            }

            var levels = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (source, probe) in _probes)
            {
                var peak = probe.Peak;
                if (!levels.TryGetValue(source.DeviceId, out var existing) || peak > existing)
                    levels[source.DeviceId] = peak;
            }
            return levels;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var probe in _probes.Values)
                probe.Dispose();
            _probes.Clear();
            _retryAfter.Clear();
        }
    }

    private sealed class Probe : IAudioLevelProbe
    {
        private readonly ObsSource _source;
        private readonly ObsVolumeMeter _meter;
        private bool _active;

        internal Probe(AudioLevelSource requested)
        {
            var sourceType = ObsAudioRoutingSink.DefaultSourceTypeId(requested.Kind);
            var sourceName = $"audio meter:{Interlocked.Increment(ref _nextSourceId)}";
            _source = ObsAudioRoutingSink.CreatePrivateCaptureSource(sourceType, sourceName,
                requested.DeviceId);
            try
            {
                _meter = new ObsVolumeMeter(_source);
                _source.MarkActive();
                _active = true;
            }
            catch
            {
                _source.Dispose();
                throw;
            }
        }

        public float Peak => _meter.Peak;

        public void Dispose()
        {
            if (_active)
            {
                _source.MarkInactive();
                _active = false;
            }
            _meter.Dispose();
            _source.Dispose();
        }
    }
}
