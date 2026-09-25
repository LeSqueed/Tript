// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Tript.TestSupport;
using Xunit;

namespace Tript.GameDiscovery.Tests;

public sealed partial class LutrisInventorySourceTests
{
    private static readonly string[] NativeDatabase = ["home", ".local", "share", "lutris", "pga.db"];
    private static readonly string[] FlatpakDatabase = ["home", ".var", "app", "net.lutris.Lutris", "data", "lutris", "pga.db"];

    [Fact]
    public async Task StoreServicesMapToTheirStoreProductIdentity()
    {
        using var fixture = new TempFixture();
        var rows = new FakeLutrisRows(
            new("Portal 2", fixture.DirectoryPath("Games", "Portal 2"), "steam", "620"),
            new("Fall Guys", fixture.DirectoryPath("Games", "FallGuys"), "egs", "0a2d9f6403244d12969e11da6713137b"),
            new("Hades", fixture.DirectoryPath("Games", "Hades"), "gog", "1418100723"));
        fixture.FilePath("", NativeDatabase);

        var inventory = await Source(fixture, rows).DiscoverAsync();

        Assert.Equal(
            [new ProductId(GameStore.Steam, "620"), new ProductId(GameStore.Epic, "0a2d9f6403244d12969e11da6713137b"), new ProductId(GameStore.Gog, "1418100723")],
            inventory.Games.Select(game => game.ProductId));
        Assert.Equal(Path.Combine(fixture.Root, "Games", "Hades"),
            inventory.Games.Single(game => game.Store == GameStore.Gog).InstallRoot);
        Assert.Empty(inventory.Diagnostics);
    }

    [Fact]
    public async Task GameWithoutAKnownStoreIsKeptWithoutAStoreIdentity()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "osu");
        var rows = new FakeLutrisRows(
            new("osu!", root, null, null),
            new("Humble Game", fixture.DirectoryPath("Games", "Humble"), "humblebundle", "abc"),
            new("Steam without id", fixture.DirectoryPath("Games", "NoId"), "steam", ""));
        fixture.FilePath("", NativeDatabase);

        var inventory = await Source(fixture, rows).DiscoverAsync();

        Assert.All(inventory.Games, game => Assert.Equal(GameStore.Local, game.Store));
        Assert.All(inventory.Games, game => Assert.False(game.Store.HasStoreProductIdentity()));
        var osu = inventory.Games.Single(game => game.DisplayName == "osu!");
        Assert.Equal(root, osu.InstallRoot);
        Assert.Equal(3, inventory.Games.Length);
    }

    [Fact]
    public async Task EntriesWithoutAnInstallDirectoryAreSkipped()
    {
        using var fixture = new TempFixture();
        var rows = new FakeLutrisRows(
            new("Emulated", "", null, null),
            new("Also emulated", null, "steam", "10"),
            new("Relative", "games/relative", null, null));
        fixture.FilePath("", NativeDatabase);

        var inventory = await Source(fixture, rows).DiscoverAsync();

        Assert.Empty(inventory.Games);
    }

    [Fact]
    public async Task UnnamedGameIsNamedAfterItsInstallFolder()
    {
        using var fixture = new TempFixture();
        var rows = new FakeLutrisRows(new LutrisGameRow(" ", fixture.DirectoryPath("Games", "Doom"), null, null));
        fixture.FilePath("", NativeDatabase);

        var inventory = await Source(fixture, rows).DiscoverAsync();

        Assert.Equal("Doom", Assert.Single(inventory.Games).DisplayName);
    }

    [Fact]
    public async Task NativeAndFlatpakDatabasesAreBothRead()
    {
        using var fixture = new TempFixture();
        var rows = new FakeLutrisRows(new LutrisGameRow("Doom", fixture.DirectoryPath("Games", "Doom"), null, null));
        var native = fixture.FilePath("", NativeDatabase);
        var flatpak = fixture.FilePath("", FlatpakDatabase);

        await Source(fixture, rows).DiscoverAsync();

        Assert.Equal([native, flatpak], rows.ReadDatabases);
    }

    [LinuxFact]
    public async Task LinkedDatabaseIsReadOnce()
    {
        using var fixture = new TempFixture();
        var rows = new FakeLutrisRows();
        var flatpak = fixture.FilePath("", FlatpakDatabase);
        fixture.DirectoryPath("home", ".local", "share", "lutris");
        File.CreateSymbolicLink(Path.Combine([fixture.Root, .. NativeDatabase]), flatpak);

        await Source(fixture, rows).DiscoverAsync();

        Assert.Equal([flatpak], rows.ReadDatabases);
    }

    [Fact]
    public async Task HomeWithoutLutrisNeverOpensADatabase()
    {
        using var fixture = new TempFixture();
        var rows = new FakeLutrisRows();

        var inventory = await Source(fixture, rows).DiscoverAsync();

        Assert.Empty(rows.ReadDatabases);
        Assert.Empty(inventory.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(DatabaseFailures))]
    public async Task UnreadableDatabaseIsReportedAndYieldsNothing(Exception failure)
    {
        using var fixture = new TempFixture();
        var database = fixture.FilePath("", NativeDatabase);

        var inventory = await Source(fixture, new FakeLutrisRows { Failure = failure }).DiscoverAsync();

        Assert.Empty(inventory.Games);
        var diagnostic = Assert.Single(inventory.Diagnostics);
        Assert.Equal("lutris.database", diagnostic.Code);
        Assert.Equal(database, diagnostic.Location);
    }

    public static TheoryData<Exception> DatabaseFailures() =>
    [
        new DllNotFoundException("libsqlite3.so.0 not found"),
        new IOException("SQLite error 5: database is locked"),
        new UnauthorizedAccessException("denied"),
    ];

    [SqliteFact]
    public async Task InstalledGamesAreReadFromARealLutrisDatabase()
    {
        using var fixture = new TempFixture();
        var root = fixture.DirectoryPath("Games", "Hades");
        var database = Path.Combine([fixture.Root, .. NativeDatabase]);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        SqliteWriter.Execute(database, $"""
            CREATE TABLE games (id INTEGER PRIMARY KEY, name TEXT, slug TEXT, directory TEXT, installed INTEGER, service TEXT, service_id TEXT);
            INSERT INTO games (name, slug, directory, installed, service, service_id) VALUES ('Hadès', 'hades', '{root}', 1, 'gog', '1418100723');
            INSERT INTO games (name, slug, directory, installed, service, service_id) VALUES ('Uninstalled', 'gone', '{root}', 0, 'steam', '1');
            INSERT INTO games (name, slug, directory, installed, service, service_id) VALUES ('Wine App', 'wine-app', '{root}', 1, NULL, NULL);
            """);

        var inventory = await new LutrisInventorySource(new PhysicalDiscoveryFileSystem(), Path.Combine(fixture.Root, "home"))
            .DiscoverAsync();

        Assert.Empty(inventory.Diagnostics);
        Assert.Equal(2, inventory.Games.Length);
        var hades = inventory.Games.Single(game => game.Store == GameStore.Gog);
        Assert.Equal("Hadès", hades.DisplayName);
        Assert.Equal("1418100723", hades.ProductId.Value);
        Assert.Equal("Wine App", inventory.Games.Single(game => game.Store == GameStore.Local).DisplayName);
    }

    [SqliteFact]
    public async Task FileThatIsNotADatabaseIsReported()
    {
        using var fixture = new TempFixture();
        fixture.FilePath("definitely not sqlite, just some text that is long enough to have a header", NativeDatabase);

        var inventory = await new LutrisInventorySource(new PhysicalDiscoveryFileSystem(), Path.Combine(fixture.Root, "home"))
            .DiscoverAsync();

        Assert.Empty(inventory.Games);
        Assert.Equal("lutris.database", Assert.Single(inventory.Diagnostics).Code);
    }

    private static LutrisInventorySource Source(TempFixture fixture, ILutrisGameRows rows) =>
        new(new PhysicalDiscoveryFileSystem(), fixture.DirectoryPath("home"), rows);

    private sealed class FakeLutrisRows(params LutrisGameRow[] rows) : ILutrisGameRows
    {
        public Exception? Failure { get; init; }
        public List<string> ReadDatabases { get; } = [];

        public IReadOnlyList<LutrisGameRow> InstalledGames(string databasePath)
        {
            ReadDatabases.Add(databasePath);
            if (Failure is not null)
                throw Failure;
            return rows;
        }
    }

    private static partial class SqliteWriter
    {
        private const int OpenReadWriteCreate = 0x2 | 0x4;

        public static void Execute(string path, string sql)
        {
            var status = sqlite3_open_v2(path, out var database, OpenReadWriteCreate, null);
            try
            {
                Assert.Equal(0, status);
                Assert.Equal(0, sqlite3_exec(database, sql, 0, 0, 0));
            }
            finally
            {
                _ = sqlite3_close(database);
            }
        }

        [LibraryImport(SqliteReadOnlyQuery.Library, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int sqlite3_open_v2(string filename, out nint database, int flags, string? vfs);

        [LibraryImport(SqliteReadOnlyQuery.Library, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int sqlite3_exec(nint database, string sql, nint callback, nint argument, nint error);

        [LibraryImport(SqliteReadOnlyQuery.Library)]
        private static partial int sqlite3_close(nint database);
    }
}

public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (!NativeLibrary.TryLoad(SqliteReadOnlyQuery.Library, out var handle))
            Skip = $"{SqliteReadOnlyQuery.Library} is not available";
        else
            NativeLibrary.Free(handle);
    }
}
