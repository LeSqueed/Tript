// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class AudioLevelFeedTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsStore _store;
    private readonly ManualClock _clock = new();
    private readonly List<IReadOnlyDictionary<string, float>> _published = [];
    private readonly Dictionary<string, FakeProbe> _probes = [];

    public AudioLevelFeedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-audio-feed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new SettingsStore(new SettingsFileProvider(Path.Combine(_root, "settings.json")));
        _store.Load().Audio.Tracks =
        [
            new AudioTrack
            {
                Sources =
                [
                    new AudioSource { Kind = AudioSourceKind.Input, DeviceId = "mic" },
                    new AudioSource { Kind = AudioSourceKind.Output, DeviceId = null },
                ],
            },
        ];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Levels_AreNotWantedUntilWatched_AndTheLeaseExpires()
    {
        using var feed = CreateFeed();

        Assert.False(feed.Wanted);
        feed.Watch();
        Assert.True(feed.Wanted);

        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.False(feed.Wanted);
    }

    [Fact]
    public void PublishLevels_WhenNobodyWatches_PublishesNothingAndReleasesProbes()
    {
        using var feed = CreateFeed();
        feed.Watch();
        feed.PublishLevels();
        Assert.False(_probes["mic"].Disposed);

        _clock.Advance(TimeSpan.FromSeconds(6));
        feed.PublishLevels();

        Assert.Single(_published);
        Assert.True(_probes["mic"].Disposed);
    }

    [Fact]
    public void PublishLevels_ReadsOnlyConfiguredDevices()
    {
        using var feed = CreateFeed();
        feed.Watch();

        feed.PublishLevels();

        var levels = Assert.Single(_published);
        Assert.Equal(0.5f, Assert.Single(levels).Value);
        Assert.Equal(new[] { "mic" }, _probes.Keys);
    }

    [Fact]
    public void PublishLevels_WhenReadingFails_KeepsRunning()
    {
        using var feed = CreateFeed();
        feed.Watch();
        feed.PublishLevels();
        _probes["mic"].Fail = true;

        feed.PublishLevels();
        feed.PublishLevels();

        Assert.Single(_published);
    }

    [Fact]
    public void RefreshDevices_ReportsOnlyRealChanges()
    {
        var devices = new List<AudioDeviceSetting>();
        var changes = 0;
        using var feed = new AudioLevelFeed(_store, monitor: null,
            new AudioDeviceInventory(_ => (true, devices.ToList())), _published.Add, () => changes++, _clock);

        feed.RefreshDevices();
        Assert.Equal(0, changes);

        devices.Add(new AudioDeviceSetting { Id = "mic", Name = "Mic", Direction = AudioSourceKind.Input });
        feed.RefreshDevices();
        feed.RefreshDevices();

        Assert.Equal(1, changes);
        Assert.Contains(feed.Devices, device => device.Id == "mic");
    }

    [Fact]
    public void AfterDispose_NothingIsPublished()
    {
        var feed = CreateFeed();
        feed.Watch();
        feed.Dispose();

        feed.PublishLevels();
        feed.RefreshDevices();

        Assert.Empty(_published);
    }

    private AudioLevelFeed CreateFeed() =>
        new(_store, new ObsAudioLevelMonitor(source =>
            {
                var probe = new FakeProbe();
                _probes[source.DeviceId] = probe;
                return probe;
            }),
            new AudioDeviceInventory(_ => (true, [])), _published.Add, () => { }, _clock);

    private sealed class FakeProbe : IAudioLevelProbe
    {
        public bool Fail { get; set; }

        public bool Disposed { get; private set; }

        public float Peak => Fail ? throw new InvalidOperationException("meter gone") : 0.5f;

        public void Dispose() => Disposed = true;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
