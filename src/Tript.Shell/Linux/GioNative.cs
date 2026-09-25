// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Shell.Linux;

internal static unsafe partial class GioNative
{
    private const string Gio = "libgio-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    internal const int SessionBus = 2;
    internal const int AuthenticationClient = 1 << 0;
    internal const int MessageBusConnection = 1 << 3;
    internal const int CallFlagsNone = 0;
    internal const int SignalFlagsNone = 0;
    internal const int SourceRemove = 0;
    internal const int DefaultPriority = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct GError
    {
        internal uint Domain;
        internal int Code;
        internal IntPtr Message;
    }

    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr g_dbus_address_get_for_bus_sync(int busType, IntPtr cancellable, GError** error);

    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr g_dbus_connection_new_for_address_sync(string address, int flags,
        IntPtr observer, IntPtr cancellable, GError** error);

    [LibraryImport(Gio)]
    internal static partial IntPtr g_dbus_connection_get_unique_name(IntPtr connection);

    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr g_dbus_connection_call_sync(IntPtr connection, string? busName,
        string objectPath, string interfaceName, string methodName, IntPtr parameters, IntPtr replyType, int flags,
        int timeoutMilliseconds, IntPtr cancellable, GError** error);

    [LibraryImport(Gio)]
    internal static partial int g_dbus_connection_close_sync(IntPtr connection, IntPtr cancellable,
        GError** error);

    [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint g_dbus_connection_signal_subscribe(IntPtr connection, string? sender,
        string? interfaceName, string? member, string? objectPath, string? arg0, int flags,
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> callback,
        IntPtr userData, delegate* unmanaged<IntPtr, void> userDataFree);

    [LibraryImport(Gio)]
    internal static partial void g_dbus_connection_signal_unsubscribe(IntPtr connection, uint subscriptionId);

    [LibraryImport(GLib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr g_variant_parse(IntPtr type, string text, IntPtr limit, IntPtr endPointer,
        GError** error);

    [LibraryImport(GLib)]
    internal static partial IntPtr g_variant_print(IntPtr value, int typeAnnotate);

    [LibraryImport(GLib)]
    internal static partial void g_variant_unref(IntPtr value);

    [LibraryImport(GLib)]
    internal static partial void g_error_free(GError* error);

    [LibraryImport(GLib)]
    internal static partial void g_free(IntPtr memory);

    [LibraryImport(GLib)]
    internal static partial IntPtr g_main_context_new();

    [LibraryImport(GLib)]
    internal static partial void g_main_context_unref(IntPtr context);

    [LibraryImport(GLib)]
    internal static partial void g_main_context_push_thread_default(IntPtr context);

    [LibraryImport(GLib)]
    internal static partial void g_main_context_pop_thread_default(IntPtr context);

    [LibraryImport(GLib)]
    internal static partial void g_main_context_invoke_full(IntPtr context, int priority,
        delegate* unmanaged<IntPtr, int> function, IntPtr data, delegate* unmanaged<IntPtr, void> notify);

    [LibraryImport(GLib)]
    internal static partial IntPtr g_main_loop_new(IntPtr context, int isRunning);

    [LibraryImport(GLib)]
    internal static partial void g_main_loop_run(IntPtr loop);

    [LibraryImport(GLib)]
    internal static partial void g_main_loop_quit(IntPtr loop);

    [LibraryImport(GLib)]
    internal static partial void g_main_loop_unref(IntPtr loop);

    [LibraryImport(GObject)]
    internal static partial void g_object_unref(IntPtr instance);

    internal static string? Utf8(IntPtr text) => text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);

    internal static string TakeUtf8(IntPtr text)
    {
        try
        {
            return Utf8(text) ?? string.Empty;
        }
        finally
        {
            g_free(text);
        }
    }

    internal static DBusException TakeError(GError* error, string what)
    {
        if (error is null)
            return new DBusException($"{what} failed without saying why.");

        try
        {
            return new DBusException($"{what} failed: {Utf8(error->Message)}");
        }
        finally
        {
            g_error_free(error);
        }
    }
}
