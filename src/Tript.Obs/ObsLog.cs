// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tript.Obs.Interop;

namespace Tript.Obs;

public enum ObsLogLevel
{
    Error = 100,
    Warning = 200,
    Info = 300,
    Debug = 400
}

public delegate void ObsLogHandler(ObsLogLevel level, string message);

public static class ObsLog
{
    private static readonly Lock Gate = new();
    private static ObsLogHandler? _handler;
    private static Scope? _installed;

    public static IDisposable Install(ObsLogHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (Gate)
        {
            if (_installed is not null)
                throw new InvalidOperationException(
                    "An OBS log handler is already installed. libobs has one handler per process; dispose the existing scope first.");

            ObsLibrary.EnsureLoaded();
            ObsNative.base_get_log_handler(out var previousHandler, out var previousParameter);

            Volatile.Write(ref _handler, handler);
            unsafe
            {
                ObsNative.base_set_log_handler(&OnNativeLog, nint.Zero);
            }

            var scope = new Scope(previousHandler, previousParameter);
            _installed = scope;
            return scope;
        }
    }

    // Runs during obs_shutdown too: allocate nothing that needs a live OBS context.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeLog(int level, nint format, nint arguments, nint parameter)
    {
        var handler = Volatile.Read(ref _handler);
        if (handler is null)
            return;

        try
        {
            handler(MapLevel(level), VaListFormatter.Format(format, arguments));
        }
        catch
        {
            // An exception crossing back into libobs terminates the process.
        }
    }

    private static ObsLogLevel MapLevel(int level) => level switch
    {
        <= (int)ObsLogLevel.Error => ObsLogLevel.Error,
        <= (int)ObsLogLevel.Warning => ObsLogLevel.Warning,
        <= (int)ObsLogLevel.Info => ObsLogLevel.Info,
        _ => ObsLogLevel.Debug
    };

    private sealed class Scope : IDisposable
    {
        private readonly nint _previousHandler;
        private readonly nint _previousParameter;
        private int _disposed;

        internal Scope(nint previousHandler, nint previousParameter)
        {
            _previousHandler = previousHandler;
            _previousParameter = previousParameter;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (Gate)
            {
                unsafe
                {
                    ObsNative.base_set_log_handler(
                        (delegate* unmanaged[Cdecl]<int, nint, nint, nint, void>)_previousHandler, _previousParameter);
                }

                Volatile.Write(ref _handler, null);
                _installed = null;
            }
        }
    }
}
