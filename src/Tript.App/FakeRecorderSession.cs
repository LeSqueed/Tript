// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Obs;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.App;

// The seam-level recorder session: no libobs anywhere, so the IPC and protocol layers run without a
// display server, the OBS modules, or the muxer helper. It mirrors the fakes in
// Tript.Recorder.Tests: CreateOutput returns a fake output whose start always succeeds, the wiring
// calls are no-ops, and the stop signal is synchronous by default. Tests can disable completion to
// exercise the host's stopping timeout without a muxer.
internal sealed class FakeRecorderSession : IRecorderSession
{
    internal const string SettingsTraceEnvironmentVariable = "TRIPT_FAKE_RECORDER_SETTINGS_TRACE";
    private readonly FakeOutput _output = new();

    internal bool CompleteStopSynchronously { get; set; } = true;

    public IRecorderOutput CreateOutput(ResolvedRecorderSettings settings)
    {
        _output.LastSettings = settings;
        _output.CompleteStopSynchronously = CompleteStopSynchronously;
        if (Environment.GetEnvironmentVariable(SettingsTraceEnvironmentVariable) is { Length: > 0 } tracePath)
        {
            File.AppendAllText(tracePath,
                JsonSerializer.Serialize(new { settings.Display }) + Environment.NewLine);
        }
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

    internal void CompleteStop() => _output.CompleteStop();

    internal sealed class FakeOutput : IRecorderOutput, IReplayBufferOutput
    {
        public ResolvedRecorderSettings? LastSettings { get; set; }

        public bool IsActive { get; private set; }

        public string? LastError => null;

        public event EventHandler<ObsOutputStopEvent>? Stopped;

        internal bool CompleteStopSynchronously { get; set; } = true;

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
            if (!CompleteStopSynchronously)
                return;
            // The recorder subscribes to the stop signal and expects it to complete the transition
            // back to Idle. The fake raises it synchronously, so a Stop returns with the recorder
            // already Idle.
            Stopped?.Invoke(this, new ObsOutputStopEvent(ObsOutputStopCode.Success, null));
        }

        public bool WaitForStop(TimeSpan timeout) => !IsActive;

        public bool SaveReplay(string directory, string format, Action<string> onSaved)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"fake-replay-{Guid.NewGuid():N}.mp4");
            File.WriteAllText(path, string.Empty);
            onSaved(path);
            return true;
        }

        public bool WaitForReplaySave(TimeSpan timeout) => true;

        internal void CompleteStop()
        {
            if (IsActive)
                return;

            Stopped?.Invoke(this, new ObsOutputStopEvent(ObsOutputStopCode.Success, null));
        }

        public void Dispose()
        {
        }
    }
}
