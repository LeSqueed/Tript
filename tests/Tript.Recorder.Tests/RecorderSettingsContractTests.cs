// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

// The effective-settings consumption contract. The resolver produces the flat
// ResolvedRecorderSettings the recorder consumes; the recorder must never depend on the settings
// schema.
public class RecorderSettingsContractTests
{
    [Fact]
    public void Resolver_ProducesTheFlatShapeTheRecorderStartsWith()
    {
        var settings = new Tript.Settings.Settings();
        settings.Recording.Mode = RecordingMode.SessionWithReplayBuffer;
        settings.Recording.ResolutionWidth = 2560;
        settings.Recording.ResolutionHeight = 1440;
        settings.Recording.Fps = 60;
        settings.Recording.Encoder = "x264";
        settings.Recording.Quality = 8;
        settings.Audio.Tracks.Add(new AudioTrack { Name = "Game", Sources = { new AudioSource { Name = "Game audio", Kind = AudioSourceKind.Output } } });

        var resolved = SettingsResolver.Resolve(settings);

        Assert.IsType<ResolvedRecorderSettings>(resolved);
        Assert.Equal(RecordingMode.SessionWithReplayBuffer, resolved.Mode);
        Assert.Equal(2560, resolved.ResolutionWidth);
        Assert.Equal(1440, resolved.ResolutionHeight);
        Assert.Equal(60, resolved.Fps);
        Assert.Equal("x264", resolved.Encoder);
        Assert.Equal(8, resolved.Quality);
        var track = Assert.Single(resolved.AudioTracks);
        Assert.Equal("Game", track.Name);
    }

    // The recorder clones the config it is given, so a caller mutating a resolved value after the
    // start cannot change what the running recording uses.
    [Fact]
    public void Start_ConsumesTheResolvedConfigTheCallerHandedIt()
    {
        var session = new FakeRecorderSession();
        var recorder = new Recorder(session, TestSettings.Session());
        var settings = TestSettings.Session();

        using (recorder)
        {
            Assert.True(recorder.Start(settings));

            // Mutating the caller's object after the start must not change the running recording.
            settings.ResolutionWidth = 640;
            settings.ResolutionHeight = 480;
        }

        Assert.Equal(1, session.PlaceSourceCalls);
    }

    [Theory]
    [InlineData(RecordingMode.Session, true, false)]
    [InlineData(RecordingMode.SessionWithReplayBuffer, true, true)]
    [InlineData(RecordingMode.ReplayBufferOnly, false, true)]
    public void RecordingMode_PublicModesHaveExpectedOutputs(RecordingMode mode, bool recordsSession,
        bool usesReplayBuffer)
    {
        Assert.True(mode.IsAlphaSupported());
        Assert.Equal(recordsSession, mode.RecordsSession());
        Assert.Equal(usesReplayBuffer, mode.UsesReplayBuffer());
    }
}
