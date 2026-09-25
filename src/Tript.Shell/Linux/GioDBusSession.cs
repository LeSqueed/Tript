// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Serilog;

namespace Tript.Shell.Linux;

internal sealed unsafe class GioDBusSession : IDBusSession, IDisposable
{
    private const int CallTimeoutMilliseconds = 5000;

    private readonly GMainLoopThread? _signals;
    private IntPtr _connection;

    private GioDBusSession(IntPtr connection, GMainLoopThread? signals)
    {
        _connection = connection;
        _signals = signals;
        UniqueName = GioNative.Utf8(GioNative.g_dbus_connection_get_unique_name(connection)) ?? string.Empty;
    }

    public string UniqueName { get; }

    internal static GioDBusSession Open(GMainLoopThread? signals)
    {
        GioNative.GError* error = null;
        var address = GioNative.g_dbus_address_get_for_bus_sync(GioNative.SessionBus, IntPtr.Zero, &error);
        if (address == IntPtr.Zero)
            throw GioNative.TakeError(error, "Finding the session bus");

        var addressText = GioNative.TakeUtf8(address);
        var connection = GioNative.g_dbus_connection_new_for_address_sync(addressText,
            GioNative.AuthenticationClient | GioNative.MessageBusConnection, IntPtr.Zero, IntPtr.Zero, &error);
        if (connection == IntPtr.Zero)
            throw GioNative.TakeError(error, "Connecting to the session bus");

        return new GioDBusSession(connection, signals);
    }

    public GVariantNode Call(DBusCall call)
    {
        GioNative.GError* error = null;
        var parameters = GioNative.g_variant_parse(IntPtr.Zero, call.Parameters, IntPtr.Zero, IntPtr.Zero, &error);
        if (parameters == IntPtr.Zero)
            throw GioNative.TakeError(error, $"Encoding the {call.Method} parameters");

        IntPtr reply;
        try
        {
            reply = GioNative.g_dbus_connection_call_sync(Connection, call.Destination, call.ObjectPath,
                call.Interface, call.Method, parameters, IntPtr.Zero, GioNative.CallFlagsNone,
                CallTimeoutMilliseconds, IntPtr.Zero, &error);
        }
        finally
        {
            GioNative.g_variant_unref(parameters);
        }

        if (reply == IntPtr.Zero)
            throw GioNative.TakeError(error, $"{call.Interface}.{call.Method}");

        try
        {
            return GVariantPrinted.Parse(GioNative.TakeUtf8(GioNative.g_variant_print(reply, 1)));
        }
        finally
        {
            GioNative.g_variant_unref(reply);
        }
    }

    public IDisposable Subscribe(DBusSignalMatch match, Action<GVariantNode> onSignal)
    {
        var signals = _signals
            ?? throw new InvalidOperationException("This bus session was opened without a signal thread.");

        var id = signals.Invoke(() =>
        {
            var handle = GCHandle.Alloc(onSignal);
            return GioNative.g_dbus_connection_signal_subscribe(Connection, match.Sender, match.Interface,
                match.Member, match.ObjectPath, null, GioNative.SignalFlagsNone, &OnSignal,
                GCHandle.ToIntPtr(handle), &ReleaseHandler);
        });
        return new Subscription(this, id);
    }

    public void Dispose()
    {
        var connection = Interlocked.Exchange(ref _connection, IntPtr.Zero);
        if (connection == IntPtr.Zero)
            return;

        GioNative.GError* error = null;
        if (GioNative.g_dbus_connection_close_sync(connection, IntPtr.Zero, &error) == 0)
            Log.Debug(GioNative.TakeError(error, "Closing the session bus connection"), "Tript.Shell: bus close");
        GioNative.g_object_unref(connection);
    }

    private IntPtr Connection => _connection != IntPtr.Zero
        ? _connection
        : throw new ObjectDisposedException(nameof(GioDBusSession));

    [UnmanagedCallersOnly]
    private static void OnSignal(IntPtr connection, IntPtr sender, IntPtr objectPath, IntPtr interfaceName,
        IntPtr signalName, IntPtr parameters, IntPtr userData)
    {
        try
        {
            var handler = (Action<GVariantNode>)GCHandle.FromIntPtr(userData).Target!;
            handler(GVariantPrinted.Parse(GioNative.TakeUtf8(GioNative.g_variant_print(parameters, 1))));
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Tript.Shell: a desktop bus signal could not be handled");
        }
    }

    [UnmanagedCallersOnly]
    private static void ReleaseHandler(IntPtr userData) => GCHandle.FromIntPtr(userData).Free();

    private sealed class Subscription(GioDBusSession session, uint id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var connection = session._connection;
            if (connection != IntPtr.Zero)
                GioNative.g_dbus_connection_signal_unsubscribe(connection, id);
        }
    }
}
