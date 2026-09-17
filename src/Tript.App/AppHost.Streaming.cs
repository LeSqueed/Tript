// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.ComponentModel;
using System.Text.Json;
using Serilog;
using Tript.Obs;
using Tript.Obs.Spout;
using Tript.Recorder;
using Tript.Settings;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

internal sealed partial class AppHost
{
    internal const string HookConflictWarning =
        "Still connecting to the game. OBS is open and may be recording this game too. " +
        "The Streamer tab shows how to let OBS use Tript's picture instead.";

    internal const string HookConflictFallbackWarning =
        "Tript could not connect to the game, so it is recording your whole screen. OBS is open and may " +
        "be recording this game too. The Streamer tab shows how to let OBS use Tript's picture instead.";

    private StreamShare<ObsSource>? _streamShare;
    private ObsProcessWatcher? _obsWatcher;
    private string? _graphicsAdapterName;
    private bool _graphicsAdapterRead;
    private int _hookConflictSuspected;

    private void InitializeStreaming()
    {
        _streamShare = ObsStreamShare.Create(realRecorder: _runtime is not null && !_options.FakeRecorder);
        _streamShare.StatusChanged += _ => PushStreamerStatus();
        _obsWatcher = new ObsProcessWatcher(OnObsPresenceChanged);
        ApplyStreamingSettings(_settingsStore.Load());
    }

    private void DisposeStreaming()
    {
        _obsWatcher?.Dispose();
        _streamShare?.Dispose();
    }

    private void ApplyStreamingSettings(SettingsModel settings)
    {
        if (_streamShare is null || _obsWatcher is null)
            return;

        var streaming = settings.Streaming;
        var senderName = SenderNameOrDefault(streaming.SenderName);
        _streamShare.Configure(streaming.ShareEnabled, streaming.ShareWhen, senderName);
        _obsWatcher.SetWatching(_streamShare.WantsObsPresence);
        _streamShare.SetObsRunning(_obsWatcher.Current.Running);
        SyncStreamShareCapture();
    }

    private static string SenderNameOrDefault(string? name) =>
        SpoutNaming.IsValidSenderName(name) ? name! : StreamingSettings.DefaultSenderName;

    private static string? ValidateStreamingSettings(StreamingSettings streaming) =>
        SpoutNaming.IsValidSenderName(streaming.SenderName)
            ? null
            : "the sender name can only use letters, numbers, spaces and basic symbols, up to 255 characters.";

    private void SyncStreamShareCapture()
    {
        ObsSource? capture;
        lock (_recorderGate)
            capture = (_recorderSession as ObsRecorderSession)?.GameCaptureSource;

        _streamShare?.SetCapture(capture);
    }

    private void OnObsPresenceChanged(ObsPresence presence)
    {
        Log.Information("AppHost: OBS is {State}{Version}", presence.Running ? "running" : "not running",
            presence.Version is null ? string.Empty : $" ({presence.Version})");
        _streamShare?.SetObsRunning(presence.Running);
        PushStreamerStatus();
    }

    private ObsPresence CurrentObsPresence()
    {
        if (_obsWatcher is { IsWatching: true } watcher)
            return watcher.Current;

        try
        {
            return ObsProcessWatcher.Probe();
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or Win32Exception)
        {
            return ObsPresence.Absent;
        }
    }

    private string HookWaitWarning()
    {
        if (!CurrentObsPresence().Running)
            return "Still connecting game capture. Recording will start when the hook is ready.";

        SetHookConflictSuspected(true);
        return HookConflictWarning;
    }

    private void ReportHookFallback()
    {
        if (!CurrentObsPresence().Running)
            return;

        SetHookConflictSuspected(true);
        PushWarning(HookConflictFallbackWarning);
    }

    private void SetHookConflictSuspected(bool suspected)
    {
        var next = suspected ? 1 : 0;
        if (Interlocked.Exchange(ref _hookConflictSuspected, next) != next)
            PushStreamerStatus();
    }

    private string? GraphicsAdapterName()
    {
        if (_graphicsAdapterRead || _runtime is null || _options.FakeRecorder)
            return _graphicsAdapterName;

        try
        {
            _graphicsAdapterName = ObsGraphicsAdapters.CurrentAdapterName(_runtime);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or ObjectDisposedException)
        {
            Log.Warning(exception, "AppHost: the graphics adapter name could not be read.");
        }

        _graphicsAdapterRead = true;
        return _graphicsAdapterName;
    }

    internal StreamerStatusInfo BuildStreamerStatus()
    {
        var status = _streamShare?.Status ?? StreamShareStatus.Initial;
        var presence = CurrentObsPresence();
        var settings = _settingsStore.Load().Streaming;
        return new StreamerStatusInfo
        {
            State = WireState(status.State),
            ShareEnabled = settings.ShareEnabled,
            ObsRunning = presence.Running,
            ObsVersion = presence.Version,
            SenderName = status.SenderName,
            Width = status.Width,
            Height = status.Height,
            AdapterName = GraphicsAdapterName(),
            HookConflictSuspected = Volatile.Read(ref _hookConflictSuspected) != 0,
        };
    }

    internal void PushStreamerStatus()
    {
        if (_disposed)
            return;

        _ipc.Broadcast("streamerStatus", JsonSerializer.SerializeToElement(BuildStreamerStatus(), Wire.Options));
    }

    internal static string WireState(StreamShareState state) => state switch
    {
        StreamShareState.Off => "off",
        StreamShareState.Unsupported => "unsupported",
        StreamShareState.WaitingForObs => "waitingForObs",
        StreamShareState.WaitingForCapture => "waitingForCapture",
        StreamShareState.Live => "live",
        StreamShareState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
