// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using Tript.Core;

namespace Tript.GameDiscovery;

public enum GameStore
{
    Steam,
    Epic,
    EA,
    Ubisoft,
    Xbox,
}

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public readonly record struct ProductId(GameStore Store, string Value)
{
    public bool Equals(ProductId other) =>
        Store == other.Store && StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    public override int GetHashCode() =>
        HashCode.Combine(Store, StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty));

    public override string ToString() => $"{Store.ToString().ToLowerInvariant()}:{Value}";
}

public sealed record InstalledGame(
    GameStore Store,
    ProductId ProductId,
    string DisplayName,
    string InstallRoot,
    ImmutableArray<string> ExecutablePaths)
{
    public bool TryResolveCatalogueExecutable(
        IDiscoveryFileSystem fileSystem,
        string filenameOrRelativePath,
        out string executablePath)
    {
        executablePath = string.Empty;
        return !string.IsNullOrWhiteSpace(filenameOrRelativePath)
            && !FilePaths.IsFullyQualified(filenameOrRelativePath)
            && PathSafety.TryResolveLexicallyContained(fileSystem, InstallRoot, filenameOrRelativePath, out var candidate)
            && fileSystem.FileExists(candidate)
            && (executablePath = candidate).Length > 0;
    }
}

public sealed record SourceDiagnostic(
    GameStore Store,
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Location = null);

public sealed record SourceInventory(
    ImmutableArray<InstalledGame> Games,
    ImmutableArray<SourceDiagnostic> Diagnostics)
{
    public static SourceInventory Empty { get; } = new([], []);
}

public sealed record GameInventory(
    ImmutableArray<InstalledGame> Games,
    ImmutableArray<SourceDiagnostic> Diagnostics);
