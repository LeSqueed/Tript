// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;

namespace Tript.App;

// The app host's command-line surface. Real invocations run with defaults; the smoke tests pass a
// temp content root and a temp settings file so nothing touches the developer's real config
// directory, and --fake-recorder for the seam-level protocol tests that must not start libobs.
internal sealed class AppOptions
{
    public string ContentRoot { get; init; } = string.Empty;

    public string SettingsPath { get; init; } = Settings.SettingsFilePaths.SettingsPath;

    public string WebRoot { get; init; } = string.Empty;

    // The recording path. When false the host wires a real ObsRecorderSession over the started
    // runtime; when true it wires a fake recorder session so the IPC and protocol layers run
    // without libobs (or a display server, or the muxer helper) present.
    public bool FakeRecorder { get; init; }

    // The game-list source. When null the host uses its own catalogue; the tests override this to
    // push a deterministic game list into the gameList broadcast.
    public string? GameListJson { get; init; }

    public static AppOptions? Parse(string[] args)
    {
        string contentRoot = Path.Combine(Path.GetTempPath(), "tript-app", "content");
        var settingsPath = Settings.SettingsFilePaths.SettingsPath;
        string webRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Tript.Web", "dist");
        var fakeRecorder = false;
        string? gameListJson = null;

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
                case "--game-list" when index + 1 < args.Length:
                    gameListJson = args[++index];
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(
                        "Usage: Tript.App [--content-root <dir>] [--settings-path <file>] " +
                        "[--web-root <dir>] [--fake-recorder] [--game-list <json>]");
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
            GameListJson = gameListJson,
        };

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(ContentRoot))
            throw new ArgumentException("The content root must not be empty.");
    }

    // The game list pushed on every NewConnection and broadcast when it changes. The alpha has no
    // remote catalogue feed (spec/games-catalogue.md), so the catalogue is whatever is in the
    // settings' game list plus an optional test override.
    internal static List<GameInfo> LoadCatalogue(Settings.Settings settings, string? overrideJson)
    {
        var games = new List<GameInfo>();
        foreach (var game in settings.Game.GameList)
        {
            games.Add(new GameInfo
            {
                Id = game.Id,
                Name = game.Name,
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
                // A malformed override is a test harness problem, not a crash.
            }
        }

        return games;
    }
}
