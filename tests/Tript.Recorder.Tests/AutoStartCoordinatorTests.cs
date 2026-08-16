// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

// The auto-start contract: a game detected starts a recording, the game going away stops it, and no
// game means no start. The coordinator is tested against a fake detector and a recorder over a fake
// session, so the seam is what is under test, not libobs.
public class AutoStartCoordinatorTests
{
    private readonly FakeRecorderSession _session = new();
    private readonly FakeGameDetector _detector = new();

    private readonly Dictionary<string, ResolvedRecorderSettings> _byGame = new();
    private readonly List<string> _resolvedGames = [];

    private Recorder _recorder = null!;
    private AutoStartCoordinator _coordinator = null!;

    private void Arrange(string game = "Overwatch")
    {
        _recorder = new Recorder(_session, TestSettings.Session());
        _coordinator = new AutoStartCoordinator(
            _recorder,
            _detector,
            name =>
            {
                _resolvedGames.Add(name);
                return _byGame.TryGetValue(name, out var settings) ? settings : TestSettings.Session();
            });

        _byGame[game] = TestSettings.Session();
    }

    [Fact]
    public void Start_SubscribesAndStartsTheDetector()
    {
        Arrange();

        _coordinator.Start();

        Assert.True(_detector.Started);
    }

    [Fact]
    public void AGameDetected_StartsTheRecorderWithTheGamesSettings()
    {
        Arrange("Overwatch");

        _coordinator.Start();
        _detector.RaiseGameStarted("Overwatch");

        Assert.Equal(RecorderState.Recording, _recorder.Snapshot.State);
        Assert.Equal(["Overwatch"], _resolvedGames);
        Assert.Equal(1, _session.PlaceSourceCalls);
    }

    [Fact]
    public void NoGameDetected_DoesNotStartTheRecorder()
    {
        Arrange();

        _coordinator.Start();

        Assert.Equal(RecorderState.Idle, _recorder.Snapshot.State);
        Assert.Empty(_resolvedGames);
        Assert.Equal(0, _session.PlaceSourceCalls);
    }

    [Fact]
    public void AGameStopping_StopsTheRecorderForAGameEnd()
    {
        Arrange();
        _coordinator.Start();
        _detector.RaiseGameStarted("Overwatch");
        Assert.Equal(RecorderState.Recording, _recorder.Snapshot.State);

        _detector.RaiseGameStopped();

        Assert.Equal(RecorderState.Stopping, _recorder.Snapshot.State);

        _session.LastCreatedOutput!.RaiseStop(ObsOutputStopCode.Success);

        Assert.Equal(RecorderState.Idle, _recorder.Snapshot.State);
        Assert.Equal(RecorderStopReason.GameStopped, _recorder.Snapshot.LastStopReason);
    }

    [Fact]
    public void AGameStoppingWhenNothingIsRecording_DoesNothing()
    {
        Arrange();

        _coordinator.Start();
        _detector.RaiseGameStopped();

        Assert.Equal(RecorderState.Idle, _recorder.Snapshot.State);
        Assert.Equal(0, _session.PlaceSourceCalls);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheDetector()
    {
        Arrange();
        _coordinator.Start();

        _coordinator.Dispose();
        _detector.RaiseGameStarted("Overwatch");

        Assert.Equal(RecorderState.Idle, _recorder.Snapshot.State);
        Assert.Empty(_resolvedGames);
    }
}
