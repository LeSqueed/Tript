// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.Core;
using Tript.Media;
using Tript.Settings;
using Serilog;

namespace Tript.App;

internal sealed partial class AppHost
{
    internal string ContentRoot => EffectiveRoot;

    internal void OpenFileLocation(OpenFileLocationParameters? parameters)
    {
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.FilePath))
            return;

        var path = ContentServer.ResolveWithinRoot(EffectiveRoot, parameters.FilePath);
        if (path is null || !File.Exists(path))
        {
            PushError("That file is not inside the recording folder or no longer exists.");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path.Replace("\"", string.Empty)}\"",
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", $"-R \"{path.Replace("\"", string.Empty)}\"");
            }
            else
            {
                Process.Start("xdg-open", Path.GetDirectoryName(path)!);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            PushError($"The file location could not be opened: {exception.Message}");
        }
    }

    internal void OpenInBrowser(OpenInBrowserParameters? parameters)
    {
        var url = parameters?.Url;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Log.Warning(exception, "AppHost: could not open {Url} in a browser", url);
        }
    }

    internal void PushContent() => _contentPush.Run();

    private void BroadcastContent()
    {
        var items = ListContent();
        _ipc.Broadcast("content", JsonSerializer.SerializeToElement(new
        {
            content = items,
        }, Wire.Options));
        BroadcastStorageReport(items);
    }

    private void ReportContentFailure(Exception exception)
    {
        Log.Warning(exception, "AppHost: the library could not be listed");
        PushError($"The library could not be listed ({exception.Message}).");
    }

    private static bool IsUsableOffsetSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds;

    internal void PushError(string message)
    {
        _ipc.Broadcast("error", JsonSerializer.SerializeToElement(new
        {
            message,
        }, Wire.Options));
        RequestNotification(NotificationKind.Error, "Tript error", message);
    }

    private void RequestNotification(NotificationKind kind, string title, string body) =>
        NotificationRequested?.Invoke(kind, title, body);

    private void PushWarning(string? message)
    {
        _ipc.Broadcast("warning", JsonSerializer.SerializeToElement(
            message is null ? null : new { message }, Wire.Options));
    }

    internal List<ContentItem> ListContent() =>
        new ContentCatalogue(EffectiveRoot, _metadata, _clipTitles, _libraryProbe, Games, ApplyBackfill)
            .Build(CaptureLibraryState());

    private LibraryState CaptureLibraryState()
    {
        AutomaticClipProgress? automaticClips;
        lock (_automaticClipGate)
        {
            automaticClips = _automaticClipJob is { } job
                ? new AutomaticClipProgress(job.SourceSessionPath,
                    job.PausedByUser || _backgroundWorkSuspendedForRecording, job.Completed, job.Total)
                : null;
        }

        var activeRecordingPath = IsRecording && _activeSessionPath is { Length: > 0 } active
            ? RelativeToRoot(active)
            : null;
        return new LibraryState(activeRecordingPath, _pendingMetadata,
            _sessionTracker.Active?.Bookmarks ?? [], automaticClips);
    }

    private void ApplyBackfill(LibraryBackfill backfill)
    {
        switch (backfill)
        {
            case LibraryBackfill.RecordingDuration duration:
                _metadata.SaveDuration(duration.FileName, duration.WirePath, duration.Seconds);
                break;
            case LibraryBackfill.ClipDuration duration:
                _clipTitles.SaveDuration(duration.FileName, duration.Seconds);
                break;
            case LibraryBackfill.ClipHdr hdr:
                _clipTitles.SaveHdrStatus(hdr.FileName, hdr.IsHdr);
                break;
            case LibraryBackfill.ClipGame game:
                _clipTitles.SaveGame(game.FileName, game.Game, game.GameId);
                break;
        }
    }

    private string? NormalizeSourcePath(string? sourcePath) =>
        ContentCatalogue.NormalizeSourcePath(EffectiveRoot, sourcePath);

    private static StringComparer ContentPathComparer => FilePaths.Comparer;

    private static StringComparison ContentPathComparison => FilePaths.Comparison;
}
