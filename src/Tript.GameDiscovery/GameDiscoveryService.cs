// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security;

namespace Tript.GameDiscovery;

public sealed class GameDiscoveryService(IEnumerable<IGameInventorySource> sources)
{
    private readonly ImmutableArray<IGameInventorySource> _sources = sources.ToImmutableArray();

    public async ValueTask<GameInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var games = ImmutableArray.CreateBuilder<InstalledGame>();
        var diagnostics = ImmutableArray.CreateBuilder<SourceDiagnostic>();
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var inventory = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                games.AddRange(inventory.Games);
                diagnostics.AddRange(inventory.Diagnostics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
            {
                diagnostics.Add(new(source.Store, DiagnosticSeverity.Error, "source.failed", ex.Message));
            }
        }
        return new(GameReconciliation.Merge(games), diagnostics.ToImmutable());
    }

    [SupportedOSPlatform("windows")]
    public static GameDiscoveryService CreateDefault(IXboxPackageProvider? xboxPackages = null)
    {
        var fileSystem = new PhysicalDiscoveryFileSystem();
        var registry = new WindowsDiscoveryRegistry();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var epicLocations = new[]
        {
            Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests"),
            Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat"),
        };
        var sources = new List<IGameInventorySource>
        {
            new SteamInventorySource(fileSystem, registry),
            new EpicInventorySource(fileSystem, epicLocations),
            new EaInventorySource(fileSystem, registry),
            new UbisoftInventorySource(fileSystem, registry),
            new XboxInventorySource(fileSystem, new FixedDriveProvider()),
        };
        if (xboxPackages is not null)
            sources.Add(new XboxPackageInventorySource(fileSystem, xboxPackages));
        return new(sources);
    }

    [SupportedOSPlatform("linux")]
    public static GameDiscoveryService CreateLinuxDefault(string? homeDirectory = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return new([]);
        var fileSystem = new PhysicalDiscoveryFileSystem();
        return new([
            SteamInventorySource.ForLinuxHome(fileSystem, home),
            new HeroicInventorySource(fileSystem, home),
            new LutrisInventorySource(fileSystem, home),
        ]);
    }
}
