// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;

namespace Tript.GameDiscovery;

public sealed class XboxPackageInventorySource(
    IDiscoveryFileSystem fileSystem,
    IXboxPackageProvider packages) : IGameInventorySource
{
    public GameStore Store => GameStore.Xbox;

    public ValueTask<SourceInventory> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = new InventoryBuilder(Store);
        foreach (var package in packages.GetInstalledPackages(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PathSafety.TryCanonicalize(fileSystem, package.InstallRoot, out var root))
            {
                result.Warn("xbox.package", "Package has an invalid install root.", package.InstallRoot);
                continue;
            }

            var executables = new List<string>();
            foreach (var declared in package.DeclaredExecutablePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PathSafety.TryResolveLexicallyContained(fileSystem, root, declared, out var executable))
                    result.Warn("xbox.package.executable", "Executable is not lexically contained by the install root.", root);
                else if (!fileSystem.FileExists(executable))
                    result.Warn("xbox.package.executable", "Executable does not identify an existing regular file.", executable);
                else
                    executables.Add(executable);
            }

            result.AddGame(package.ProductId, package.DisplayName, root, executables);
        }

        return ValueTask.FromResult(result.Build());
    }
}

[SupportedOSPlatform("windows10.0.10240")]
public sealed class WindowsXboxPackageProvider : IXboxPackageProvider
{
    public IEnumerable<XboxPackage> GetInstalledPackages(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Xbox package discovery requires Windows 10 or later.");

        var managerType = Type.GetType(
            "Windows.Management.Deployment.PackageManager, Windows, ContentType=WindowsRuntime",
            throwOnError: false)
            ?? throw new PlatformNotSupportedException("The Windows package deployment API is unavailable.");
        var manager = Activator.CreateInstance(managerType)
            ?? throw new PlatformNotSupportedException("The Windows package deployment API could not be initialized.");
        var findPackages = managerType.GetMethod("FindPackagesForUser", [typeof(string)])
            ?? throw new PlatformNotSupportedException("PackageManager.FindPackagesForUser is unavailable.");

        System.Collections.IEnumerable installed;
        try
        {
            installed = (System.Collections.IEnumerable)findPackages.Invoke(manager, [string.Empty])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw ex.InnerException;
        }

        foreach (var package in installed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Property(Property(package, "InstalledLocation"), "Path") as string;
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var config = Path.Combine(root, "MicrosoftGame.config");
            if (!File.Exists(config))
                continue;

            XboxPackage? game = null;
            try
            {
                var document = XDocument.Load(config, LoadOptions.None);
                var identity = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Identity");
                var visuals = document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName is "ShellVisuals" or "VisualElements");
                var id = identity?.Attribute("Name")?.Value ?? Property(Property(package, "Id"), "Name") as string;
                var name = visuals?.Attribute("DefaultDisplayName")?.Value ?? id;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    var executables = document.Descendants()
                        .Where(element => element.Name.LocalName == "Executable"
                            && element.Ancestors().Any(parent => parent.Name.LocalName == "ExecutableList"))
                        .Select(element => element.Attribute("Name")?.Value)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Cast<string>()
                        .ToArray();
                    game = new(id, name, root, executables);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                continue;
            }

            if (game is not null)
                yield return game;
        }
    }

    private static object? Property(object? value, string name) =>
        value?.GetType().GetProperty(name)?.GetValue(value);
}
