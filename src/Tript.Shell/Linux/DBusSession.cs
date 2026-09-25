// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Shell.Linux;

internal sealed class DBusException(string message) : Exception(message);

internal sealed record DBusCall(string Destination, string ObjectPath, string Interface, string Method,
    string Parameters);

internal sealed record DBusSignalMatch(string? Sender, string Interface, string Member, string? ObjectPath);

internal interface IDBusSession
{
    string UniqueName { get; }

    GVariantNode Call(DBusCall call);

    IDisposable Subscribe(DBusSignalMatch match, Action<GVariantNode> onSignal);
}

internal static class DBusProperties
{
    internal static GVariantNode Get(IDBusSession session, string destination, string objectPath,
        string interfaceName, string property) =>
        session.Call(new DBusCall(destination, objectPath, "org.freedesktop.DBus.Properties", "Get",
            GVariantText.Tuple(GVariantText.String(interfaceName), GVariantText.String(property))))[0]?.Unboxed
        ?? GVariantNode.None;
}
