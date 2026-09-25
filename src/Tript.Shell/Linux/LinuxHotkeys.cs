// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Settings;

namespace Tript.Shell.Linux;

internal enum LinuxHotkeyBackend
{
    None,
    Portal,
    X11,
    XWayland,
}

internal sealed record HotkeyAvailability(bool Available, string? Note, bool ManagedByDesktop = false,
    bool Configurable = false);

internal static class LinuxHotkeyBackends
{
    internal const string XWaylandNote =
        "This desktop has no global shortcuts portal, so hotkeys only work while an X11 window has focus, " +
        "such as a game running through XWayland or Proton.";

    internal const string PortalNote =
        "Your desktop manages these shortcuts. It asks you to confirm them the first time, and from then on the keys " +
        "are changed in the desktop's shortcut settings.";

    internal static bool IsWaylandSession(Func<string, string?> environment) =>
        !string.IsNullOrEmpty(environment("WAYLAND_DISPLAY"))
        || string.Equals(environment("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    internal static bool HasX11Display(Func<string, string?> environment) =>
        !string.IsNullOrEmpty(environment("DISPLAY"));

    internal static LinuxHotkeyBackend Choose(bool waylandSession, bool x11Display, Func<bool> portalAvailable,
        Func<bool> x11Opens)
    {
        if (waylandSession)
        {
            if (portalAvailable())
                return LinuxHotkeyBackend.Portal;
            return x11Display && x11Opens() ? LinuxHotkeyBackend.XWayland : LinuxHotkeyBackend.None;
        }

        if (x11Display && x11Opens())
            return LinuxHotkeyBackend.X11;
        return portalAvailable() ? LinuxHotkeyBackend.Portal : LinuxHotkeyBackend.None;
    }

    internal static HotkeyAvailability Describe(LinuxHotkeyBackend backend, uint portalVersion = 0) => backend switch
    {
        LinuxHotkeyBackend.Portal => new HotkeyAvailability(true, PortalNote, ManagedByDesktop: true,
            Configurable: portalVersion >= PortalGlobalShortcuts.ConfigureShortcutsVersion),
        LinuxHotkeyBackend.X11 => new HotkeyAvailability(true, null),
        LinuxHotkeyBackend.XWayland => new HotkeyAvailability(true, XWaylandNote),
        _ => new HotkeyAvailability(false, null),
    };
}

internal sealed class LinuxHotkeys : IGlobalHotkeys
{
    internal const string ApplicationId = "io.github.lesqueed.Tript";

    private readonly Action<HotkeyAction> _onHotkey;
    private readonly Action<string> _onRegistrationFailed;
    private readonly Action<HotkeyAvailability> _onAvailability;
    private readonly Action<IReadOnlyDictionary<HotkeyAction, string>> _onDesktopTriggers;
    private uint _portalVersion;
    private readonly Lock _gate = new();
    private IReadOnlyList<LinuxHotkey>? _latest;
    private Action<IReadOnlyList<LinuxHotkey>>? _apply;
    private GMainLoopThread? _portalThread;
    private GioDBusSession? _portalBus;
    private PortalGlobalShortcuts? _portal;
    private X11Hotkeys? _x11;
    private bool _disposed;

    internal LinuxHotkeys(Action<HotkeyAction> onHotkey, Action<string> onRegistrationFailed,
        Action<HotkeyAvailability> onAvailability, Action<IReadOnlyDictionary<HotkeyAction, string>> onDesktopTriggers)
    {
        _onHotkey = onHotkey;
        _onRegistrationFailed = onRegistrationFailed;
        _onAvailability = onAvailability;
        _onDesktopTriggers = onDesktopTriggers;
        ThreadPool.QueueUserWorkItem(_ => Start());
    }

    public void ApplyBindings(IReadOnlyDictionary<HotkeyAction, HotkeyBinding?> effective)
    {
        var hotkeys = LinuxKeySymbols.Resolve(effective);
        Action<IReadOnlyList<LinuxHotkey>>? apply;
        lock (_gate)
        {
            if (_latest is not null && _latest.SequenceEqual(hotkeys))
                return;

            _latest = hotkeys;
            apply = _apply;
        }

        apply?.Invoke(hotkeys);
    }

    internal bool ConfigureInDesktop()
    {
        if (_portal is not { } portal || _portalThread is not { } thread)
            return false;

        try
        {
            return thread.Invoke(portal.Configure);
        }
        catch (DBusException exception)
        {
            Log.Warning(exception, "Tript.Shell: the desktop's shortcut settings could not be opened");
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _apply = null;
        }

        _x11?.Dispose();
        if (_portal is { } portal && _portalThread is { } thread)
        {
            try
            {
                thread.Invoke(() =>
                {
                    portal.Dispose();
                    return true;
                });
            }
            catch (DBusException exception)
            {
                Log.Debug(exception, "Tript.Shell: the global shortcuts session was not closed");
            }
        }

        _portalBus?.Dispose();
        _portalThread?.Dispose();
    }

    private void Start()
    {
        LinuxHotkeyBackend backend;
        try
        {
            backend = LinuxHotkeyBackends.Choose(
                LinuxHotkeyBackends.IsWaylandSession(Environment.GetEnvironmentVariable),
                LinuxHotkeyBackends.HasX11Display(Environment.GetEnvironmentVariable),
                OpenPortal,
                OpenX11);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Tript.Shell: global hotkeys could not be set up");
            backend = LinuxHotkeyBackend.None;
        }

        Log.Information("Tript.Shell: global hotkeys use {Backend}", backend);
        if (backend != LinuxHotkeyBackend.Portal)
            ClosePortal();

        Action<IReadOnlyList<LinuxHotkey>>? apply = backend switch
        {
            LinuxHotkeyBackend.Portal => ApplyThroughPortal,
            LinuxHotkeyBackend.X11 or LinuxHotkeyBackend.XWayland => hotkeys => _x11!.Apply(hotkeys),
            _ => null,
        };

        IReadOnlyList<LinuxHotkey>? latest;
        lock (_gate)
        {
            if (_disposed)
                return;

            _apply = apply;
            latest = _latest;
        }

        if (latest is not null)
            apply?.Invoke(latest);
        _onAvailability(LinuxHotkeyBackends.Describe(backend, _portalVersion));
    }

    private void ApplyThroughPortal(IReadOnlyList<LinuxHotkey> hotkeys)
    {
        var shortcuts = PortalGlobalShortcuts.Describe(hotkeys);
        _portalThread?.Post(() => _portal?.Bind(shortcuts));
    }

    private bool OpenPortal()
    {
        try
        {
            _portalThread = new GMainLoopThread("Tript desktop portal");
            _portalBus = GioDBusSession.Open(_portalThread);
            PortalGlobalShortcuts.RegisterApplication(_portalBus, ApplicationId);
            _portalVersion = PortalGlobalShortcuts.Version(_portalBus);
            if (_portalVersion < 1)
                return false;

            var bus = _portalBus;
            _portal = _portalThread.Invoke(() =>
                new PortalGlobalShortcuts(bus, _onHotkey, _onRegistrationFailed, _onDesktopTriggers));
            return true;
        }
        catch (Exception exception) when (exception is DBusException or DllNotFoundException
                                              or EntryPointNotFoundException or FormatException)
        {
            Log.Information("Tript.Shell: the desktop portal is not reachable ({Reason})", exception.Message);
            return false;
        }
    }

    private void ClosePortal()
    {
        _portalBus?.Dispose();
        _portalBus = null;
        _portalThread?.Dispose();
        _portalThread = null;
        _portal = null;
    }

    private bool OpenX11()
    {
        _x11 = X11Hotkeys.TryOpen(_onHotkey, _onRegistrationFailed);
        return _x11 is not null;
    }
}
