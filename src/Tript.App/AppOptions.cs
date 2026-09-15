// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
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

    public static AppOptions? Parse(string[] args)
    {
        string contentRoot = Tript.Settings.RecordingLocations.DefaultDirectory();
        var settingsPath = Settings.SettingsFilePaths.SettingsPath;
        string webRoot = DefaultWebRoot();
        var fakeRecorder = false;
        var disableUpdater = false;
        var startedByWindows = false;
        string? gameListJson = null;
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
                case "--ui-port" when index + 1 < args.Length && int.TryParse(args[++index], out uiPort):
                    break;
                case "--content-port" when index + 1 < args.Length && int.TryParse(args[++index], out contentPort):
                    break;
                case "--control-port" when index + 1 < args.Length && int.TryParse(args[++index], out controlPort):
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(
                        "Usage: Tript.App [--content-root <dir>] [--settings-path <file>] " +
                        "[--web-root <dir>] [--fake-recorder] [--disable-updater] [--startup] " +
                        "[--game-list <json>] [--ui-port <port>] [--content-port <port>] [--control-port <port>]");
                    return null;
                default:
                    Console.Error.WriteLine($"Tript.App: unknown argument '{args[index]}'.");
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
            UiPort = uiPort,
            ContentPort = contentPort,
            ControlPort = controlPort,
        };

        options.Validate();
        return options;
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

    internal static List<GameInfo> LoadCatalogue(Settings.Settings settings, GameCatalog catalog,
        string? overrideJson, out bool settingsMigrated, GameIdAliasStore? aliases = null)
    {
        settingsMigrated = MigrateLegacyGameIds(settings.Game.GameList, catalog, aliases);

        var games = new List<GameInfo>();
        var packagedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in catalog.Entries)
        {
            packagedIds.Add(entry.GameId);
            var setting = settings.Game.GameList.FirstOrDefault(game =>
                string.Equals(game.Id, entry.GameId, StringComparison.OrdinalIgnoreCase));
            games.Add(new GameInfo
            {
                Id = entry.GameId,
                Name = string.IsNullOrWhiteSpace(setting?.Name)
                    ? string.IsNullOrWhiteSpace(entry.Name) ? entry.GameId : entry.Name
                    : setting.Name,
                Executable = entry.Executable,
                BuiltIn = true,
                Detected = false,
            });
        }

        foreach (var setting in settings.Game.GameList)
        {
            if (packagedIds.Contains(setting.Id) || string.IsNullOrWhiteSpace(setting.Id))
                continue;

            games.Add(new GameInfo
            {
                Id = setting.Id,
                Name = string.IsNullOrWhiteSpace(setting.Name) ? setting.Id : setting.Name,
                Executable = setting.EffectiveExecutable,
                ExecutablePath = string.IsNullOrWhiteSpace(setting.ExecutablePath)
                    ? null
                    : setting.ExecutablePath.Trim(),
                BuiltIn = false,
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
            catch (JsonException)
            {
            }
        }

        return games;
    }

    private static bool MigrateLegacyGameIds(List<Settings.GameSetting> gameList, GameCatalog catalog,
        GameIdAliasStore? aliases)
    {
        var migrated = false;
        foreach (var game in gameList)
        {
            var current = aliases?.Resolve(game.Id) ?? catalog.ResolveLegacyGameId(game.Id);
            if (current is not null && !string.Equals(current, game.Id, StringComparison.OrdinalIgnoreCase))
            {
                game.Id = current;
                migrated = true;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        migrated |= gameList.RemoveAll(game => !string.IsNullOrWhiteSpace(game.Id) && !seen.Add(game.Id)) > 0;
        return migrated;
    }
}
