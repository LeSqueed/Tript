// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Recorder.Tests;

public sealed class X11ActiveWindowProbeTests
{
    private const int GameProcess = 4242;
    private const ulong Fullscreen = 301;
    private const ulong Maximized = 302;
    private const string GameFolder = "/home/u/Games/Hollow Knight";
    private static readonly ScreenRect Main = new(0, 0, 2560, 1440);
    private static readonly ScreenRect Side = new(2560, 0, 1920, 1080);
    private static readonly DateTimeOffset Started = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeProcessFiles _files = new();
    private readonly FakeDesktop _desktop = new();
    private readonly ManualTime _time = new();
    private Func<IX11Desktop?> _connect;
    private int _connections;

    public X11ActiveWindowProbeTests()
    {
        _connect = () => _desktop;
        _files.Executables[GameProcess] = GameFolder + "/hollow_knight.x86_64";
    }

    private X11ActiveWindowProbe Probe() => new(
        () =>
        {
            _connections++;
            return _connect();
        },
        _files, _ => Started, path => path, _time, ownProcessId: 99);

    private static X11ActiveWindow Window(
        ScreenRect rect,
        IReadOnlyList<ulong>? state = null,
        int? processId = GameProcess,
        ScreenRect? frame = null)
        => new(processId, state ?? [], Fullscreen, frame is { } f ? [rect, f] : [rect], [Main, Side]);

    [Fact]
    public void AWindowTheWindowManagerMarksFullscreen_IsFullscreenWhateverItsSize()
        => Assert.True(X11ActiveWindowProbe.IsFullscreen(new(100, 100, 800, 600), [Maximized, Fullscreen], Fullscreen, [Main]));

    [Fact]
    public void AWindowCoveringTheSecondaryMonitor_IsFullscreenWithoutTheState()
        => Assert.True(X11ActiveWindowProbe.IsFullscreen(new(2560, 0, 1920, 1080), [], Fullscreen, [Main, Side]));

    [Fact]
    public void AWindowOffByOnePixel_StillCoversItsMonitor()
        => Assert.True(X11ActiveWindowProbe.IsFullscreen(new(2561, 1, 1918, 1078), [], Fullscreen, [Main, Side]));

    [Fact]
    public void AMaximizedWindowBelowAPanel_IsNotFullscreen()
        => Assert.False(X11ActiveWindowProbe.IsFullscreen(new(0, 0, 2560, 1392), [Maximized], Fullscreen, [Main, Side]));

    [Fact]
    public void AnOrdinaryWindow_IsNotFullscreen()
        => Assert.False(X11ActiveWindowProbe.IsFullscreen(new(200, 150, 1280, 720), [], Fullscreen, [Main, Side]));

    [Fact]
    public void AnUnknownFullscreenAtom_NeverMatchesAnEmptyStateSlot()
        => Assert.False(X11ActiveWindowProbe.IsFullscreen(new(200, 150, 1280, 720), [0], 0, [Main]));

    [Theory]
    [InlineData("/usr/lib/firefox/firefox")]
    [InlineData("/usr/bin/mpv")]
    [InlineData("/opt/google/chrome/chrome")]
    [InlineData("/snap/vlc/3777/usr/bin/vlc")]
    [InlineData("/app/bin/obs")]
    [InlineData("/home/u/.local/share/Steam/ubuntu12_64/steamwebhelper")]
    [InlineData("/home/u/.steam/steam/ubuntu12_32/steam")]
    [InlineData("/home/u/.steam/debian-installation/ubuntu12_64/steam-runtime-heavy/pv-bwrap")]
    [InlineData("/home/u/bin/steam")]
    [InlineData("/home/u/Games/Hollow Knight/UnityCrashHandler64.exe")]
    [InlineData("/home/u/Games/prefix/drive_c/windows/system32/explorer.exe")]
    [InlineData("relative/game")]
    public void DesktopApplicationsAndTheSteamClient_AreNeverAGame(string path)
        => Assert.True(X11ActiveWindowProbe.IsNeverAGame(path));

    [Theory]
    [InlineData("/home/u/Games/Hollow Knight/hollow_knight.x86_64")]
    [InlineData("/home/u/.local/share/Steam/steamapps/common/Celeste/Celeste.exe")]
    [InlineData("/mnt/games/usr/Factorio/bin/x64/factorio")]
    [InlineData("/opt/googlegames/runner")]
    public void GamesOutsideSystemFolders_CanBeGames(string path)
        => Assert.False(X11ActiveWindowProbe.IsNeverAGame(path));

    [Fact]
    public void Format32Items_AreReadFromNativeLongs()
    {
        byte[] data = [0x2A, 0x10, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal([0x102Aul, 7ul], X11Properties.DecodeFormat32(data, 32, 2, 8));
        Assert.Equal([0x102Aul, 0xFFFFFFFFul, 7ul, 0ul], X11Properties.DecodeFormat32(data, 32, 4, 4));
    }

    [Fact]
    public void PropertiesOfAnotherFormat_DecodeToNothing()
        => Assert.Empty(X11Properties.DecodeFormat32(new byte[16], 8, 2, 8));

    [Fact]
    public void AnItemCountBeyondTheData_ReadsOnlyWhatIsThere()
        => Assert.Single(X11Properties.DecodeFormat32(new byte[12], 32, 5, 8));

    [Fact]
    public void ANoneActiveWindow_IsNoWindow()
    {
        Assert.Null(X11Properties.DecodeWindow([0]));
        Assert.Null(X11Properties.DecodeWindow([]));
        Assert.Equal(0x3a00007ul, X11Properties.DecodeWindow([0x3a00007]));
    }

    [Fact]
    public void AProcessIdOutsideTheValidRange_IsNoProcess()
    {
        Assert.Null(X11Properties.DecodeProcessId([0]));
        Assert.Null(X11Properties.DecodeProcessId([0x80000000]));
        Assert.Equal(4242, X11Properties.DecodeProcessId([4242]));
    }

    [Fact]
    public void AFullscreenGameWindow_IsProposedWithItsProcess()
    {
        _desktop.Active = Window(new(10, 10, 800, 600), state: [Fullscreen]);

        var candidate = Probe().Probe();

        Assert.Equal(
            new FullscreenGameCandidate(GameProcess, "hollow_knight.x86_64", GameFolder + "/hollow_knight.x86_64", Started),
            candidate);
    }

    [Fact]
    public void AGameCoveringTheSecondaryMonitor_IsProposed()
    {
        _desktop.Active = Window(Side);

        Assert.Equal(GameProcess, Probe().Probe()?.ProcessId);
    }

    [Fact]
    public void AClientInsideAFrameThatCoversTheMonitor_IsProposed()
    {
        _desktop.Active = Window(new(0, 0, 2560, 1400), frame: Main);

        Assert.Equal(GameProcess, Probe().Probe()?.ProcessId);
    }

    [Fact]
    public void AWindowedGame_IsNotProposed()
    {
        _desktop.Active = Window(new(200, 150, 1280, 720), frame: new(198, 120, 1284, 752));

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void WithNoActiveX11Window_NothingIsProposed()
    {
        _desktop.Active = null;

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void TriptsOwnFullscreenWindow_IsNotProposed()
    {
        _files.Executables[99] = GameFolder + "/hollow_knight.x86_64";
        _desktop.Active = Window(Main, processId: 99);

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void AWindowWithoutAProcessId_IsNotProposed()
    {
        _desktop.Active = Window(Main, processId: null);

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void AFullscreenProcessWhoseExecutableCannotBeRead_IsNotProposed()
    {
        _files.Executables.Remove(GameProcess);
        _desktop.Active = Window(Main);

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void AFullscreenBrowser_IsNotProposed()
    {
        _files.Executables[GameProcess] = "/usr/lib/firefox/firefox";
        _desktop.Active = Window(Main, state: [Fullscreen]);

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void AFullscreenProtonGame_IsProposedByItsWindowsExecutable()
    {
        _files.Executables.Remove(GameProcess);
        _files.CommandLines[GameProcess] = @"Z:\home\u\Games\Celeste\Celeste.exe" + "\0";
        _desktop.Active = Window(Main);

        var candidate = Probe().Probe();

        Assert.Equal("/home/u/Games/Celeste/Celeste.exe", candidate?.ExecutablePath);
        Assert.Equal("Celeste", candidate?.Executable);
    }

    [Fact]
    public void WithoutAnXServer_TheProbeAnswersNothingAndWaitsBeforeRetrying()
    {
        _connect = () => null;
        var probe = Probe();

        Assert.Null(probe.Probe());
        Assert.Null(probe.Probe());
        _time.Advance(X11ActiveWindowProbe.ReconnectDelay - TimeSpan.FromSeconds(1));
        Assert.Null(probe.Probe());
        Assert.Equal(1, _connections);

        _time.Advance(TimeSpan.FromSeconds(1));
        _connect = () => _desktop;
        _desktop.Active = Window(Main);

        Assert.Equal(GameProcess, probe.Probe()?.ProcessId);
        Assert.Equal(2, _connections);
    }

    [Fact]
    public void AMissingX11Library_IsTreatedAsNoXServer()
    {
        _connect = () => throw new DllNotFoundException("libX11.so.6");

        Assert.Null(Probe().Probe());
    }

    [Fact]
    public void AnOpenConnection_IsReusedAcrossPolls()
    {
        _desktop.Active = Window(Main);
        var probe = Probe();

        probe.Probe();
        probe.Probe();

        Assert.Equal(1, _connections);
        Assert.Equal(2, _desktop.Reads);
    }

    [Fact]
    public void ALostConnection_IsClosedAndReopenedLater()
    {
        _desktop.Active = Window(Main);
        var probe = Probe();
        probe.Probe();

        _desktop.IsConnected = false;
        Assert.Null(probe.Probe());
        Assert.True(_desktop.Disposed);

        _desktop.IsConnected = true;
        _desktop.Disposed = false;
        _time.Advance(X11ActiveWindowProbe.ReconnectDelay);

        Assert.Equal(GameProcess, probe.Probe()?.ProcessId);
        Assert.Equal(2, _connections);
    }

    [Fact]
    public void AFailingRead_IsNotThrownAndDropsTheConnection()
    {
        _desktop.Failure = new System.Runtime.InteropServices.SEHException();
        var probe = Probe();

        Assert.Null(probe.Probe());
        Assert.True(_desktop.Disposed);
    }

    [Fact]
    public void DisposingTheProbe_ClosesTheConnectionAndStopsProbing()
    {
        _desktop.Active = Window(Main);
        var probe = Probe();
        probe.Probe();

        probe.Dispose();

        Assert.True(_desktop.Disposed);
        Assert.Null(probe.Probe());
        Assert.Equal(1, _connections);
    }

    [Fact]
    public void ThisMachinesProbe_AnswersWithoutThrowing()
    {
        using var probe = X11ActiveWindowProbe.ForThisMachine();

        var exception = Record.Exception(() => probe.Probe());

        Assert.Null(exception);
    }

    private sealed class FakeDesktop : IX11Desktop
    {
        internal X11ActiveWindow? Active { get; set; }

        internal Exception? Failure { get; set; }

        internal bool Disposed { get; set; }

        internal int Reads { get; private set; }

        public bool IsConnected { get; set; } = true;

        public X11ActiveWindow? ReadActiveWindow()
        {
            Reads++;
            if (Failure is not null)
                throw Failure;
            return Active;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeProcessFiles : IProcessFiles
    {
        internal Dictionary<int, string> CommandLines { get; } = [];

        internal Dictionary<int, string> Executables { get; } = [];

        public string? ReadCommandLine(int processId) => CommandLines.GetValueOrDefault(processId);

        public string? ReadExecutableLink(int processId) => Executables.GetValueOrDefault(processId);

        public string? ReadEnvironmentVariable(int processId, string name) => null;

        public string? ReadCommandName(int processId) => null;
    }
}
