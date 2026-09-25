// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;

namespace Tript.GameDiscovery;

internal sealed class InventoryBuilder(GameStore store)
{
    private readonly List<InstalledGame> _games = [];
    private readonly List<SourceDiagnostic> _diagnostics = [];

    public void AddGame(string productId, string displayName, string installRoot, IEnumerable<string>? executables = null) =>
        AddGame(store, productId, displayName, installRoot, executables);

    public void AddGame(GameStore gameStore, string productId, string displayName, string installRoot,
        IEnumerable<string>? executables = null)
    {
        if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(displayName))
            return;
        _games.Add(new InstalledGame(gameStore, new ProductId(gameStore, productId.Trim()), displayName.Trim(),
            installRoot, executables?.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray() ?? []));
    }

    public void Warn(string code, string message, string? location = null) =>
        Warn(store, code, message, location);

    public void Warn(GameStore diagnosticStore, string code, string message, string? location = null) =>
        _diagnostics.Add(new(diagnosticStore, DiagnosticSeverity.Warning, code, message, location));

    public SourceInventory Build() => new(
        GameReconciliation.Merge(_games),
        _diagnostics.ToImmutableArray());
}

internal static class GameReconciliation
{
    public static ImmutableArray<InstalledGame> Merge(IEnumerable<InstalledGame> games)
    {
        var merged = new Dictionary<ProductId, InstalledGame>();
        foreach (var game in games)
        {
            if (!merged.TryGetValue(game.ProductId, out var existing))
            {
                merged.Add(game.ProductId, game);
                continue;
            }

            merged[game.ProductId] = existing with
            {
                DisplayName = Richer(existing.DisplayName, game.DisplayName),
                InstallRoot = Richer(existing.InstallRoot, game.InstallRoot),
                ExecutablePaths = existing.ExecutablePaths.Concat(game.ExecutablePaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray(),
            };
        }

        return merged.Values
            .OrderBy(game => game.Store)
            .ThenBy(game => game.ProductId.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static string Richer(string left, string right)
    {
        var leftValue = left?.Trim() ?? string.Empty;
        var rightValue = right?.Trim() ?? string.Empty;
        if (leftValue.Length != rightValue.Length)
            return leftValue.Length > rightValue.Length ? leftValue : rightValue;
        return StringComparer.OrdinalIgnoreCase.Compare(leftValue, rightValue) <= 0 ? leftValue : rightValue;
    }
}
