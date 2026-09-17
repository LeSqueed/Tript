// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using System.Text;
using System.Text.Json;
using Tript.App.Content;
using Tript.App.Ipc;
using Tript.Media;

namespace Tript.App;

internal sealed class AppController
{
    private readonly AppHost _host;

    private readonly Dictionary<string, Action<JsonElement?, ClientHandle>> _commands;
    private readonly Dictionary<string, Func<JsonElement?, ClientHandle, Task>> _asyncCommands;

    public AppController(AppHost host)
    {
        _host = host;
        _commands = new Dictionary<string, Action<JsonElement?, ClientHandle>>(StringComparer.Ordinal)
        {
            ["NewConnection"] = (_, _) => OnNewConnection(),
            ["Shutdown"] = (_, _) => _host.Ipc.RequestShutdown(),

            ["StartRecording"] = (parameters, _) =>
            {
                var parsed = parameters.Deserialize<StartRecordingParameters>();
                _host.StartRecordingOrReport(
                    parsed?.GameId,
                    parsed?.ApplyDisplay == true ? parsed.DisplayId : null,
                    parsed?.ApplyDisplay == true);
            },
            ["StopRecording"] = (_, _) => _host.StopRecordingOrReport(),
            ["ApplyUpdate"] = (_, _) => _host.ApplyUpdate(),
            ["CreateClip"] = (parameters, _) => CreateClip(parameters),
            ["ConvertToSdr"] = (parameters, _) => _host.ConvertToSdr(
                parameters.Deserialize<ConvertToSdrParameters>()),
            ["CreateAutomaticClips"] = (parameters, _) => _host.CreateAutomaticClips(
                parameters.Deserialize<CreateAutomaticClipsParameters>()),
            ["PauseAutomaticClips"] = (_, _) => _host.ToggleAutomaticClipPause(),
            ["ListGames"] = (_, _) => { _host.PushGameList(); _host.PushModelStatus(); },
            ["DeleteContent"] = (parameters, _) => _host.DeleteContent(parameters.Deserialize<DeleteContentParameters>()),
            ["DeleteMultipleContent"] = (parameters, _) => _host.DeleteMultipleContent(
                parameters.Deserialize<DeleteMultipleContentParameters>()),
            ["RenameContent"] = (parameters, _) => _host.RenameContent(parameters.Deserialize<RenameContentParameters>()),
            ["ToggleFavorite"] = (parameters, _) => _host.ToggleFavorite(parameters.Deserialize<ToggleFavoriteParameters>()),
            ["ListTrash"] = (_, _) => _host.PushTrash(),
            ["WatchAudioLevels"] = (_, _) => _host.WatchAudioLevels(),
            ["RestoreTrash"] = (parameters, _) => _host.RestoreTrash(parameters.Deserialize<RestoreTrashParameters>()),

            ["PurgeTrash"] = (parameters, _) => _host.PurgeTrash(
                parameters is null ? new PurgeTrashParameters() : parameters.Deserialize<PurgeTrashParameters>()),
            ["AddBookmark"] = (parameters, _) => _host.AddBookmark(parameters.Deserialize<AddBookmarkParameters>()),
            ["DeleteBookmark"] = (parameters, _) => _host.DeleteBookmark(parameters.Deserialize<DeleteBookmarkParameters>()),

            ["ListSettings"] = (_, _) => _host.PushSettings(),
            ["GetStreamerStatus"] = (_, _) => _host.PushStreamerStatus(),
            ["UpdateSettings"] = (parameters, _) =>
            {
                var parsed = parameters.Deserialize<UpdateSettingsParameters>();
                _host.UpdateSettings(parsed?.Settings, parsed?.RequestId);
            },
            ["SetVideoLocation"] = (_, _) => _host.RequestVideoLocation(),
            ["BrowseTrainingFolder"] = (_, _) => _host.RequestTrainingFolder(),
            ["SelectGameExecutable"] = (parameters, _) => _host.RequestGameExecutable(
                parameters.Deserialize<SelectGameExecutableParameters>()?.RequestId ?? string.Empty),
            ["AddGameCandidate"] = (parameters, _) =>
            {
                var parsed = parameters.Deserialize<AddGameCandidateParameters>();
                if (parsed is not null)
                    _host.AddGameCandidate(parsed.Name, parsed.ExecutablePath, parsed.RequestId);
            },
            ["IgnoreGameCandidate"] = (parameters, _) =>
            {
                var parsed = parameters.Deserialize<GameCandidateParameters>();
                _host.IgnoreGameCandidate(parsed?.ExecutablePath, parsed?.RequestId);
            },
            ["OpenFileLocation"] = (parameters, _) => _host.OpenFileLocation(
                parameters.Deserialize<OpenFileLocationParameters>()),
            ["OpenInBrowser"] = (parameters, _) => _host.OpenInBrowser(parameters.Deserialize<OpenInBrowserParameters>()),
#if TRIPT_TRAINING
            ["CancelTraining"] = (_, _) => _host.CancelTraining(),
#endif
        };

        _asyncCommands = new Dictionary<string, Func<JsonElement?, ClientHandle, Task>>(StringComparer.Ordinal)
        {
            ["CheckForUpdates"] = (_, _) => Task.Run(_host.CheckForUpdatesManualAsync),
            ["SearchGames"] = async (parameters, client) =>
                await _host.SearchGamesAsync(parameters.Deserialize<SearchGamesParameters>(), client),
            ["ResolveGameSearch"] = async (parameters, client) =>
                await _host.ResolveGameSearchAsync(parameters.Deserialize<ResolveGameSearchParameters>(), client),
            ["RequestGameAdd"] = async (parameters, client) =>
                await _host.RequestGameAddAsync(parameters.Deserialize<RequestGameAddParameters>(), client),

            ["ListContent"] = (_, _) => Task.Run(_host.PushContent),
#if TRIPT_TRAINING
            ["ListTraining"] = async (parameters, _) =>
                await _host.PushTraining(parameters.Deserialize<TrainingGameParameters>()?.GameId),
            ["ImportTrainingAssets"] = async (parameters, _) =>
                await _host.ImportTrainingAssets(parameters.Deserialize<ImportTrainingParameters>()),
            ["CaptureTrainingSample"] = async (parameters, _) =>
                await _host.CaptureTrainingSample(parameters.Deserialize<CaptureTrainingSampleParameters>()),
            ["UpdateTrainingEvents"] = async (parameters, _) =>
                await _host.UpdateTrainingEvents(parameters.Deserialize<UpdateTrainingEventsParameters>()),
            ["UpdateTrainingRegionGroups"] = async (parameters, _) =>
                await _host.UpdateTrainingRegionGroups(
                    parameters.Deserialize<UpdateTrainingRegionGroupsParameters>()),
            ["GetTrainingSample"] = async (parameters, _) =>
                await _host.GetTrainingSample(parameters.Deserialize<TrainingSampleParameters>()),
            ["UpdateTrainingSample"] = async (parameters, _) =>
                await _host.UpdateTrainingSample(parameters.Deserialize<UpdateTrainingSampleParameters>()),
            ["SuggestTrainingLabels"] = async (parameters, _) =>
                await _host.SuggestTrainingLabels(parameters.Deserialize<SuggestTrainingLabelsParameters>()),
            ["DeleteTrainingSample"] = async (parameters, _) =>
                await _host.DeleteTrainingSample(parameters.Deserialize<TrainingSampleParameters>()),
            ["StartTraining"] = async (parameters, _) =>
                await _host.StartTraining(parameters.Deserialize<StartTrainingParameters>()),
            ["InstallTrainingModel"] = async (parameters, _) =>
                await _host.InstallTrainingModelCommand(parameters.Deserialize<TrainingGameParameters>()),
            ["PublishTrainingModel"] = async (parameters, client) =>
                await _host.PublishTrainingModel(parameters.Deserialize<PublishTrainingModelParameters>(), client),
            ["ListAvailableRecordingModels"] = (_, _) => Task.Run(_host.PushAvailableRecordingModels),
            ["ActivateRecordingModel"] = (parameters, _) =>
            {
                var parsed = parameters.Deserialize<TrainingGameParameters>();
                return Task.Run(() => _host.ActivateRecordingModel(parsed?.GameId));
            },
#endif
        };
    }

    internal async Task HandleAsync(string method, JsonElement? parameters, ClientHandle client)
    {
        if (_commands.TryGetValue(method, out var handler))
        {
            handler(parameters, client);
        }
        else if (_asyncCommands.TryGetValue(method, out var asyncHandler))
        {
            await asyncHandler(parameters, client);
        }
    }

    private void OnNewConnection()
    {
        _host.PushState(_host.IsRecording, _host.CurrentGameId);
        _host.PushSettings();
        _host.PushGameList();
        _host.PushModelStatus();
        _host.PushUpdateStatus();
        if (!_host.WindowVisible)
            _host.PushWindowVisibility();
    }

    private void CreateClip(JsonElement? parameters)
    {
        var parsed = parameters.Deserialize<CreateClipParameters>() ?? new CreateClipParameters();
        var request = BuildClipRequest(parsed, _host.EffectiveRoot, out var refusal);
        if (request is null)
        {
            _host.PushClipError(parsed.Id, refusal
                ?? $"That clip's source is not inside the recording folder, so it was not read: '{parsed.FilePath}'.");
            return;
        }

        request.ForceSdr = _host.ConvertHdrClipsToSdr;
        _host.CreateClip(request);
    }

    internal static ClipRequest? BuildClipRequest(CreateClipParameters parsed, string effectiveRoot) =>
        BuildClipRequest(parsed, effectiveRoot, out _);

    internal static ClipRequest? BuildClipRequest(CreateClipParameters parsed, string effectiveRoot,
        out string? refusal)
    {
        refusal = null;

        var mode = parsed.OutputMode.Equals("separate", StringComparison.OrdinalIgnoreCase)
            ? ClipMode.Separate
            : ClipMode.Combine;

        var wireSegments = parsed.Segments.Count > 0
            ? parsed.Segments.Select(segment => (segment.StartTime, segment.EndTime)).ToList()
            : [(parsed.StartTime, parsed.EndTime)];

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

        var outputPath = BuildClipOutputPath(parsed, effectiveRoot, sourcePath);

        return new ClipRequest
        {
            OperationId = parsed.Id,
            SourcePath = sourcePath,
            SourceSessionPath = ContentLayout.ToWirePath(effectiveRoot, sourcePath),
            Regions = regions,
            Mode = mode,
            OutputPath = outputPath,
            AudioTrackAdjustments = BuildAudioAdjustments(parsed),
            EncoderFamily = "libx264",
            Title = parsed.Title,
            Progress = null,
        };
    }

    internal static IReadOnlyList<AudioTrackAdjustment> BuildAudioAdjustments(CreateClipParameters parsed)
    {
        var muted = new HashSet<int>();
        foreach (var key in parsed.MutedAudioTracks ?? [])
        {
            if (TryTrackIndex(key, out var index))
                muted.Add(index);
        }

        var volumes = new Dictionary<int, double>();
        foreach (var (key, volume) in parsed.AudioTrackVolumes ?? [])
        {
            if (TryTrackIndex(key, out var index) && double.IsFinite(volume) && volume >= 0 && volume <= 1)
                volumes[index] = volume;
        }

        if (muted.Count == 0 && volumes.Count == 0)
            return [];

        return muted
            .Union(volumes.Keys)
            .OrderBy(index => index)
            .Select(index => new AudioTrackAdjustment(
                index,
                volumes.TryGetValue(index, out var volume) ? volume : 1.0,
                Muted: muted.Contains(index)))
            .ToList();
    }

    private static bool TryTrackIndex(string? key, out int index) =>
        int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0;

    internal static string BuildClipOutputPath(CreateClipParameters parameters, string effectiveRoot)
    {
        var sourcePath = ContentServer.ResolveWithinRoot(effectiveRoot, parameters.FilePath);
        return BuildClipOutputPath(parameters, effectiveRoot, sourcePath);
    }

    private static string BuildClipOutputPath(CreateClipParameters parameters, string effectiveRoot,
        string? sourcePath)
    {
        var outputDirectory = sourcePath is null
            ? Path.Combine(effectiveRoot, ContentLayout.Clips)
            : ContentLayout.SiblingOfSessions(effectiveRoot, sourcePath, ContentLayout.Clips);

        if (parameters.OutputMode.Equals("separate", StringComparison.OrdinalIgnoreCase))
        {
            return outputDirectory;
        }

        var sourceBaseName = Path.GetFileNameWithoutExtension(parameters.FilePath);
        return Path.Combine(outputDirectory, $"{sourceBaseName}-{SafeClipId(parameters.Id)}.mp4");
    }

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

        return safe.Length > 0 ? safe.ToString() : Guid.NewGuid().ToString("N");
    }

    private const int MaxClipIdLength = 64;
}

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
    internal static T? Deserialize<T>(this JsonElement? element) where T : class
    {
        if (element is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), Wire.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
