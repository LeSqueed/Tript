// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Settings;

namespace Tript.Shell.Linux;

internal sealed record PortalShortcut(string Id, HotkeyAction Action, string Description, string Trigger);

internal sealed class PortalGlobalShortcuts : IDisposable
{
    internal const string Destination = "org.freedesktop.portal.Desktop";
    internal const string DesktopPath = "/org/freedesktop/portal/desktop";
    internal const string Interface = "org.freedesktop.portal.GlobalShortcuts";
    internal const string RequestInterface = "org.freedesktop.portal.Request";
    internal const string SessionInterface = "org.freedesktop.portal.Session";
    internal const string RegistryInterface = "org.freedesktop.host.portal.Registry";

    private const uint ResponseSuccess = 0;
    private const uint ResponseCancelled = 1;

    private readonly IDBusSession _bus;
    private readonly Action<HotkeyAction> _onActivated;
    private readonly Action<string> _onFailure;
    private readonly Action<IReadOnlyDictionary<HotkeyAction, string>> _onTriggers;
    private readonly IDisposable _activations;
    private readonly IDisposable _triggerChanges;
    private IReadOnlyList<PortalShortcut>? _desired;
    private IReadOnlyList<PortalShortcut>? _attempted;
    private IReadOnlyList<PortalShortcut> _bound = [];
    private string? _session;
    private PendingRequest? _pending;
    private int _tokens;
    private bool _disposed;

    internal PortalGlobalShortcuts(IDBusSession bus, Action<HotkeyAction> onActivated, Action<string> onFailure,
        Action<IReadOnlyDictionary<HotkeyAction, string>>? onTriggers = null)
    {
        _bus = bus;
        _onActivated = onActivated;
        _onFailure = onFailure;
        _onTriggers = onTriggers ?? (_ => { });
        _activations = bus.Subscribe(new DBusSignalMatch(Destination, Interface, "Activated", DesktopPath),
            OnActivated);
        _triggerChanges = bus.Subscribe(new DBusSignalMatch(Destination, Interface, "ShortcutsChanged", DesktopPath),
            OnShortcutsChanged);
    }

    internal string? SessionHandle => _session;

    internal const uint ConfigureShortcutsVersion = 2;

    internal static bool IsAvailable(IDBusSession bus) => Version(bus) >= 1;

    internal static uint Version(IDBusSession bus)
    {
        try
        {
            return DBusProperties.Get(bus, Destination, DesktopPath, Interface, "version").AsUInt32() ?? 0;
        }
        catch (DBusException exception)
        {
            Log.Debug(exception, "Tript.Shell: the global shortcuts portal is not available");
            return 0;
        }
    }

    internal bool Configure()
    {
        if (_disposed || _session is not { } session)
            return false;

        try
        {
            _bus.Call(new DBusCall(Destination, DesktopPath, Interface, "ConfigureShortcuts",
                GVariantText.Tuple(GVariantText.ObjectPath(session), GVariantText.String(string.Empty),
                    GVariantText.VariantDictionary([]))));
            return true;
        }
        catch (DBusException exception)
        {
            Log.Warning(exception, "Tript.Shell: the desktop could not open its shortcut settings");
            return false;
        }
    }

    internal static void RegisterApplication(IDBusSession bus, string applicationId)
    {
        try
        {
            bus.Call(new DBusCall(Destination, DesktopPath, RegistryInterface, "Register",
                GVariantText.Tuple(GVariantText.String(applicationId), GVariantText.VariantDictionary([]))));
        }
        catch (DBusException exception)
        {
            Log.Debug(exception, "Tript.Shell: the portal did not register Tript's application id");
        }
    }

    internal static string RequestPath(string uniqueName, string token) =>
        $"{DesktopPath}/request/{uniqueName.TrimStart(':').Replace('.', '_')}/{token}";

    internal static IReadOnlyList<PortalShortcut> Describe(IEnumerable<LinuxHotkey> hotkeys) =>
        hotkeys.Select(hotkey => new PortalShortcut(ShortcutId(hotkey.Action), hotkey.Action,
            Description(hotkey.Action), LinuxKeySymbols.PortalTrigger(hotkey))).ToList();

    internal static string ShortcutId(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleRecording => "toggle-recording",
        HotkeyAction.ManualBookmark => "manual-bookmark",
        HotkeyAction.QuickClip => "quick-clip",
        _ => action.ToString(),
    };

    private static string Description(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleRecording => "Start or stop recording",
        HotkeyAction.ManualBookmark => "Bookmark the current moment",
        HotkeyAction.QuickClip => "Save a quick clip from the replay buffer",
        _ => action.ToString(),
    };

    internal void Bind(IReadOnlyList<PortalShortcut> shortcuts)
    {
        if (_disposed)
            return;

        _desired = shortcuts;
        if (_pending is null)
            Advance();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pending?.Subscription?.Dispose();
        _pending = null;
        CloseSession();
        _activations.Dispose();
        _triggerChanges.Dispose();
    }

    private void Advance()
    {
        if (_disposed || _desired is not { } target || Same(target, _attempted))
            return;

        _attempted = target;
        CloseSession();
        if (target.Count == 0)
            return;

        var sessionToken = NextToken();
        Request(RequestKind.CreateSession, target, token => new DBusCall(Destination, DesktopPath, Interface,
            "CreateSession", GVariantText.Tuple(GVariantText.VariantDictionary(
            [
                new("handle_token", GVariantText.String(token)),
                new("session_handle_token", GVariantText.String(sessionToken)),
            ]))));
    }

    private void BindShortcuts(string session, IReadOnlyList<PortalShortcut> shortcuts)
    {
        var described = shortcuts.Select(shortcut => GVariantText.Tuple(
            GVariantText.String(shortcut.Id),
            GVariantText.VariantDictionary(
            [
                new("description", GVariantText.String(shortcut.Description)),
                new("preferred_trigger", GVariantText.String(shortcut.Trigger)),
            ])));

        Request(RequestKind.BindShortcuts, shortcuts, token => new DBusCall(Destination, DesktopPath, Interface,
            "BindShortcuts", GVariantText.Tuple(
                GVariantText.ObjectPath(session),
                GVariantText.Array("(sa{sv})", described),
                GVariantText.String(string.Empty),
                GVariantText.VariantDictionary([new("handle_token", GVariantText.String(token))]))));
    }

    private void Request(RequestKind kind, IReadOnlyList<PortalShortcut> shortcuts, Func<string, DBusCall> call)
    {
        var token = NextToken();
        var expectedPath = RequestPath(_bus.UniqueName, token);
        var pending = new PendingRequest(kind, shortcuts);
        _pending = pending;
        pending.Subscription = SubscribeResponse(expectedPath, pending);

        GVariantNode reply;
        try
        {
            reply = _bus.Call(call(token));
        }
        catch (DBusException exception)
        {
            Log.Warning(exception, "Tript.Shell: the global shortcuts portal refused {Kind}", kind);
            Settle(pending);
            _onFailure("Tript could not set up its global shortcuts with the desktop.");
            return;
        }

        var actualPath = reply[0]?.AsString();
        if (actualPath is not null && actualPath != expectedPath && ReferenceEquals(_pending, pending))
        {
            pending.Subscription.Dispose();
            pending.Subscription = SubscribeResponse(actualPath, pending);
        }
    }

    private IDisposable SubscribeResponse(string requestPath, PendingRequest pending) =>
        _bus.Subscribe(new DBusSignalMatch(Destination, RequestInterface, "Response", requestPath),
            arguments => OnResponse(pending, arguments));

    private void OnResponse(PendingRequest pending, GVariantNode arguments)
    {
        if (_disposed || !ReferenceEquals(_pending, pending))
            return;

        Settle(pending);
        var response = arguments[0]?.AsUInt32();
        var results = arguments[1];
        if (response != ResponseSuccess)
        {
            _onFailure(response == ResponseCancelled
                ? "Global shortcuts were not set up because the desktop's request was cancelled."
                : "The desktop could not set up Tript's global shortcuts.");
            Advance();
            return;
        }

        if (pending.Kind == RequestKind.CreateSession)
        {
            _session = results?.Lookup("session_handle")?.AsString();
            if (_session is null)
            {
                _onFailure("The desktop did not open a global shortcuts session for Tript.");
                return;
            }

            var latest = _desired ?? pending.Shortcuts;
            _attempted = latest;
            if (latest.Count == 0)
                CloseSession();
            else
                BindShortcuts(_session, latest);
            return;
        }

        _bound = pending.Shortcuts;
        _attempted = pending.Shortcuts;
        ReportTriggers(results?.Lookup("shortcuts"));
        Advance();
    }

    private void Settle(PendingRequest pending)
    {
        pending.Subscription?.Dispose();
        if (ReferenceEquals(_pending, pending))
            _pending = null;
    }

    private void OnActivated(GVariantNode arguments)
    {
        if (_disposed || _session is null || arguments[0]?.AsString() != _session)
            return;

        var id = arguments[1]?.AsString();
        foreach (var shortcut in _bound)
        {
            if (shortcut.Id == id)
            {
                _onActivated(shortcut.Action);
                return;
            }
        }
    }

    private void OnShortcutsChanged(GVariantNode arguments)
    {
        if (_disposed || _session is null || arguments[0]?.AsString() != _session)
            return;

        ReportTriggers(arguments[1]);
    }

    private void CloseSession()
    {
        var session = _session;
        var hadBindings = _bound.Count > 0;
        _session = null;
        _bound = [];
        if (hadBindings)
            _onTriggers(new Dictionary<HotkeyAction, string>());
        if (session is null)
            return;

        try
        {
            _bus.Call(new DBusCall(Destination, session, SessionInterface, "Close", "()"));
        }
        catch (DBusException exception)
        {
            Log.Debug(exception, "Tript.Shell: the global shortcuts session did not close cleanly");
        }
    }

    private void ReportTriggers(GVariantNode? shortcuts)
    {
        if (shortcuts is null)
            return;

        var triggers = new Dictionary<HotkeyAction, string>();
        foreach (var shortcut in shortcuts.Items)
        {
            var id = shortcut[0]?.AsString();
            var trigger = shortcut[1]?.Lookup("trigger_description")?.AsString();
            Log.Information("Tript.Shell: the desktop bound the {Shortcut} shortcut to {Trigger}",
                id, string.IsNullOrEmpty(trigger) ? "no trigger" : trigger);

            if (_bound.FirstOrDefault(bound => bound.Id == id) is { } known && !string.IsNullOrEmpty(trigger))
                triggers[known.Action] = trigger;
        }

        _onTriggers(triggers);
    }

    private string NextToken() => $"tript{++_tokens}";

    private static bool Same(IReadOnlyList<PortalShortcut> left, IReadOnlyList<PortalShortcut>? right) =>
        right is not null
        && left.Select(shortcut => (shortcut.Id, shortcut.Action))
            .SequenceEqual(right.Select(shortcut => (shortcut.Id, shortcut.Action)));

    private enum RequestKind
    {
        CreateSession,
        BindShortcuts,
    }

    private sealed class PendingRequest(RequestKind kind, IReadOnlyList<PortalShortcut> shortcuts)
    {
        internal RequestKind Kind { get; } = kind;

        internal IReadOnlyList<PortalShortcut> Shortcuts { get; } = shortcuts;

        internal IDisposable? Subscription { get; set; }
    }
}
