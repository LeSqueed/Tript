// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.Recorder.Tests;

// The state machine contract: the transitions, the refusals, and the failure paths — a stop code
// must surface as a reason, never be swallowed. The recorder is exercised through a fake session and
// fake output, so these tests make no contact with libobs at all.
public class RecorderStateMachineTests
{
    private readonly FakeRecorderSession _session = new();

    private static Recorder NewRecorder(FakeRecorderSession session)
        => new(session, TestSettings.Session());

    [Fact]
    public void IdleStart_TransitionsToRecording_AndPlacesTheSource()
    {
        using var recorder = NewRecorder(_session);

        var started = recorder.Start(TestSettings.Session());

        Assert.True(started);
        Assert.Equal(RecorderState.Recording, recorder.Snapshot.State);
        Assert.NotNull(recorder.Output);
        Assert.Equal(1, _session.PlaceSourceCalls);
        Assert.Null(recorder.Snapshot.LastStopReason);
    }

    [Fact]
    public void RecordingStop_TransitionsToStopping_ThenTheStopSignalReturnsToIdle()
    {
        using var recorder = NewRecorder(_session);
        recorder.Start(TestSettings.Session());

        var stopped = recorder.Stop();

        Assert.True(stopped);
        Assert.Equal(RecorderState.Stopping, recorder.Snapshot.State);

        _session.LastCreatedOutput!.RaiseStop(ObsOutputStopCode.Success);

        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
        Assert.Equal(RecorderStopReason.UserRequested, recorder.Snapshot.LastStopReason);
        Assert.Equal(ObsOutputStopCode.Success, recorder.Snapshot.LastStopCode);
        Assert.Equal(1, _session.ClearSourceCalls);
    }

    [Fact]
    public void CompletedStop_CanDrainTheDeferredOutputImmediately()
    {
        using var recorder = NewRecorder(_session);
        recorder.Start(TestSettings.Session());
        var output = _session.LastCreatedOutput!;

        output.RaiseStop(ObsOutputStopCode.Success);
        Assert.False(output.Disposed);

        recorder.DrainCompletedOutput();

        Assert.True(output.Disposed);
    }

    [Fact]
    public void StopFromIdle_IsANoOp()
    {
        using var recorder = NewRecorder(_session);

        var stopped = recorder.Stop();

        Assert.False(stopped);
        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
        Assert.Equal(0, _session.PlaceSourceCalls);
    }

    [Fact]
    public void StartWhileRecording_IsRefusedAndDoesNotDisturbTheRecording()
    {
        using var recorder = NewRecorder(_session);
        recorder.Start(TestSettings.Session());

        var secondStart = recorder.Start(TestSettings.Session("second.mp4"));

        Assert.False(secondStart);
        Assert.Equal(RecorderState.Recording, recorder.Snapshot.State);
        Assert.Equal(1, _session.PlaceSourceCalls);
    }

    [Fact]
    public void StartWhileStopping_IsRefused()
    {
        using var recorder = NewRecorder(_session);
        recorder.Start(TestSettings.Session());
        recorder.Stop();
        Assert.Equal(RecorderState.Stopping, recorder.Snapshot.State);

        var start = recorder.Start(TestSettings.Session());

        Assert.False(start);
        Assert.Equal(RecorderState.Stopping, recorder.Snapshot.State);
    }

    [Fact]
    public void AfterACompleteStop_StartWorksAgain()
    {
        using var recorder = NewRecorder(_session);
        recorder.Start(TestSettings.Session());
        _session.LastCreatedOutput!.RaiseStop(ObsOutputStopCode.Success);

        _session.ResetCalls();
        var started = recorder.Start(TestSettings.Session());

        Assert.True(started);
        Assert.Equal(RecorderState.Recording, recorder.Snapshot.State);
        Assert.Equal(1, _session.PlaceSourceCalls);
    }

    [Fact]
    public void AnUnsupportedMode_IsRefusedWithTheModeAsTheReason()
    {
        using var recorder = NewRecorder(_session);

        var buffer = TestSettings.Session();
        buffer.Mode = RecordingMode.Buffer;
        Assert.False(recorder.Start(buffer));
        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
        Assert.Equal(RecorderStopReason.UnsupportedMode, recorder.Snapshot.LastStopReason);
        Assert.Equal(0, _session.PlaceSourceCalls);

        var hybrid = TestSettings.Session();
        hybrid.Mode = RecordingMode.Hybrid;
        Assert.False(recorder.Start(hybrid));
        Assert.Equal(RecorderStopReason.UnsupportedMode, recorder.Snapshot.LastStopReason);
        Assert.Equal(0, _session.PlaceSourceCalls);
    }

    // The stop code is never swallowed: a failure the output reports when it stops is surfaced as
    // the reason, not reported as a clean user-requested end.
    [Fact]
    public void AnOutputFailure_SurfacesAsTheStopReason()
    {
        using var recorder = NewRecorder(_session);
        recorder.Start(TestSettings.Session());

        _session.LastCreatedOutput!.RaiseStop(ObsOutputStopCode.EncodeError, "muxer died");

        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
        Assert.Equal(RecorderStopReason.OutputFailure, recorder.Snapshot.LastStopReason);
        Assert.Equal(ObsOutputStopCode.EncodeError, recorder.Snapshot.LastStopCode);
        Assert.Equal("muxer died", recorder.Snapshot.LastError);
    }

    [Fact]
    public void ASynchronousStartRefusal_IsSurfacedWithTheReason()
    {
        // The fake cannot refuse before the recorder asks it to start, so the recorder has to see
        // a start refusal through the output's Start return. The fake session's default output
        // starts fine; a second recorder whose fake output refuses is the direct route.
        var refusing = new FakeRecorderSession();
        var output = new FakeOutput { StartReturns = false, LastError = "bad path" };
        refusing.SetOutput(output);

        using var recorder2 = new Recorder(refusing, TestSettings.Session());
        var started = recorder2.Start(TestSettings.Session());

        Assert.False(started);
        Assert.Equal(RecorderState.Idle, recorder2.Snapshot.State);
        Assert.Equal(RecorderStopReason.StartRefused, recorder2.Snapshot.LastStopReason);
        Assert.Equal("bad path", recorder2.Snapshot.LastError);
        Assert.Equal(1, output.StartCalls);
        Assert.Equal(0, refusing.PlaceSourceCalls);
    }

    [Fact]
    public void OutputStartException_CleansUpTheOutputAndRefusesTheStart()
    {
        var session = new FakeRecorderSession();
        var output = new FakeOutput { StartError = new InvalidOperationException("start failed") };
        session.SetOutput(output);
        using var recorder = new Recorder(session, TestSettings.Session());

        Assert.False(recorder.Start(TestSettings.Session()));
        Assert.True(output.Disposed);
        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
        Assert.Equal(RecorderStopReason.StartRefused, recorder.Snapshot.LastStopReason);
    }

    [Fact]
    public void PlaceSourceException_CleansUpTheStartedOutputAndClearsTheSource()
    {
        var session = new FakeRecorderSession
        {
            PlaceSourceError = new InvalidOperationException("place failed")
        };
        using var recorder = NewRecorder(session);

        Assert.False(recorder.Start(TestSettings.Session()));
        Assert.True(session.LastCreatedOutput!.Disposed);
        Assert.Equal(1, session.ClearSourceCalls);
        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
    }

    [Fact]
    public void OutputStoppingDuringStart_DoesNotPublishRecording()
    {
        var session = new FakeRecorderSession();
        var output = new FakeOutput { RaiseStopDuringStart = true };
        session.SetOutput(output);
        using var recorder = new Recorder(session, TestSettings.Session());

        Assert.False(recorder.Start(TestSettings.Session()));
        Assert.True(output.Disposed);
        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
        Assert.Equal(0, session.PlaceSourceCalls);
    }

    // A wiring failure (encoder unavailable, no video mix) refuses the start synchronously with the
    // exception's message as the reason.
    [Fact]
    public void AWiringFailure_RefusesTheStartWithTheExceptionMessage()
    {
        var session = new FakeRecorderSession { CreateOutputError = new ObsException("No loaded module registers the audio encoder 'ffmpeg_aac'.") };
        using var recorder = new Recorder(session, TestSettings.Session());

        var started = recorder.Start(TestSettings.Session());

        Assert.False(started);
        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
        Assert.Equal(RecorderStopReason.StartRefused, recorder.Snapshot.LastStopReason);
        Assert.Contains("ffmpeg_aac", recorder.Snapshot.LastError, StringComparison.Ordinal);
        Assert.Equal(0, session.PlaceSourceCalls);
    }

    // A stop that is neither user-requested nor a failure code: the game-ended path, which the
    // auto-start coordinator drives. The reason must survive the stop signal.
    [Fact]
    public void AStopForGameEnd_CompletesWithGameStoppedAsTheReason()
    {
        using var recorder = NewRecorder(_session);
        recorder.Start(TestSettings.Session());

        Assert.True(recorder.StopForGameEnd());
        Assert.Equal(RecorderState.Stopping, recorder.Snapshot.State);
        Assert.Equal(RecorderStopReason.GameStopped, recorder.Snapshot.LastStopReason);

        _session.LastCreatedOutput!.RaiseStop(ObsOutputStopCode.Success);

        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
        Assert.Equal(RecorderStopReason.GameStopped, recorder.Snapshot.LastStopReason);
        Assert.Equal(ObsOutputStopCode.Success, recorder.Snapshot.LastStopCode);
    }

    [Fact]
    public void StopForGameEndFromIdle_IsANoOp()
    {
        using var recorder = NewRecorder(_session);

        Assert.False(recorder.StopForGameEnd());
        Assert.Equal(RecorderState.Idle, recorder.Snapshot.State);
    }

    // A snapshot read from a thread that is not the recorder thread must be immutable and readable.
    [Fact]
    public void Snapshot_IsReadableAcrossThreads()
    {
        using var recorder = NewRecorder(_session);
        recorder.Start(TestSettings.Session());

        var snapshots = new List<RecorderStateSnapshot>();
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() => snapshots.Add(recorder.Snapshot))).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.All(snapshots, s => Assert.Equal(RecorderState.Recording, s.State));
    }

    [Fact]
    public void DisposeWhileRecording_MarksTheEndAsDisposed()
    {
        var recorder = NewRecorder(_session);
        recorder.Start(TestSettings.Session());

        recorder.Dispose();

        Assert.Equal(RecorderState.Stopping, recorder.Snapshot.State);
        Assert.Equal(RecorderStopReason.Disposed, recorder.Snapshot.LastStopReason);
        Assert.Equal(1, _session.ClearSourceCalls);

        // Dispose is idempotent.
        recorder.Dispose();
    }
}
