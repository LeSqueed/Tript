// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Models;

internal static class GameModelPaths
{
    internal static string DataRoot
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = AppContext.BaseDirectory;
            return Path.Combine(root, "Tript");
        }
    }

    internal static string ModelsRoot => Path.Combine(DataRoot, "models");

    internal static string ManifestPath => Path.Combine(DataRoot, "model-manifest.json");

    internal static string ManifestStatePath => Path.Combine(DataRoot, "model-manifest-state.json");

    internal static string GamePath(string gameId) => Path.Combine(ModelsRoot, ValidateGameId(gameId));

    internal static string InstalledManifestPath(string gameId) => Path.Combine(GamePath(gameId), "installed.json");

    internal static string ValidateGameId(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        if (gameId is "." or ".." || gameId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            gameId.Contains(Path.DirectorySeparatorChar) || gameId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The game ID must be a single safe path segment.", nameof(gameId));
        }
        return gameId;
    }
}
