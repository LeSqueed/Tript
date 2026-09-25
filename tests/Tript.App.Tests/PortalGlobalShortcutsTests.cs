// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Tript.Shell.Linux;
using Xunit;

namespace Tript.App.Tests;

public sealed class PortalGlobalShortcutsTests
{
    private const string SessionPath = "/org/freedesktop/portal/desktop/session/1_42/tript_session";

    private static readonly PortalShortcut Toggle =
        new("toggle-recording", HotkeyAction.ToggleRecording, "Start or stop recording", "CTRL+SHIFT+F9");

    private static readonly PortalShortcut Bookmark =
        new("manual-bookmark", HotkeyAction.ManualBookmark, "Bookmark the current moment", "CTRL+ALT+b");

    private readonly FakePortalBus _bus = new();
    private readonly List<HotkeyAction> _activated = [];
    private readonly List<string> _failures = [];
    private readonly List<IReadOnlyDictionary<HotkeyAction, string>> _triggers = [];

    private PortalGlobalShortcuts CreatePortal() => new(_bus, _activated.Add, _failures.Add, _triggers.Add);

    [Fact]
    public void TheRequestPath_IsDerivedFromTheUniqueNameAndToken() =>
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42/tript7",
            PortalGlobalShortcuts.RequestPath(":1.42", "tript7"));

    [Fact]
    public void APortalShortcut_IsBoundWithItsPreferredTrigger()
    {
        using var portal = CreatePortal();

        portal.Bind([Toggle]);
        var create = _bus.LastCall("CreateSession");
        Assert.NotNull(_bus.Parameters(create)[0]!.Lookup("session_handle_token")?.AsString());
        _bus.Respond(create, 0, $"{{'session_handle': <'{SessionPath}'>}}");

        var bind = _bus.Parameters(_bus.LastCall("BindShortcuts"));
        Assert.Equal(GVariantKind.ObjectPath, bind[0]!.Kind);
        Assert.Equal(SessionPath, bind[0]!.AsString());
        var shortcut = Assert.Single(bind[1]!.Items);
        Assert.Equal("toggle-recording", shortcut[0]!.AsString());
        Assert.Equal("CTRL+SHIFT+F9", shortcut[1]!.Lookup("preferred_trigger")!.AsString());
        Assert.Equal("Start or stop recording", shortcut[1]!.Lookup("description")!.AsString());
        Assert.Equal(string.Empty, bind[2]!.AsString());
        Assert.NotNull(bind[3]!.Lookup("handle_token"));
    }

    [Fact]
    public void AnActivatedShortcutOfOurSession_RunsItsAction()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle, Bookmark]);

        _bus.Activate(SessionPath, "manual-bookmark");

        Assert.Equal([HotkeyAction.ManualBookmark], _activated);
    }

    [Fact]
    public void AnActivatedShortcutOfAnotherSession_IsIgnored()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);

        _bus.Activate("/org/freedesktop/portal/desktop/session/1_99/someone_else", "toggle-recording");

        Assert.Empty(_activated);
    }

    [Fact]
    public void AShortcutActivatedBeforeTheDesktopConfirmedTheBinding_IsIgnored()
    {
        using var portal = CreatePortal();
        portal.Bind([Toggle]);
        _bus.Respond(_bus.LastCall("CreateSession"), 0, $"{{'session_handle': <'{SessionPath}'>}}");

        _bus.Activate(SessionPath, "toggle-recording");

        Assert.Empty(_activated);
    }

    [Fact]
    public void RebindingTheSameShortcuts_DoesNotAskTheDesktopAgain()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);
        var callsBefore = _bus.Calls.Count;

        portal.Bind([Toggle with { }]);

        Assert.Equal(callsBefore, _bus.Calls.Count);
    }

    [Fact]
    public void AChangedPreferredTriggerAlone_KeepsTheSessionTheDesktopAlreadyConfirmed()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);
        var callsBefore = _bus.Calls.Count;

        portal.Bind([Toggle with { Trigger = "CTRL+ALT+r" }]);

        Assert.Equal(callsBefore, _bus.Calls.Count);
        Assert.Equal(SessionPath, portal.SessionHandle);
    }

    [Fact]
    public void AnAddedShortcut_ClosesTheOldSessionAndBindsTheWholeSetWithTheLatestTriggers()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);

        portal.Bind([Toggle with { Trigger = "CTRL+ALT+r" }, Bookmark]);

        Assert.Contains(_bus.Calls, call => call.Method == "Close" && call.ObjectPath == SessionPath);
        _bus.Respond(_bus.LastCall("CreateSession"), 0, $"{{'session_handle': <'{SessionPath}2'>}}");
        var bind = _bus.Parameters(_bus.LastCall("BindShortcuts"));
        Assert.Equal(["toggle-recording", "manual-bookmark"], bind[1]!.Items.Select(item => item[0]!.AsString()));
        Assert.Equal("CTRL+ALT+r", bind[1]![0]![1]!.Lookup("preferred_trigger")!.AsString());
    }

    [Fact]
    public void TheTriggersTheDesktopAssigned_AreReportedPerAction()
    {
        using var portal = CreatePortal();
        portal.Bind([Toggle, Bookmark]);
        _bus.Respond(_bus.LastCall("CreateSession"), 0, $"{{'session_handle': <'{SessionPath}'>}}");

        _bus.Respond(_bus.LastCall("BindShortcuts"), 0,
            "{'shortcuts': <[('toggle-recording', {'trigger_description': <'Meta+F9'>}), " +
            "('manual-bookmark', {'description': <'Bookmark the current moment'>})]>}");

        var reported = Assert.Single(_triggers);
        Assert.Equal("Meta+F9", reported[HotkeyAction.ToggleRecording]);
        Assert.False(reported.ContainsKey(HotkeyAction.ManualBookmark));
    }

    [Fact]
    public void ShortcutsChangedInTheDesktopsSettings_AreReportedForOurSessionOnly()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);
        _triggers.Clear();

        _bus.ShortcutsChanged("/org/freedesktop/portal/desktop/session/1_99/someone_else", "toggle-recording", "F1");
        _bus.ShortcutsChanged(SessionPath, "toggle-recording", "Ctrl+Alt+R");

        var reported = Assert.Single(_triggers);
        Assert.Equal("Ctrl+Alt+R", reported[HotkeyAction.ToggleRecording]);
    }

    [Fact]
    public void ClosingTheSession_ReportsThatNoTriggersAreBound()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);
        _triggers.Clear();

        portal.Bind([]);

        Assert.Empty(Assert.Single(_triggers));
    }

    [Fact]
    public void ConfiguringShortcuts_AsksTheDesktopToOpenItsSettingsForOurSession()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);

        Assert.True(portal.Configure());

        var configure = _bus.Parameters(_bus.LastCall("ConfigureShortcuts"));
        Assert.Equal(GVariantKind.ObjectPath, configure[0]!.Kind);
        Assert.Equal(SessionPath, configure[0]!.AsString());
        Assert.Equal(string.Empty, configure[1]!.AsString());
    }

    [Fact]
    public void ConfiguringShortcuts_WithoutASession_AsksNothing()
    {
        using var portal = CreatePortal();

        Assert.False(portal.Configure());
        Assert.DoesNotContain(_bus.Calls, call => call.Method == "ConfigureShortcuts");
    }

    [Fact]
    public void ADesktopThatRefusesToConfigure_IsReportedAsNotOpened()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);
        _bus.Refuse = "ConfigureShortcuts";

        Assert.False(portal.Configure());
    }

    [Fact]
    public void ThePortalVersion_IsReadAndZeroWhenThePortalIsMissing()
    {
        _bus.VersionReply = "(<uint32 2>,)";
        Assert.Equal(2u, PortalGlobalShortcuts.Version(_bus));

        _bus.VersionReply = null;
        Assert.Equal(0u, PortalGlobalShortcuts.Version(_bus));
    }

    [Fact]
    public void ShortcutsChangedWhileTheDesktopIsStillAnswering_AreBoundOnceItAnswers()
    {
        using var portal = CreatePortal();
        portal.Bind([Toggle]);
        var create = _bus.LastCall("CreateSession");

        portal.Bind([Toggle, Bookmark]);
        Assert.Single(_bus.Calls, call => call.Method == "CreateSession");

        _bus.Respond(create, 0, $"{{'session_handle': <'{SessionPath}'>}}");
        var bind = _bus.Parameters(_bus.LastCall("BindShortcuts"));
        Assert.Equal(["toggle-recording", "manual-bookmark"], bind[1]!.Items.Select(item => item[0]!.AsString()));
    }

    [Fact]
    public void ACancelledBinding_IsReportedOnceAndNotRetried()
    {
        using var portal = CreatePortal();
        portal.Bind([Toggle]);
        _bus.Respond(_bus.LastCall("CreateSession"), 0, $"{{'session_handle': <'{SessionPath}'>}}");

        _bus.Respond(_bus.LastCall("BindShortcuts"), 1, "@a{sv} {}");
        portal.Bind([Toggle]);

        Assert.Single(_failures);
        Assert.Single(_bus.Calls, call => call.Method == "BindShortcuts");
    }

    [Fact]
    public void ARefusedCall_IsReportedAsAFailure()
    {
        _bus.Refuse = "CreateSession";
        using var portal = CreatePortal();

        portal.Bind([Toggle]);

        Assert.Single(_failures);
    }

    [Fact]
    public void DisablingEveryShortcut_ClosesTheSession()
    {
        using var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);

        portal.Bind([]);

        Assert.Contains(_bus.Calls, call => call.Method == "Close" && call.ObjectPath == SessionPath);
        Assert.Null(portal.SessionHandle);
        _bus.Activate(SessionPath, "toggle-recording");
        Assert.Empty(_activated);
    }

    [Fact]
    public void ADesktopThatAnswersOnAnotherRequestPath_IsStillFollowed()
    {
        _bus.ReplyWithRequestPath = token => $"/org/freedesktop/portal/desktop/request/legacy/{token}";
        using var portal = CreatePortal();

        portal.Bind([Toggle]);
        var create = _bus.LastCall("CreateSession");
        _bus.Respond(create, 0, $"{{'session_handle': <'{SessionPath}'>}}");

        Assert.Single(_bus.Calls, call => call.Method == "BindShortcuts");
    }

    [Fact]
    public void Disposing_ClosesTheSessionAndStopsListening()
    {
        var portal = CreatePortal();
        BindAndConfirm(portal, [Toggle]);

        portal.Dispose();

        Assert.Contains(_bus.Calls, call => call.Method == "Close" && call.ObjectPath == SessionPath);
        Assert.Equal(0, _bus.ActiveSubscriptions);
        Assert.Contains(_bus.Calls, call => call.Method == "Close");
    }

    [Fact]
    public void ThePortal_IsAvailableOnlyWhenItReportsAVersion()
    {
        _bus.VersionReply = "(<uint32 1>,)";
        Assert.True(PortalGlobalShortcuts.IsAvailable(_bus));

        _bus.VersionReply = null;
        Assert.False(PortalGlobalShortcuts.IsAvailable(_bus));
    }

    private void BindAndConfirm(PortalGlobalShortcuts portal, IReadOnlyList<PortalShortcut> shortcuts)
    {
        portal.Bind(shortcuts);
        _bus.Respond(_bus.LastCall("CreateSession"), 0, $"{{'session_handle': <'{SessionPath}'>}}");
        _bus.Respond(_bus.LastCall("BindShortcuts"), 0, "@a{sv} {}");
    }

    private sealed class FakePortalBus : IDBusSession
    {
        private readonly List<Subscription> _subscriptions = [];

        public string UniqueName => ":1.42";

        internal List<DBusCall> Calls { get; } = [];

        internal string? Refuse { get; set; }

        internal string? VersionReply { get; set; }

        internal Func<string, string>? ReplyWithRequestPath { get; set; }

        internal int ActiveSubscriptions => _subscriptions.Count(subscription => !subscription.Disposed);

        public GVariantNode Call(DBusCall call)
        {
            Calls.Add(call);
            if (call.Method == Refuse)
                throw new DBusException("refused");

            if (call.Method == "Get")
            {
                return VersionReply is null
                    ? throw new DBusException("no such interface")
                    : GVariantPrinted.Parse(VersionReply);
            }

            if (Token(call) is not { } token)
                return GVariantPrinted.Parse("()");

            var path = ReplyWithRequestPath?.Invoke(token) ?? PortalGlobalShortcuts.RequestPath(UniqueName, token);
            return GVariantPrinted.Parse($"(objectpath '{path}',)");
        }

        public IDisposable Subscribe(DBusSignalMatch match, Action<GVariantNode> onSignal)
        {
            var subscription = new Subscription(match, onSignal);
            _subscriptions.Add(subscription);
            return subscription;
        }

        internal DBusCall LastCall(string method) => Calls.Last(call => call.Method == method);

        internal GVariantNode Parameters(DBusCall call) => GVariantPrinted.Parse(call.Parameters);

        internal void Respond(DBusCall call, uint response, string results)
        {
            var token = Token(call)!;
            var path = ReplyWithRequestPath?.Invoke(token) ?? PortalGlobalShortcuts.RequestPath(UniqueName, token);
            Emit(PortalGlobalShortcuts.RequestInterface, "Response", path, $"(uint32 {response}, {results})");
        }

        internal void ShortcutsChanged(string session, string shortcutId, string trigger) =>
            Emit(PortalGlobalShortcuts.Interface, "ShortcutsChanged", PortalGlobalShortcuts.DesktopPath,
                $"(objectpath '{session}', [('{shortcutId}', {{'trigger_description': <'{trigger}'>}})])");

        internal void Activate(string session, string shortcutId) =>
            Emit(PortalGlobalShortcuts.Interface, "Activated", PortalGlobalShortcuts.DesktopPath,
                $"(objectpath '{session}', '{shortcutId}', uint64 1, @a{{sv}} {{}})");

        private void Emit(string interfaceName, string member, string path, string arguments)
        {
            foreach (var subscription in _subscriptions.ToList())
            {
                if (!subscription.Disposed && subscription.Match.Interface == interfaceName
                    && subscription.Match.Member == member && subscription.Match.ObjectPath == path)
                {
                    subscription.Handler(GVariantPrinted.Parse(arguments));
                }
            }
        }

        private string? Token(DBusCall call)
        {
            var parameters = GVariantPrinted.Parse(call.Parameters);
            foreach (var item in parameters.Items)
            {
                if (item.Kind == GVariantKind.Dictionary && item.Lookup("handle_token")?.AsString() is { } token)
                    return token;
            }

            return null;
        }

        private sealed class Subscription(DBusSignalMatch match, Action<GVariantNode> handler) : IDisposable
        {
            internal DBusSignalMatch Match { get; } = match;

            internal Action<GVariantNode> Handler { get; } = handler;

            internal bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }
    }
}
