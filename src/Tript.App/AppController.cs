// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text;
using System.Text.Json;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.Media;

namespace Tript.App;

// The command surface of the control socket: every frontend -> backend command, implemented or an
// explicit no-op. The implemented set is the alpha's: recording, settings, content, bookmarks,
// clipping, and the lifecycle commands.
internal sealed class AppController
{
    private readonly AppHost _host;

    private readonly Dictionary<string, Action<JsonElement?, ClientHandle>> _commands;

    public AppController(AppHost host)
    {
        _host = host;
        _commands = new Dictionary<string, Action<JsonElement?, ClientHandle>>(StringComparer.Ordinal)
        {
            ["NewConnection"] = (_, client) => OnNewConnection(client),
            ["Shutdown"] = (_, _) => _host.Ipc.RequestShutdown(),
            // The refusal is reported, not discarded: StartRecording returns false before its
            // state push (recorder busy, a mode it does not record, a start libobs refused), so
            // without this the user presses record and nothing in the UI changes at all.
            ["StartRecording"] = (_, _) => _host.StartRecordingOrReport(null),
            ["StopRecording"] = (_, _) => _host.StopRecordingOrReport(),
            ["ToggleFullscreen"] = (parameters, _) => _host.ToggleFullscreen(
                parameters.GetPropertyOrDefault("enabled").GetBooleanOr(false)),
            ["CheckForUpdates"] = (_, _) => _host.CheckForUpdates(),
            ["ApplyUpdate"] = (_, _) => { /* No update pipeline; accepted and ignored. */ },
            ["RefreshStorageStats"] = (_, _) => _host.RefreshStorageStats(),
            ["OpenLogsLocation"] = (_, _) => _host.OpenLogsLocation(),
            ["MigrateContent"] = (_, _) => _host.MigrateContent(),
            ["CreateClip"] = (parameters, _) => CreateClip(parameters),
            ["ListContent"] = (_, _) => _host.PushContent(),
            ["CancelClip"] = (_, _) => { /* The engine is not cancellable in the alpha. */ },
            ["DeleteContent"] = (parameters, _) => _host.DeleteContent(parameters.Deserialize<DeleteContentParameters>()),
            ["DeleteMultipleContent"] = (parameters, _) => _host.DeleteMultipleContent(
                parameters.Deserialize<DeleteMultipleContentParameters>()),
            ["RenameContent"] = (parameters, _) => _host.RenameContent(parameters.Deserialize<RenameContentParameters>()),
            ["ListTrash"] = (_, _) => _host.PushTrash(),
            ["RestoreTrash"] = (parameters, _) => _host.RestoreTrash(parameters.Deserialize<RestoreTrashParameters>()),
            // Parameterless PurgeTrash empties the whole bin, so the absent-parameters case has to
            // reach the host rather than being dropped as a malformed command. It is substituted
            // HERE, where "absent" is still distinguishable: Deserialize answers null for a frame it
            // could not parse as well as for one that carried nothing, and conflating those would let
            // a malformed frame empty the entire bin.
            ["PurgeTrash"] = (parameters, _) => _host.PurgeTrash(
                parameters is null ? new PurgeTrashParameters() : parameters.Deserialize<PurgeTrashParameters>()),
            ["ImportFile"] = (_, _) => { /* No import surface in the alpha. */ },
            ["AddBookmark"] = (parameters, _) => _host.AddBookmark(parameters.Deserialize<AddBookmarkParameters>()),
            ["DeleteBookmark"] = (parameters, _) => _host.DeleteBookmark(parameters.Deserialize<DeleteBookmarkParameters>()),
            // The settings counterpart of ListContent. OnNewConnection already pushes settings when
            // the socket opens, but the settings UI mounts on demand — the route is not the landing
            // one — so by then that push is long gone and the page would render on its own defaults
            // until the user's first edit triggered one.
            ["ListSettings"] = (_, _) => _host.PushSettings(),
            ["UpdateSettings"] = (parameters, _) => _host.UpdateSettings(
                parameters.Deserialize<UpdateSettingsParameters>()?.Settings),
            ["SetVideoLocation"] = (_, _) => _host.RequestVideoLocation(),
            ["SetCacheLocation"] = (_, _) => { /* No native folder picker in the alpha. */ },
            ["SelectGameExecutable"] = (_, client) =>
            {
                // The picker command's reply is a message; the alpha has no picker, so nothing is
                // sent.
            },
            ["ApplyVideoPreset"] = (_, _) => { /* No presets in the alpha. */ },
            ["ApplyClipPreset"] = (_, _) => { /* No presets in the alpha. */ },
            ["OpenFileLocation"] = (_, _) => { /* No file manager integration in the alpha. */ },
            ["CopyFileToClipboard"] = (_, _) => { /* No clipboard integration in the alpha. */ },
            ["OpenInBrowser"] = (_, _) => { /* No browser integration in the alpha. */ },
            ["StorageWarningConfirm"] = (_, _) => { /* No storage warnings raised. */ },
            ["RecoveryConfirm"] = (parameters, _) => _host.RecoveryConfirm(parameters.Deserialize<RecoveryConfirmParameters>()),
        };
    }

    internal void Handle(string method, JsonElement? parameters, ClientHandle client)
    {
        if (_commands.TryGetValue(method, out var handler))
        {
            handler(parameters, client);
        }
        // Unknown methods are dropped silently, matching the frontend's tolerance of unknown
        // backend messages.
    }

    // ---- NewConnection ----

    private void OnNewConnection(ClientHandle client)
    {
        // A full push on connection: state, settings, game list. The frontend sends NewConnection
        // on socket open and expects to converge on these three.
        _host.PushState(_host.IsRecording, _host.CurrentGameId);
        _host.PushSettings();
        _host.PushGameList();

        // The recovery prompt, when there are orphaned files. Run after the state push so the
        // frontend has its content model before the prompt arrives.
        _host.RaiseRecoveryPromptIfNeeded(client);
    }

    // ---- CreateClip ----

    // The wire's filePath is relative to the effective recording root, by design: ListContent
    // builds ContentItem.FilePath with Path.GetRelativePath against EffectiveRoot and '/'
    // separators (AppHost.ListContent), because that is the form the content server's URLs take,
    // and the frontend echoes that exact string back in CreateClip. Every consumer that then
    // touches the file system has to resolve it against the root first.
    private void CreateClip(JsonElement? parameters)
    {
        var parsed = parameters.Deserialize<CreateClipParameters>() ?? new CreateClipParameters();
        var request = BuildClipRequest(parsed, _host.EffectiveRoot, out var refusal);
        if (request is null)
        {
            // The request never reaches the engine: either the source path did not resolve inside
            // the recording root (a traversal, an absolute path outside it, or an empty filePath)
            // or its segment times were not real times at all. The refusal rides the importProgress
            // "error" the engine's own failures already use, so the clip dialog surfaces it exactly
            // like a bad source file instead of the command dying silently.
            _host.PushClipError(refusal
                ?? $"That clip's source is not inside the recording folder, so it was not read: '{parsed.FilePath}'.");
            return;
        }

        _host.CreateClip(request);
    }

    // Builds the engine request, or null (with the reason in `refusal`) when the source path cannot
    // be resolved inside the recording root or the segment times are not usable. Static and internal
    // so the resolution can be asserted directly: the suite's hosts run with a CWD that is not the
    // content root, but the bug hid behind the engine's ffmpeg dependency, so the absoluteness of
    // SourcePath is worth pinning on its own.
    internal static ClipRequest? BuildClipRequest(CreateClipParameters parsed, string effectiveRoot) =>
        BuildClipRequest(parsed, effectiveRoot, out _);

    internal static ClipRequest? BuildClipRequest(CreateClipParameters parsed, string effectiveRoot,
        out string? refusal)
    {
        refusal = null;

        var mode = parsed.OutputMode.Equals("separate", StringComparison.OrdinalIgnoreCase)
            ? ClipMode.Separate
            : ClipMode.Combine;

        // The wire's seconds become TimeSpans here, which is the one conversion that can throw on
        // input this method does not control: TimeSpan.FromSeconds throws ArgumentException on NaN
        // and OverflowException on anything past ~9.22e11 seconds — including 1e18, a JSON number a
        // client can send without trying. This runs on the IPC dispatch thread, where
        // IpcServer.Dispatch catches the throw and only writes it to stderr, so the frontend would
        // receive no frame at all: not the "importing" one, not an error, nothing for the clip
        // dialog to render.
        var wireSegments = parsed.Segments.Count > 0
            ? parsed.Segments.Select(segment => (segment.StartTime, segment.EndTime)).ToList()
            : [(parsed.StartTime, parsed.EndTime)];

        // The content server's traversal guard, reused rather than re-derived: it joins the '/'
        // separated wire path onto the root, normalizes separators for the platform, and returns
        // null for anything that escapes the root — a "../../etc/passwd" filePath is refused here,
        // and an already-absolute path is accepted only when it points inside the root. The result
        // is always absolute, so the engine no longer depends on the CWD.
        var sourcePath = ContentServer.ResolveWithinRoot(effectiveRoot, parsed.FilePath);
        if (sourcePath is null)
        {
            refusal =
                $"That clip's source is not inside the recording folder, so it was not read: '{parsed.FilePath}'.";
            return null;
        }

        var regions = new List<ClipRegion>(wireSegments.Count);
        foreach (var (start, end) in wireSegments)
        {
            if (ClipRegionBounds.TryFromSeconds(start, end, out var region))
                regions.Add(region);
        }

        if (regions.Count == 0)
        {
            refusal = "That clip's marked times are not real times (not a number, infinite, or out of "
                + "range), so nothing was clipped. Re-mark the region and try again.";
            return null;
        }

        var outputPath = BuildClipOutputPath(parsed, effectiveRoot);

        return new ClipRequest
        {
            SourcePath = sourcePath,
            Regions = regions,
            Mode = mode,
            OutputPath = outputPath,
            AudioTrackAdjustments = [],
            EncoderFamily = "libx264",
            Title = parsed.Title,
            Progress = null,
        };
    }

    // Where a clip is written. Clips live in a single top-level clips/ directory under the
    // recording root.
    internal static string BuildClipOutputPath(CreateClipParameters parameters, string effectiveRoot)
    {
        var outputDirectory = Path.Combine(effectiveRoot, "clips");

        if (parameters.OutputMode.Equals("separate", StringComparison.OrdinalIgnoreCase))
        {
            return outputDirectory;
        }

        var sourceBaseName = Path.GetFileNameWithoutExtension(parameters.FilePath);
        return Path.Combine(outputDirectory, $"{sourceBaseName}-{SafeClipId(parameters.Id)}.mp4");
    }

    // The wire's clip id, reduced to something that can only ever be one path segment. It arrives
    // straight off the socket and is concatenated into a file name, so without this an id of
    // "x/../../../../tmp/pwn" would have the engine create directories and write a file anywhere the
    // user can write — the source path is resolved through the traversal guard, but the output path
    // is composed here and never was.
    internal static string SafeClipId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.NewGuid().ToString("N");

        var safe = new StringBuilder(id.Length);
        foreach (var character in id)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                safe.Append(character);

            if (safe.Length == MaxClipIdLength)
                break;
        }

        // An id made entirely of characters that cannot appear in a name still has to produce a
        // distinct file, so it gets a generated one rather than colliding on the empty string.
        return safe.Length > 0 ? safe.ToString() : Guid.NewGuid().ToString("N");
    }

    private const int MaxClipIdLength = 64;
}

// The per-client handle the controller needs: a way to push messages to one client (the recovery
// prompt) and nothing else.
internal sealed class ClientHandle
{
    private readonly Action<string, JsonElement> _send;

    internal ClientHandle(Action<string, JsonElement> send)
    {
        _send = send;
    }

    internal void Push(string method, JsonElement content) => _send(method, content);
}

internal static class JsonElementExtensions
{
    internal static bool GetBooleanOr(this JsonElement? element, bool fallback)
    {
        if (element is null)
            return fallback;
        try
        {
            return element.Value.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return fallback;
        }
    }

    internal static JsonElement? GetPropertyOrDefault(this JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
            return null;
        return element.Value.TryGetProperty(name, out var value) ? value : null;
    }

    internal static T? Deserialize<T>(this JsonElement? element) where T : class
    {
        if (element is null)
            return null;
        try
        {
            // Deserialize from the raw text rather than from the JsonElement itself. A JsonElement
            // is a handle into a JsonDocument; deserializing a JsonElement-typed property from it
            // keeps a reference to that document, and the nullable-conditional call sites
            // (parameters?.Deserialize<T>()) produce an element whose document handle is lost.
            return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), Wire.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
