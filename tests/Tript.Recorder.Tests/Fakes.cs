// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.Recorder.Tests;

internal sealed class FakeRecorderSession : IRecorderSession
{
    public FakeOutput? LastCreatedOutput { get; private set; }

    public int PlaceSourceCalls { get; private set; }

    public int ClearSourceCalls { get; private set; }

    public Exception? CreateOutputError { get; set; }

    public Exception? PlaceSourceError { get; set; }

    public void SetOutput(FakeOutput output) => LastCreatedOutput = output;

    public IRecorderOutput CreateOutput(ResolvedRecorderSettings settings)
    {
        if (CreateOutputError is not null)
            throw CreateOutputError;

        LastCreatedOutput ??= new FakeOutput();
        return LastCreatedOutput;
    }

    public void PlaceSourceOnChannel()
    {
        PlaceSourceCalls++;
        if (PlaceSourceError is not null)
            throw PlaceSourceError;
    }

    public void ClearSourceFromChannel() => ClearSourceCalls++;

    public CapturePolicy Policy => CapturePolicy.Default;

    public bool HasGameCaptureSource => false;

    public bool HasDisplayFallback => true;

    public bool WaitForGameCapture(TimeSpan deadline, TimeSpan warningAfter, Action showWarning,
        Action clearWarning, CancellationToken cancellationToken) => true;

    public bool Disposed { get; private set; }

    internal void ResetCalls()
    {
        PlaceSourceCalls = 0;
        ClearSourceCalls = 0;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeOutput : IRecorderOutput
{
    private bool _active;
    private readonly ManualResetEventSlim _stopped = new(false);

    public bool IsActive => _active;

    public bool StartReturns { get; set; } = true;

    public Exception? StartError { get; set; }

    public bool RaiseStopDuringStart { get; set; }

    public string? LastError { get; set; }

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public bool Disposed { get; private set; }

    public event EventHandler<ObsOutputStopEvent>? Stopped;

    public bool Start()
    {
        StartCalls++;
        _stopped.Reset();
        if (StartError is not null)
            throw StartError;

        _active = StartReturns;
        if (RaiseStopDuringStart)
            RaiseStop(ObsOutputStopCode.Success);

        return StartReturns;
    }

    public void Stop()
    {
        StopCalls++;
        _active = false;
        _stopped.Set();
    }

    public void RaiseStop(ObsOutputStopCode code, string? lastError = null)
    {
        _active = false;
        _stopped.Set();
        Stopped?.Invoke(this, new ObsOutputStopEvent(code, lastError));
    }

    public bool WaitForStop(TimeSpan timeout) => _stopped.Wait(timeout);

    public void Dispose() => Disposed = true;
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
