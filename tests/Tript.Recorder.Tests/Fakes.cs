// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.Recorder.Tests;

// A fake IRecorderSession: no libobs anywhere, the output is a fake, and the wiring calls are
// recorded so a test can assert on what the recorder asked of its session.
internal sealed class FakeRecorderSession : IRecorderSession
{
    public FakeOutput? LastCreatedOutput { get; private set; }

    public int PlaceSourceCalls { get; private set; }

    public int ClearSourceCalls { get; private set; }

    // When set, CreateOutput throws rather than returning a fake — the wiring-failure path.
    public Exception? CreateOutputError { get; set; }

    // Installs the output the next CreateOutput call returns.
    public void SetOutput(FakeOutput output) => LastCreatedOutput = output;

    public IRecorderOutput CreateOutput(ResolvedRecorderSettings settings)
    {
        if (CreateOutputError is not null)
            throw CreateOutputError;

        LastCreatedOutput ??= new FakeOutput();
        return LastCreatedOutput;
    }

    public void PlaceSourceOnChannel() => PlaceSourceCalls++;

    public void ClearSourceFromChannel() => ClearSourceCalls++;

    public bool Disposed { get; private set; }

    internal void ResetCalls()
    {
        PlaceSourceCalls = 0;
        ClearSourceCalls = 0;
    }

    public void Dispose() => Disposed = true;
}

// A fake IRecorderOutput whose start/stop behaviour and stop signal the test drives. The recorder
// subscribes to Stopped itself; a test raises the event to simulate the output ending.
internal sealed class FakeOutput : IRecorderOutput
{
    private bool _active;

    public bool IsActive => _active;

    public bool StartReturns { get; set; } = true;

    public string? LastError { get; set; }

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public bool Disposed { get; private set; }

    public event EventHandler<ObsOutputStopEvent>? Stopped;

    public bool Start()
    {
        StartCalls++;
        _active = StartReturns;
        return StartReturns;
    }

    public void Stop()
    {
        StopCalls++;
        _active = false;
    }

    // Simulates the output's stop signal, the way the binding delivers it.
    public void RaiseStop(ObsOutputStopCode code, string? lastError = null)
    {
        _active = false;
        Stopped?.Invoke(this, new ObsOutputStopEvent(code, lastError));
    }

    public void Dispose() => Disposed = true;
}

// A fake IGameDetector the auto-start tests drive by hand.
internal sealed class FakeGameDetector : IGameDetector
{
    public bool Started { get; private set; }

    public event Action<string>? GameStarted;

    public event Action? GameStopped;

    public void Start() => Started = true;

    public void RaiseGameStarted(string game) => GameStarted?.Invoke(game);

    public void RaiseGameStopped() => GameStopped?.Invoke();

    public void Dispose()
    {
    }
}

internal static class TestSettings
{
    internal static ResolvedRecorderSettings Session(string outputPath = "out.mp4") => new()
    {
        Mode = RecordingMode.Session,
        OutputPath = outputPath,
        ResolutionWidth = 1920,
        ResolutionHeight = 1080,
        Fps = 60,
        Encoder = "x264",
        Quality = 10,
        AudioTracks = []
    };
}
