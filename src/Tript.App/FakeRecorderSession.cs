// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.App;

// The seam-level recorder session: no libobs anywhere, so the IPC and protocol layers run without a
// display server, the OBS modules, or the muxer helper. It mirrors the fakes in
// Tript.Recorder.Tests: CreateOutput returns a fake output whose start always succeeds, the wiring
// calls are no-ops, and the stop signal is raised synchronously by Stop so the recorder's Idle ->
// Recording -> Stopping -> Idle round-trip completes without a muxer.
internal sealed class FakeRecorderSession : IRecorderSession
{
    private readonly FakeOutput _output = new();

    public IRecorderOutput CreateOutput(ResolvedRecorderSettings settings)
    {
        _output.LastSettings = settings;
        return _output;
    }

    public void PlaceSourceOnChannel()
    {
    }

    public void ClearSourceFromChannel()
    {
    }

    public void Dispose()
    {
    }

    internal sealed class FakeOutput : IRecorderOutput
    {
        public ResolvedRecorderSettings? LastSettings { get; set; }

        public bool IsActive { get; private set; }

        public string? LastError => null;

        public event EventHandler<ObsOutputStopEvent>? Stopped;

        public bool Start()
        {
            IsActive = true;
            return true;
        }

        public void Stop()
        {
            if (!IsActive)
                return;
            IsActive = false;
            // The recorder subscribes to the stop signal and expects it to complete the transition
            // back to Idle. The fake raises it synchronously, so a Stop returns with the recorder
            // already Idle.
            Stopped?.Invoke(this, new ObsOutputStopEvent(ObsOutputStopCode.Success, null));
        }

        public void Dispose()
        {
        }
    }
}
