// SPDX-License-Identifier: GPL-2.0-or-later

using System.Xml;
using System.Xml.Linq;
using System.Security;

namespace Tript.GameDiscovery;

public sealed class XboxInventorySource(IDiscoveryFileSystem fileSystem, IFixedDriveProvider drives) : IGameInventorySource
{
    public GameStore Store => GameStore.Xbox;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        IEnumerable<string> roots;
        try { roots = drives.GetFixedDriveRoots().ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            result.Warn("xbox.drives", ex.Message);
            return ValueTask.FromResult(result.Build());
        }

        foreach (var drive in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xboxGames = Path.Combine(drive, "XboxGames");
            if (!fileSystem.DirectoryExists(xboxGames)) continue;
            IEnumerable<string> installs;
            try { installs = fileSystem.EnumerateDirectories(xboxGames).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warn("xbox.enumerate", ex.Message, xboxGames); continue;
            }
            foreach (var install in installs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rootConfig = Path.Combine(install, "MicrosoftGame.config");
                var contentRoot = Path.Combine(install, "Content");
                var contentConfig = Path.Combine(contentRoot, "MicrosoftGame.config");
                if (fileSystem.FileExists(rootConfig)) ParseConfig(install, rootConfig, result, cancellationToken);
                else if (fileSystem.FileExists(contentConfig)) ParseConfig(contentRoot, contentConfig, result, cancellationToken);
            }
        }
        return ValueTask.FromResult(result.Build());
    }

    private void ParseConfig(string install, string config, InventoryBuilder result, CancellationToken cancellationToken)
    {
        try
        {
            var document = XDocument.Parse(fileSystem.ReadAllText(config), LoadOptions.None);
            var identity = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
            var visuals = document.Descendants().FirstOrDefault(e => e.Name.LocalName is "ShellVisuals" or "VisualElements");
            var id = identity?.Attribute("Name")?.Value;
            var name = visuals?.Attribute("DefaultDisplayName")?.Value ?? id;
            if (id is null || name is null || !PathSafety.TryCanonicalize(fileSystem, install, out var root))
            {
                result.Warn("xbox.config", "Config is missing Identity Name or display name.", config); return;
            }
            var executables = new List<string>();
            foreach (var element in document.Descendants().Where(e =>
                e.Name.LocalName == "Executable" &&
                e.Ancestors().Any(parent => parent.Name.LocalName == "ExecutableList")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var declared = element.Attribute("Name")?.Value;
                if (declared is null) continue;
                if (!PathSafety.TryResolveLexicallyContained(fileSystem, root, declared, out var executable))
                    result.Warn("xbox.executable", "Executable is not lexically contained by the install root.", config);
                else if (!fileSystem.FileExists(executable))
                    result.Warn("xbox.executable", "Executable does not identify an existing regular file.", config);
                else
                    executables.Add(executable);
            }
            result.AddGame(id, name, root, executables);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            result.Warn("xbox.config", ex.Message, config);
        }
    }
}
