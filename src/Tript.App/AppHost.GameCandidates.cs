// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Serilog;
using Tript.App.Resolver;
using Tript.Core;
using Tript.GameDiscovery;
using Tript.Recorder;
using Tript.Settings;

namespace Tript.App;

internal sealed partial class AppHost
{
    private readonly object _candidateResolutionGate = new();

    private readonly HashSet<string> _resolvingCandidatePaths = new(FilePaths.Comparer);

    internal void OnFullscreenCandidateFound(FullscreenGameCandidate candidate)
    {
        var normalized = ProcessNameGameDetector.NormalizePath(candidate.ExecutablePath);
        if (normalized is null)
            return;

        if (_settingsStore.Load().Game.IgnoredApplications?.Contains(normalized, FilePaths.Comparer) == true)
            return;

        if (_resolverClient is not null)
        {
            lock (_candidateResolutionGate)
            {
                if (!_resolvingCandidatePaths.Add(normalized))
                    return;
            }
            _ = ResolveGameCandidateAsync(candidate, normalized);
            return;
        }

        PushGameCandidate(candidate, normalized,
            CandidateResolverInput(candidate.Executable, normalized, _gameInventory.Inventory).Name);
    }

    private void PushGameCandidate(FullscreenGameCandidate candidate, string normalized, string? name)
    {
        _ipc.Broadcast("gameCandidate", JsonSerializer.SerializeToElement(new
        {
            pid = candidate.ProcessId,
            executable = candidate.Executable,
            executablePath = normalized,
            name,
        }, Wire.Options));
    }

    private async Task ResolveGameCandidateAsync(FullscreenGameCandidate candidate, string normalized)
    {
        string? libraryName = null;
        try
        {
            await _gameInventory.CurrentScan.WaitAsync(_discoveryCancellation.Token).ConfigureAwait(false);
            var inventory = _gameInventory.Inventory;
            var resolution = CandidateResolverInput(candidate.Executable, normalized, inventory);
            libraryName = resolution.Name;
            var resolved = resolution.StoreBacked
                ? await _resolverClient!.ResolveAsync(resolution.Input, resolution.Name,
                    _discoveryCancellation.Token)
                : await _resolverClient!.ResolveAsync(
                    $"name:{Path.GetFileNameWithoutExtension(candidate.Executable)}",
                    _discoveryCancellation.Token);
            if (_disposed || _shuttingDown)
                return;

            // Outside a store install the executable's name is the only evidence, so it may only move
            // a game the user already has; it never adds one.
            if (!resolution.StoreBacked && (!resolved.Canonical || IsCustomGameId(resolved.GameId)))
            {
                PushGameCandidate(candidate, normalized, libraryName);
                return;
            }

            var displayName = string.IsNullOrWhiteSpace(resolved.DisplayName)
                ? Path.GetFileNameWithoutExtension(candidate.Executable)
                : resolved.DisplayName;
            switch (ApplyResolvedCandidate(resolved.GameId, displayName, normalized,
                        addIfMissing: resolution.StoreBacked))
            {
                case ResolvedCandidateOutcome.Added:
                    PushGameAdded(resolved.GameId, displayName, normalized);
                    break;
                case ResolvedCandidateOutcome.NotApplied:
                    PushGameCandidate(candidate, normalized, libraryName);
                    break;
            }
        }
        catch (OperationCanceledException) when (_discoveryCancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException exception)
        {
            Log.Warning(exception, "AppHost: game candidate resolution timed out for {Executable}",
                candidate.Executable);
            PushGameCandidate(candidate, normalized, libraryName);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            Log.Warning(exception, "AppHost: game candidate resolution failed for {Executable}", candidate.Executable);
            PushGameCandidate(candidate, normalized, libraryName);
        }
        finally
        {
            lock (_candidateResolutionGate)
                _resolvingCandidatePaths.Remove(normalized);
        }
    }

    private enum ResolvedCandidateOutcome
    {
        NotApplied,
        Added,
        Updated,
    }

    private ResolvedCandidateOutcome ApplyResolvedCandidate(string gameId, string displayName, string normalized,
        bool addIfMissing)
    {
        var outcome = ResolvedCandidateOutcome.NotApplied;
        lock (_settingsUpdateGate)
        {
            var saved = _settingsStore.TryUpdate(settings =>
            {
                var game = settings.Game.GameList.FirstOrDefault(value =>
                    string.Equals(value.Id, gameId, StringComparison.OrdinalIgnoreCase));
                if (game is null)
                {
                    if (!addIfMissing)
                        return null;

                    settings.Game.GameList.Add(new GameSetting
                    {
                        Id = gameId,
                        Name = displayName,
                        ExecutablePath = normalized,
                    });
                    outcome = ResolvedCandidateOutcome.Added;
                }
                else
                {
                    if (!FilePaths.Comparer.Equals(game.ExecutablePath?.Trim() ?? string.Empty, normalized))
                        game.ExecutablePath = normalized;
                    outcome = ResolvedCandidateOutcome.Updated;
                }

                return ValidateGameList(settings.Game.GameList, out var validationError)
                    ? null
                    : validationError;
            }, out _, out var failure);
            if (!saved)
            {
                Log.Warning("AppHost: resolved game candidate was not saved: {Reason}", failure);
                return ResolvedCandidateOutcome.NotApplied;
            }

            if (outcome == ResolvedCandidateOutcome.NotApplied)
                return outcome;

            ReloadGameList();
            RebuildDetectionTargets();
            PushGameList();
            PushSettings();
        }

        return outcome;
    }

    private void PushGameAdded(string gameId, string name, string executablePath)
    {
        _ipc.Broadcast("gameAdded", JsonSerializer.SerializeToElement(new
        {
            gameId,
            name,
            executablePath,
        }, Wire.Options));
    }

    internal sealed record CandidateResolution(string Input, bool StoreBacked, string? Name = null);

    internal static CandidateResolution CandidateResolverInput(string executable, string normalized,
        GameInventory inventory)
    {
        var containing = inventory.Games
            .Where(game => FilePaths.IsUnder(normalized, game.InstallRoot))
            .OrderByDescending(game => game.InstallRoot.Length)
            .ToArray();
        var storeBacked = containing.FirstOrDefault(IsResolvableByStore);
        var name = (storeBacked ?? containing.FirstOrDefault())?.DisplayName;
        return storeBacked is not null
            ? new CandidateResolution(storeBacked.ProductId.ToString(), true, name)
            : new CandidateResolution($"executable:{ExecutableNames.Normalize(executable)}", false, name);
    }

    private static bool IsResolvableByStore(InstalledGame game) =>
        game.Store != GameStore.Ubisoft && game.Store.HasStoreProductIdentity();

    private void OnFullscreenCandidateCleared(FullscreenGameCandidate candidate)
    {
        var normalized = ProcessNameGameDetector.NormalizePath(candidate.ExecutablePath);
        if (normalized is null)
            return;

        _ipc.Broadcast("gameCandidateCleared", JsonSerializer.SerializeToElement(new
        {
            executablePath = normalized,
        }, Wire.Options));
    }

    internal void IgnoreGameCandidate(string? executablePath, string? requestId = null)
    {
        var normalized = ProcessNameGameDetector.NormalizePath(executablePath);
        if (normalized is null)
        {
            PushGameCandidateActionResult(requestId, executablePath ?? string.Empty, "ignore", false,
                "The executable path is invalid.");
            return;
        }

        lock (_settingsUpdateGate)
        {
            var saved = _settingsStore.TryUpdate(settings =>
            {
                settings.Game.IgnoredApplications ??= [];
                if (!settings.Game.IgnoredApplications.Contains(normalized, FilePaths.Comparer))
                    settings.Game.IgnoredApplications.Add(normalized);
                return null;
            }, out _, out var failure);

            if (!saved)
            {
                var error = $"That application was not ignored: {failure ?? "the settings file could not be written."}";
                PushError(error);
                PushGameCandidateActionResult(requestId, normalized, "ignore", false, error);
                return;
            }
        }

        PushSettings();
        PushGameCandidateActionResult(requestId, normalized, "ignore", true, null);
    }

    internal void AddGameCandidate(string? name, string? executablePath, string? requestId = null)
    {
        lock (_settingsUpdateGate)
            AddGameCandidateLocked(name, executablePath, requestId);
    }

    private void AddGameCandidateLocked(string? name, string? executablePath, string? requestId)
    {
        var normalized = NormalizePickedExecutable(executablePath);
        if (normalized is null)
        {
            const string error = "That executable no longer exists; select it again before adding the game.";
            PushError(error);
            PushGameCandidateActionResult(requestId, executablePath ?? string.Empty, "add", false, error);
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(normalized)
            : name.Trim();
        var saved = _settingsStore.TryUpdate(settings =>
        {
            settings.Game.GameList.Add(new GameSetting
            {
                Id = $"custom-{Guid.NewGuid():N}",
                Name = displayName,
                ExecutablePath = normalized,
            });
            return ValidateGameList(settings.Game.GameList, out var validationError)
                ? null
                : validationError;
        }, out _, out var failure);

        if (!saved)
        {
            var error = $"That game was not added: {failure ?? "the settings file could not be written."}";
            PushError(error);
            PushSettings();
            PushGameCandidateActionResult(requestId, normalized, "add", false, error);
            return;
        }

        ReloadGameList();
        RebuildDetectionTargets();
        PushGameList();
        PushSettings();
        PushGameCandidateActionResult(requestId, normalized, "add", true, null);
    }

    private void PushGameCandidateActionResult(string? requestId, string executablePath, string action,
        bool success, string? error)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        _ipc.Broadcast("gameCandidateActionResult", JsonSerializer.SerializeToElement(new
        {
            requestId,
            executablePath,
            action,
            success,
            error,
        }, Wire.Options));
    }
}
