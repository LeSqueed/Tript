// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.App.Ipc;
using Tript.Media;

namespace Tript.App;

// The command surface of the control socket: every frontend -> backend command, implemented or an
// explicit no-op. The implemented set is the alpha's: recording, settings, content, bookmarks,
// clipping, and the lifecycle commands. Everything else is a named no-op so the frontend's probe
// buttons never crash the host.
//
// Dispatch is by exact wire method name (PascalCase, spec/local-ipc.md). Unknown methods are
// dropped silently — the frontend tolerates unknown backend messages, and the backend tolerates
// unknown commands the same way.
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
            ["StartRecording"] = (_, _) => _host.StartRecording(null),
            ["StopRecording"] = (_, _) => _host.StopRecording(),
            ["ToggleFullscreen"] = (parameters, _) => _host.ToggleFullscreen(
                parameters.GetPropertyOrDefault("enabled").GetBooleanOr(false)),
            ["CheckForUpdates"] = (_, _) => _host.CheckForUpdates(),
            ["ApplyUpdate"] = (_, _) => { /* No update pipeline; accepted and ignored. */ },
            ["RefreshStorageStats"] = (_, _) => _host.RefreshStorageStats(),
            ["OpenLogsLocation"] = (_, _) => _host.OpenLogsLocation(),
            ["MigrateContent"] = (_, _) => _host.MigrateContent(),
            ["CreateClip"] = (parameters, _) => _host.CreateClip(BuildClipRequest(parameters)),
            ["ListContent"] = (_, _) => _host.PushContent(),
            ["CancelClip"] = (_, _) => { /* The engine is not cancellable in the alpha. */ },
            ["DeleteContent"] = (parameters, _) => _host.DeleteContent(parameters.Deserialize<DeleteContentParameters>()),
            ["DeleteMultipleContent"] = (parameters, _) => _host.DeleteMultipleContent(
                parameters.Deserialize<DeleteMultipleContentParameters>()),
            ["RenameContent"] = (parameters, _) => _host.RenameContent(parameters.Deserialize<RenameContentParameters>()),
            ["ImportFile"] = (_, _) => { /* No import surface in the alpha. */ },
            ["AddBookmark"] = (parameters, _) => _host.AddBookmark(parameters.Deserialize<AddBookmarkParameters>()),
            ["DeleteBookmark"] = (parameters, _) => _host.DeleteBookmark(parameters.Deserialize<DeleteBookmarkParameters>()),
            ["UpdateSettings"] = (parameters, _) => _host.UpdateSettings(
                parameters.Deserialize<UpdateSettingsParameters>()?.Settings),
            ["SetVideoLocation"] = (_, _) => { /* No native folder picker in the alpha. */ },
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

    private ClipRequest BuildClipRequest(JsonElement? parameters)
    {
        var parsed = parameters.Deserialize<CreateClipParameters>() ?? new CreateClipParameters();

        var mode = parsed.OutputMode.Equals("separate", StringComparison.OrdinalIgnoreCase)
            ? ClipMode.Separate
            : ClipMode.Combine;

        var regions = parsed.Segments.Count > 0
            ? parsed.Segments.Select(segment => ClipRegion.FromSeconds(segment.StartTime, segment.EndTime)).ToList()
            : [ClipRegion.FromSeconds(parsed.StartTime, parsed.EndTime)];

        var outputPath = BuildClipOutputPath(parsed, _host.EffectiveRoot);

        return new ClipRequest
        {
            SourcePath = parsed.FilePath,
            Regions = regions,
            Mode = mode,
            OutputPath = outputPath,
            AudioTrackAdjustments = [],
            EncoderFamily = "libx264",
            Progress = null,
        };
    }

    // Where a clip is written. Clips live in a single top-level clips/ directory under the
    // recording root. The name carries both the source session and the clip id (the frontend's
    // newClipId()), so a combine clip and a separate-mode batch all stay distinct:
    //   <root>/clips/session-20260817-083000-clip-k2m3xq.mp4
    //   <root>/clips/session-20260817-083000-clip-1-0s-10s.mp4   (separate mode)
    // Separate mode contributes only the directory; the engine (BuildFileName) supplies the
    // per-region file names inside it.
    internal static string BuildClipOutputPath(CreateClipParameters parameters, string effectiveRoot)
    {
        var outputDirectory = Path.Combine(effectiveRoot, "clips");

        if (parameters.OutputMode.Equals("separate", StringComparison.OrdinalIgnoreCase))
        {
            return outputDirectory;
        }

        var sourceBaseName = Path.GetFileNameWithoutExtension(parameters.FilePath);
        var id = parameters.Id.Length > 0 ? parameters.Id : Guid.NewGuid().ToString("N");
        return Path.Combine(outputDirectory, $"{sourceBaseName}-{id}.mp4");
    }
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
            // Round-tripping through the raw text gives every deserialized JsonElement property
            // its own backing document, and the call sites must invoke this WITHOUT the ?. operator
            // (the extension handles null itself) — `parameters?.Deserialize<T>()` on a
            // Nullable<JsonElement> drops the document handle before the method body runs.
            return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), Wire.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
