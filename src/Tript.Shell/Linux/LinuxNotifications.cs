// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.App;

namespace Tript.Shell.Linux;

internal sealed class LinuxNotifications : IDisposable
{
    internal const string Destination = "org.freedesktop.Notifications";
    internal const string ObjectPath = "/org/freedesktop/Notifications";
    internal const string Interface = "org.freedesktop.Notifications";
    internal const string ApplicationName = "Tript";

    private readonly GioDBusSession _session;
    private readonly bool _markup;
    private readonly SerialWorkQueue<DBusCall> _queue;

    private LinuxNotifications(GioDBusSession session, bool markup)
    {
        _session = session;
        _markup = markup;
        _queue = new SerialWorkQueue<DBusCall>(call => _session.Call(call),
            (_, exception) => Log.Warning(exception, "Tript.Shell: a desktop notification could not be shown"));
    }

    internal static LinuxNotifications? TryOpen()
    {
        GioDBusSession? session = null;
        try
        {
            session = GioDBusSession.Open(signals: null);
            var capabilities = session.Call(new DBusCall(Destination, ObjectPath, Interface, "GetCapabilities", "()"));
            var markup = capabilities[0]?.Items.Any(capability => capability.AsString() == "body-markup") == true;
            return new LinuxNotifications(session, markup);
        }
        catch (Exception exception) when (exception is DBusException or DllNotFoundException
                                              or EntryPointNotFoundException or FormatException)
        {
            Log.Information("Tript.Shell: desktop notifications are not available ({Reason})", exception.Message);
            session?.Dispose();
            return null;
        }
    }

    internal void Show(string title, string body, string? iconPath) =>
        _queue.Enqueue(NotifyCall(title, body, iconPath, _markup));

    internal static DBusCall NotifyCall(string title, string body, string? iconPath, bool markup) =>
        new(Destination, ObjectPath, Interface, "Notify", GVariantText.Tuple(
            GVariantText.String(ApplicationName),
            GVariantText.UInt32(0),
            GVariantText.String(iconPath ?? string.Empty),
            GVariantText.String(title),
            GVariantText.String(markup ? EscapeMarkup(body) : body),
            GVariantText.StringArray([]),
            GVariantText.VariantDictionary([new("suppress-sound", GVariantText.Boolean(true))]),
            GVariantText.Int32(-1)));

    internal static string EscapeMarkup(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    public void Dispose() => _session.Dispose();
}
