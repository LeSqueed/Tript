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

    // Set by the Windows per-user startup entry so the shell can distinguish a login launch from a
    // user opening the executable directly when those behaviors diverge later.
    public bool StartedByWindows { get; init; }

    public int UiPort { get; init; } = LocalPorts.Ui;

    public int ContentPort { get; init; } = LocalPorts.Content;

    public int ControlPort { get; init; } = LocalPorts.ControlSocket;

    // The game-list source. When null the host uses its own catalogue; the tests override this to
    // push a deterministic game list into the gameList broadcast.
    public string? GameListJson { get; init; }

    public static AppOptions? Parse(string[] args)
    {
        // The content root doubles as the default recording location: recordings live under
        // <contentRoot>/sessions/<date>/ and the UI lists and streams them from there. On a fresh
        // install the default is the platform recordings directory (Videos/Tript), so sessions land
        // somewhere the user can find them; --content-root overrides it for tests and headless runs.
        string contentRoot = Tript.Settings.RecordingLocations.DefaultDirectory();
        var settingsPath = Settings.SettingsFilePaths.SettingsPath;
        string webRoot = DefaultWebRoot();
        var fakeRecorder = false;
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
                        "[--web-root <dir>] [--fake-recorder] [--startup] [--game-list <json>] " +
                        "[--ui-port <port>] [--content-port <port>] [--control-port <port>]");
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
            StartedByWindows = startedByWindows,
            GameListJson = gameListJson,
            UiPort = uiPort,
            ContentPort = contentPort,
            ControlPort = controlPort,
        };

        options.Validate();
        return options;
    }

    // The default web root. In a published layout the built frontend ships as ./dist next to the
    // executable (the Makefile assembles it there); in a dev checkout the source tree carries it at
    // <repo>/src/Tript.Web/dist. Prefer the published layout when it exists, else the dev path.
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

    // The game list pushed on every NewConnection and broadcast when it changes. The alpha has no
    // remote catalogue feed, so the catalogue is whatever is in the
    // settings' game list plus an optional test override.
    internal static List<GameInfo> LoadCatalogue(Settings.Settings settings, string? overrideJson)
    {
        var games = new List<GameInfo>();
        foreach (var game in settings.Game.GameList)
        {
            var executable = game.EffectiveExecutable;
            games.Add(new GameInfo
            {
                Id = game.Id,
                Name = game.Name,
                Executable = string.IsNullOrWhiteSpace(executable) ? null : executable,
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
