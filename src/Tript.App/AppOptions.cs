// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using System.Text.Json;
using Serilog;
using Tript.App.Models;

namespace Tript.App;

internal sealed class AppOptions
{
    public string ContentRoot { get; init; } = string.Empty;

    public string SettingsPath { get; init; } = Settings.SettingsFilePaths.SettingsPath;

    public string WebRoot { get; init; } = string.Empty;

    public bool FakeRecorder { get; init; }

    public bool DisableUpdater { get; init; }

    public bool StartedByWindows { get; init; }

    public int UiPort { get; init; } = LocalPorts.Ui;

    public int ContentPort { get; init; } = LocalPorts.Content;

    public int ControlPort { get; init; } = LocalPorts.ControlSocket;

    public string? GameListJson { get; init; }

    // Null means the per-user folder; tests must set it or they prune the real user's logs.
    public string? LogDirectory { get; init; }

    public bool VerboseLog { get; init; }

    public static AppOptions? Parse(string[] args) => Parse(args, Console.Error);

    internal static AppOptions? Parse(string[] args, TextWriter errors)
    {
        string contentRoot = Tript.Settings.RecordingLocations.DefaultDirectory();
        var settingsPath = Settings.SettingsFilePaths.SettingsPath;
        string webRoot = DefaultWebRoot();
        var fakeRecorder = false;
        var disableUpdater = false;
        var startedByWindows = false;
        string? gameListJson = null;
        string? logDirectory = null;
        var verboseLog = false;
        var uiPort = LocalPorts.Ui;
        var contentPort = LocalPorts.Content;
        var controlPort = LocalPorts.ControlSocket;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--content-root" when index + 1 < args.Length:
                    contentRoot = args[++index];
                    break;
                case "--settings-path" when index + 1 < args.Length:
                    settingsPath = args[++index];
                    break;
                case "--web-root" when index + 1 < args.Length:
                    webRoot = args[++index];
                    break;
                case "--fake-recorder":
                    fakeRecorder = true;
                    break;
                case "--disable-updater":
                    disableUpdater = true;
                    break;
                case "--startup":
                    startedByWindows = true;
                    break;
                case "--game-list" when index + 1 < args.Length:
                    gameListJson = args[++index];
                    break;
                case "--log-dir" when index + 1 < args.Length:
                    logDirectory = args[++index];
                    break;
                case "--verbose-log":
                    verboseLog = true;
                    break;
                case "--ui-port" when index + 1 < args.Length:
                    if (!TryParsePort(args[index], args[++index], out uiPort, errors))
                        return null;
                    break;
                case "--content-port" when index + 1 < args.Length:
                    if (!TryParsePort(args[index], args[++index], out contentPort, errors))
                        return null;
                    break;
                case "--control-port" when index + 1 < args.Length:
                    if (!TryParsePort(args[index], args[++index], out controlPort, errors))
                        return null;
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(
                        "Usage: Tript.App [--content-root <dir>] [--settings-path <file>] " +
                        "[--web-root <dir>] [--fake-recorder] [--disable-updater] [--startup] " +
                        "[--game-list <json>] [--log-dir <dir>] [--verbose-log] [--ui-port <port>] [--content-port <port>] "
                        + "[--control-port <port>]");
                    return null;
                default:
                    errors.WriteLine($"Tript.App: unknown argument '{args[index]}'.");
                    return null;
            }
        }

        var options = new AppOptions
        {
            ContentRoot = contentRoot,
            SettingsPath = settingsPath,
            WebRoot = webRoot,
            FakeRecorder = fakeRecorder,
            DisableUpdater = disableUpdater,
            StartedByWindows = startedByWindows,
            GameListJson = gameListJson,
            LogDirectory = logDirectory,
            VerboseLog = verboseLog,
            UiPort = uiPort,
            ContentPort = contentPort,
            ControlPort = controlPort,
        };

        options.Validate();
        return options;
    }

    private static bool TryParsePort(string option, string value, out int port, TextWriter errors)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port))
            return true;

        errors.WriteLine($"Tript.App: invalid value '{value}' for {option}; expected a port number.");
        return false;
    }

    internal static string DefaultWebRoot()
    {
        var published = Path.Combine(AppContext.BaseDirectory, "dist");
        if (Directory.Exists(published))
            return published;

        var dev = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Tript.Web", "dist");
        return dev;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(ContentRoot))
            throw new ArgumentException("The content root must not be empty.");

        var ports = new[] { UiPort, ContentPort, ControlPort };
        if (ports.Any(port => port is < 1 or > 65535) || ports.Distinct().Count() != ports.Length)
            throw new ArgumentException("The IPC ports must be distinct values between 1 and 65535.");
    }

    internal static List<GameInfo> LoadCatalogue(Settings.Settings settings, string? overrideJson,
        out bool settingsMigrated, GameIdAliasStore aliases)
    {
        settingsMigrated = MigrateLegacyGameIds(settings.Game.GameList, aliases);

        var games = new List<GameInfo>();
        foreach (var setting in settings.Game.GameList)
        {
            if (string.IsNullOrWhiteSpace(setting.Id))
                continue;

            games.Add(new GameInfo
            {
                Id = setting.Id,
                Name = string.IsNullOrWhiteSpace(setting.Name) ? setting.Id : setting.Name,
                Executable = setting.EffectiveExecutable,
                ExecutablePath = string.IsNullOrWhiteSpace(setting.ExecutablePath)
                    ? null
                    : setting.ExecutablePath.Trim(),
                Detected = false,
            });
        }

        if (!string.IsNullOrWhiteSpace(overrideJson))
        {
            try
            {
                var extra = JsonSerializer.Deserialize<List<GameInfo>>(overrideJson);
                if (extra is not null)
                    games.AddRange(extra);
            }
            catch (JsonException exception)
            {
                Log.Warning("the --game-list value is not valid JSON and was ignored: {Reason}", exception.Message);
            }
        }

        return games;
    }

    private static bool MigrateLegacyGameIds(List<Settings.GameSetting> gameList, GameIdAliasStore aliases)
    {
        var migrated = false;
        var fromLegacyDefault = new HashSet<Settings.GameSetting>();
        foreach (var game in gameList)
        {
            var current = aliases.Resolve(game.Id);
            if (current is not null && !string.Equals(current, game.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (LegacyGameIds.Map.ContainsKey(game.Id))
                    fromLegacyDefault.Add(game);
                game.Id = current;
                migrated = true;
            }
        }

        migrated |= gameList.RemoveAll(game => fromLegacyDefault.Contains(game) && IsUntouched(game)) > 0;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        migrated |= gameList.RemoveAll(game => !string.IsNullOrWhiteSpace(game.Id) && !seen.Add(game.Id)) > 0;
        return migrated;
    }

    private static bool IsUntouched(Settings.GameSetting game) =>
        string.IsNullOrWhiteSpace(game.ExecutablePath)
        && string.IsNullOrWhiteSpace(game.Executable)
        && string.IsNullOrWhiteSpace(game.IconId)
        && game.AutoRecordOverride is null
        && game.RecordingModeOverride is null
        && game.QualityOverride is null
        && game.CaptureMethodOverride is null
        && game.AutomaticClipOverride is null
        && game.UnknownProperties.Count == 0;
}
