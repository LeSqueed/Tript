// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tript.App.Updater;
using Xunit;

namespace Tript.App.Tests;

public sealed class UpdateManagerTests : IDisposable
{
    private const string ReleasesUrl = "https://api.test/releases";
    private const string ZipUrl = "https://api.test/assets/Tript-0.1.0-alpha.3-win-x64.zip";
    private const string ShaUrl = "https://api.test/assets/Tript-0.1.0-alpha.3-win-x64.zip.sha256";

    private readonly string _installRoot = Path.Combine(Path.GetTempPath(),
        "tript-update-manager-" + Guid.NewGuid().ToString("N"));

    public UpdateManagerTests() => Directory.CreateDirectory(_installRoot);

    [Fact]
    public async Task NewerCompatibleRelease_IsVerifiedStagedAndMarked()
    {
        var zipBytes = BuildAppZip("Release-win");
        var routes = ReleaseRoutes(zipBytes, ValidHashOf(zipBytes));
        var handler = new RouteHandler(routes);
        using var manager = CreateManager(handler);

        await manager.CheckAsync(manual: true, CancellationToken.None);

        var status = manager.Snapshot();
        Assert.Equal("ready", status.Stage);
        Assert.Equal("0.1.0-alpha.3", status.Version);

        var marker = UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot));
        Assert.NotNull(marker);
        var stagedFolder = UpdateStagingPaths.StagedFolderPath(_installRoot, marker!.StagedFolderName);
        Assert.True(File.Exists(Path.Combine(stagedFolder, "Tript.Shell.exe")));
        Assert.True(File.Exists(Path.Combine(stagedFolder, "dist", "index.html")));
        Assert.False(File.Exists(Path.Combine(stagedFolder, "Tript.exe")));
    }

    [Fact]
    public async Task ChecksumMismatch_LeavesNoStagedFilesAndReportsError()
    {
        var zipBytes = BuildAppZip("Release-win");
        var routes = ReleaseRoutes(zipBytes, new string('0', 64));
        using var manager = CreateManager(new RouteHandler(routes));

        await manager.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal("error", manager.Snapshot().Stage);
        Assert.Null(UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot)));
        AssertNoStagedLeftovers();
    }

    [Fact]
    public async Task ZipSlipEntry_IsRejectedDuringExtraction()
    {
        var zipBytes = BuildAppZip("Release-win", ("Release-win/App/../../evil.txt", "evil"u8.ToArray()));
        var routes = ReleaseRoutes(zipBytes, ValidHashOf(zipBytes));
        using var manager = CreateManager(new RouteHandler(routes));

        await manager.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal("error", manager.Snapshot().Stage);
        Assert.False(File.Exists(Path.Combine(_installRoot, "evil.txt")));
        AssertNoStagedLeftovers();
    }

    [Fact]
    public async Task ReleaseWithoutWindowsAsset_IsReportedAvailableNotError()
    {
        var routes = new Dictionary<string, byte[]>
        {
            [ReleasesUrl] = ReleaseListJson("v0.1.0-alpha.3", assets: []),
        };
        using var manager = CreateManager(new RouteHandler(routes));

        await manager.CheckAsync(manual: true, CancellationToken.None);

        var status = manager.Snapshot();
        Assert.Equal("available", status.Stage);
        Assert.Equal("0.1.0-alpha.3", status.Version);
    }

    [Fact]
    public async Task AlreadyUpToDate_ReportsUpToDateWithoutDownloading()
    {
        var routes = new Dictionary<string, byte[]>
        {
            [ReleasesUrl] = ReleaseListJson("v0.1.0-alpha.1", assets: [WindowsZipAsset(0), WindowsShaAsset()]),
        };
        var handler = new RouteHandler(routes);
        using var manager = CreateManager(handler, currentVersion: "0.1.0-alpha.1");

        await manager.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal("upToDate", manager.Snapshot().Stage);
        Assert.DoesNotContain(ZipUrl, handler.Requests);
    }

    [Fact]
    public async Task NonWindowsPlatform_StopsAtAvailableWithoutDownloading()
    {
        var zipBytes = BuildAppZip("Release-win");
        var routes = ReleaseRoutes(zipBytes, ValidHashOf(zipBytes));
        var handler = new RouteHandler(routes);
        using var manager = CreateManager(handler, supportsAutomaticApply: false);

        await manager.CheckAsync(manual: true, CancellationToken.None);

        var status = manager.Snapshot();
        Assert.Equal("available", status.Stage);
        Assert.DoesNotContain(ZipUrl, handler.Requests);
    }

    [Fact]
    public async Task AlreadyStagedSameVersion_SkipsRedownloadOnASubsequentCheck()
    {
        var zipBytes = BuildAppZip("Release-win");
        var routes = ReleaseRoutes(zipBytes, ValidHashOf(zipBytes));
        var handler = new RouteHandler(routes);
        using var manager = CreateManager(handler);

        await manager.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal("ready", manager.Snapshot().Stage);
        var firstDownloadCount = handler.Requests.Count(url => url == ZipUrl);

        await manager.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal("ready", manager.Snapshot().Stage);
        Assert.Equal(firstDownloadCount, handler.Requests.Count(url => url == ZipUrl));
    }

    [Fact]
    public void SweepLeftovers_ClearsAMarkerWhoseVersionTheRunningAppAlreadyReached()
    {
        var stagedFolder = UpdateStagingPaths.StagedFolderPath(_installRoot, "staged-old");
        Directory.CreateDirectory(stagedFolder);
        File.WriteAllText(Path.Combine(stagedFolder, "Tript.Shell.exe"), "shell");
        UpdateMarker.WriteAtomic(UpdateStagingPaths.MarkerPath(_installRoot),
            new UpdateMarker(UpdateMarker.CurrentFormatVersion, "0.1.0-alpha.1", "staged-old"));

        using var manager = CreateManager(new RouteHandler(new Dictionary<string, byte[]>()),
            currentVersion: "0.1.0-alpha.1");
        manager.SweepLeftovers();

        Assert.Null(UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot)));
        Assert.False(Directory.Exists(stagedFolder));
        Assert.False(Directory.Exists(UpdateStagingPaths.StagingRoot(_installRoot)));
    }

    [Fact]
    public void SweepLeftovers_KeepsAMarkerForAVersionNotYetReached()
    {
        var stagedFolder = UpdateStagingPaths.StagedFolderPath(_installRoot, "staged-pending");
        Directory.CreateDirectory(stagedFolder);
        File.WriteAllText(Path.Combine(stagedFolder, "Tript.Shell.exe"), "shell");
        UpdateMarker.WriteAtomic(UpdateStagingPaths.MarkerPath(_installRoot),
            new UpdateMarker(UpdateMarker.CurrentFormatVersion, "0.1.0-alpha.3", "staged-pending"));

        using var manager = CreateManager(new RouteHandler(new Dictionary<string, byte[]>()),
            currentVersion: "0.1.0-alpha.1");
        manager.SweepLeftovers();

        Assert.NotNull(UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot)));
        Assert.True(Directory.Exists(stagedFolder));
    }

    [Fact]
    public void TryApply_FalseWhenNoMarkerExists()
    {
        using var manager = CreateManager(new RouteHandler(new Dictionary<string, byte[]>()));

        Assert.False(manager.TryApply());
    }

    [Fact]
    public void TryApply_FalseWhenTheStagedFolderIsGone()
    {
        UpdateMarker.WriteAtomic(UpdateStagingPaths.MarkerPath(_installRoot),
            new UpdateMarker(UpdateMarker.CurrentFormatVersion, "0.1.0-alpha.3", "staged-missing"));
        using var manager = CreateManager(new RouteHandler(new Dictionary<string, byte[]>()));

        Assert.False(manager.TryApply());
    }

    [Fact]
    public void TryApply_TrueWhenTheMarkerAndStagedShellBothExist()
    {
        var stagedFolder = UpdateStagingPaths.StagedFolderPath(_installRoot, "staged-ok");
        Directory.CreateDirectory(stagedFolder);
        File.WriteAllText(Path.Combine(stagedFolder, "Tript.Shell.exe"), "shell");
        UpdateMarker.WriteAtomic(UpdateStagingPaths.MarkerPath(_installRoot),
            new UpdateMarker(UpdateMarker.CurrentFormatVersion, "0.1.0-alpha.3", "staged-ok"));
        using var manager = CreateManager(new RouteHandler(new Dictionary<string, byte[]>()));

        Assert.True(manager.TryApply());
    }

    // Deleting old-App on the new version's first startup is what used to make a bad release
    // unrecoverable: the launcher rolls back to it if the new version fails within a minute.
    [Fact]
    public void SweepLeftovers_KeepsThePreviousInstallForTheLauncherToRollBackTo()
    {
        var oldApp = UpdateStagingPaths.OldAppBackupPath(_installRoot);
        Directory.CreateDirectory(oldApp);
        File.WriteAllText(Path.Combine(oldApp, "Tript.Shell.exe"), "previous");

        using var manager = CreateManager(new RouteHandler(new Dictionary<string, byte[]>()));
        manager.SweepLeftovers();

        Assert.True(File.Exists(Path.Combine(oldApp, "Tript.Shell.exe")));
    }

    [Fact]
    public void DiscardPreviousInstall_RemovesItOnceProbationHasPassed()
    {
        var oldApp = UpdateStagingPaths.OldAppBackupPath(_installRoot);
        Directory.CreateDirectory(oldApp);
        File.WriteAllText(Path.Combine(oldApp, "Tript.Shell.exe"), "previous");

        using var manager = CreateManager(new RouteHandler(new Dictionary<string, byte[]>()));
        manager.DiscardPreviousInstall();

        Assert.False(Directory.Exists(oldApp));
    }

    // Longer than the launcher's 60 second window, or the launcher could roll back to nothing.
    [Fact]
    public void ThePreviousInstallOutlivesTheLaunchersRollbackWindow() =>
        Assert.True(UpdateManager.PreviousInstallProbation > TimeSpan.FromSeconds(60));

    // After a rollback the marker names the build that failed, and its staged folder is gone.
    [Fact]
    public void SweepLeftovers_DropsAMarkerForTheVersionThatWasRolledBack()
    {
        UpdateMarker.WriteAtomic(UpdateStagingPaths.MarkerPath(_installRoot),
            new UpdateMarker(UpdateMarker.CurrentFormatVersion, "0.1.0-alpha.3", "staged-gone"));
        File.WriteAllText(UpdateStagingPaths.RolledBackPath(_installRoot), "0.1.0-alpha.3");

        using var manager = CreateManager(new RouteHandler(new Dictionary<string, byte[]>()),
            currentVersion: "0.1.0-alpha.1");
        manager.SweepLeftovers();

        Assert.Null(UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot)));
    }

    // Without the record, the old version downloaded and applied the same broken release the next
    // day, failed, rolled back, and repeated.
    [Fact]
    public async Task AnAutomaticCheck_DoesNotReapplyAVersionThatWasRolledBack()
    {
        var zipBytes = BuildAppZip("Release-win");
        Directory.CreateDirectory(UpdateStagingPaths.StagingRoot(_installRoot));
        File.WriteAllText(UpdateStagingPaths.RolledBackPath(_installRoot), "0.1.0-alpha.3");
        using var manager = CreateManager(new RouteHandler(ReleaseRoutes(zipBytes, ValidHashOf(zipBytes))));

        await manager.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal("available", manager.Snapshot().Stage);
        Assert.Null(UpdateMarker.TryRead(UpdateStagingPaths.MarkerPath(_installRoot)));
    }

    [Fact]
    public async Task AManualCheck_RetriesARolledBackVersionOnPurpose()
    {
        var zipBytes = BuildAppZip("Release-win");
        Directory.CreateDirectory(UpdateStagingPaths.StagingRoot(_installRoot));
        File.WriteAllText(UpdateStagingPaths.RolledBackPath(_installRoot), "0.1.0-alpha.3");
        using var manager = CreateManager(new RouteHandler(ReleaseRoutes(zipBytes, ValidHashOf(zipBytes))));

        await manager.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal("ready", manager.Snapshot().Stage);
    }

    [Fact]
    public async Task ANewerReleaseClearsAnOldRollbackRecord()
    {
        var zipBytes = BuildAppZip("Release-win");
        Directory.CreateDirectory(UpdateStagingPaths.StagingRoot(_installRoot));
        File.WriteAllText(UpdateStagingPaths.RolledBackPath(_installRoot), "0.1.0-alpha.2");
        using var manager = CreateManager(new RouteHandler(ReleaseRoutes(zipBytes, ValidHashOf(zipBytes))));

        await manager.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal("ready", manager.Snapshot().Stage);
        Assert.False(File.Exists(UpdateStagingPaths.RolledBackPath(_installRoot)));
    }

    private void AssertNoStagedLeftovers() =>
        Assert.False(Directory.Exists(UpdateStagingPaths.StagingRoot(_installRoot)));

    private UpdateManager CreateManager(RouteHandler handler, string currentVersion = "0.1.0-alpha.1",
        bool supportsAutomaticApply = true)
    {
        var httpClient = new HttpClient(handler);
        return new UpdateManager(_installRoot, currentVersion, httpClient: httpClient,
            releaseClient: new GitHubReleaseClient(httpClient, new Uri(ReleasesUrl)),
            supportsAutomaticApply: supportsAutomaticApply);
    }

    private static string ValidHashOf(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static Dictionary<string, byte[]> ReleaseRoutes(byte[] zipBytes, string sha256Hex) => new()
    {
        [ReleasesUrl] = ReleaseListJson("v0.1.0-alpha.3",
            assets: [WindowsZipAsset(zipBytes.Length), WindowsShaAsset()]),
        [ZipUrl] = zipBytes,
        [ShaUrl] = Encoding.UTF8.GetBytes($"{sha256Hex}  Tript-0.1.0-alpha.3-win-x64.zip\n"),
    };

    private static object WindowsZipAsset(long size) =>
        new { name = "Tript-0.1.0-alpha.3-win-x64.zip", browser_download_url = ZipUrl, size };

    private static object WindowsShaAsset() =>
        new { name = "Tript-0.1.0-alpha.3-win-x64.zip.sha256", browser_download_url = ShaUrl, size = 90 };

    private static byte[] ReleaseListJson(string tag, object[] assets)
    {
        var releases = new object[]
        {
            new
            {
                tag_name = tag,
                draft = false,
                prerelease = true,
                html_url = $"https://github.com/LeSqueed/Tript/releases/tag/{tag}",
                assets,
            },
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(releases));
    }

    private static byte[] BuildAppZip(string topLevel, params (string Path, byte[] Content)[] extraEntries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, $"{topLevel}/", []);
            AddEntry(archive, $"{topLevel}/App/", []);
            AddEntry(archive, $"{topLevel}/App/dist/", []);
            AddEntry(archive, $"{topLevel}/Tript.exe", "launcher"u8.ToArray());
            AddEntry(archive, $"{topLevel}/App/Tript.Shell.exe", "shell"u8.ToArray());
            AddEntry(archive, $"{topLevel}/App/dist/index.html", "<html/>"u8.ToArray());
            foreach (var (path, content) in extraEntries)
                AddEntry(archive, path, content);
        }
        return stream.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string entryName, byte[] content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var entryStream = entry.Open();
        entryStream.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_installRoot))
            Directory.Delete(_installRoot, recursive: true);
    }

    private sealed class RouteHandler(IReadOnlyDictionary<string, byte[]> routes) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (!routes.TryGetValue(url, out var content))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }
}
