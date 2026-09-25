// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Serilog;
using Tript.Settings;

namespace Tript.Shell.Linux;

internal sealed unsafe class X11Hotkeys : IDisposable
{
    private const byte BadAccess = 10;

    private static IntPtr s_trappedDisplay;
    private static IntPtr s_previousErrorHandler;
    private static byte s_trappedError;

    private readonly IntPtr _display;
    private readonly nuint _root;
    private readonly int _wakeRead;
    private readonly int _wakeWrite;
    private readonly Action<HotkeyAction> _onHotkey;
    private readonly Action<string> _onRegistrationFailed;
    private readonly Thread _thread;
    private readonly Lock _gate = new();
    private readonly Dictionary<(uint Keycode, uint Mask), HotkeyAction> _grabbed = new();
    private readonly X11RepeatFilter _repeats = new();
    private IReadOnlyList<LinuxHotkey>? _requested;
    private volatile bool _disposed;

    private X11Hotkeys(IntPtr display, int wakeRead, int wakeWrite, Action<HotkeyAction> onHotkey,
        Action<string> onRegistrationFailed)
    {
        _display = display;
        _root = X11Native.XDefaultRootWindow(display);
        _wakeRead = wakeRead;
        _wakeWrite = wakeWrite;
        _onHotkey = onHotkey;
        _onRegistrationFailed = onRegistrationFailed;
        X11Native.XkbSetDetectableAutoRepeat(display, 1, IntPtr.Zero);
        _thread = new Thread(Run) { IsBackground = true, Name = "Tript X11 hotkeys" };
        _thread.Start();
    }

    internal static X11Hotkeys? TryOpen(Action<HotkeyAction> onHotkey, Action<string> onRegistrationFailed)
    {
        IntPtr display;
        try
        {
            display = X11Native.XOpenDisplay(IntPtr.Zero);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(exception, "Tript.Shell: Xlib is not available for global hotkeys");
            return null;
        }

        if (display == IntPtr.Zero)
            return null;

        var descriptors = stackalloc int[2];
        if (X11Native.pipe(descriptors) != 0)
        {
            X11Native.XCloseDisplay(display);
            return null;
        }

        return new X11Hotkeys(display, descriptors[0], descriptors[1], onHotkey, onRegistrationFailed);
    }

    internal void Apply(IReadOnlyList<LinuxHotkey> hotkeys)
    {
        lock (_gate)
            _requested = hotkeys;
        Wake();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Wake();
        if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(2)))
        {
            X11Native.close(_wakeRead);
            X11Native.close(_wakeWrite);
        }
    }

    private void Wake()
    {
        byte signal = 1;
        X11Native.write(_wakeWrite, &signal, 1);
    }

    private void Run()
    {
        var eventBuffer = stackalloc byte[X11Native.XEventSize];
        var descriptors = stackalloc X11Native.PollDescriptor[2];
        descriptors[0] = new X11Native.PollDescriptor
        {
            Descriptor = X11Native.XConnectionNumber(_display),
            Events = X11Native.PollIn,
        };
        descriptors[1] = new X11Native.PollDescriptor { Descriptor = _wakeRead, Events = X11Native.PollIn };

        try
        {
            while (!_disposed)
            {
                ApplyRequested();
                while (!_disposed && X11Native.XPending(_display) > 0)
                {
                    X11Native.XNextEvent(_display, eventBuffer);
                    Dispatch((X11Native.XKeyEvent*)eventBuffer);
                }

                if (_disposed)
                    break;

                descriptors[0].ReturnedEvents = 0;
                descriptors[1].ReturnedEvents = 0;
                if (X11Native.poll(descriptors, 2, -1) < 0 && Marshal.GetLastPInvokeError() != 4)
                    break;

                if ((descriptors[1].ReturnedEvents & X11Native.PollIn) != 0)
                    DrainWakeups();
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Tript.Shell: the X11 hotkey loop stopped");
        }
        finally
        {
            UngrabAll();
            X11Native.XCloseDisplay(_display);
        }
    }

    private void Dispatch(X11Native.XKeyEvent* keyEvent)
    {
        if (keyEvent->Type == X11Native.KeyRelease)
        {
            _repeats.Released(keyEvent->Keycode);
            return;
        }

        if (keyEvent->Type != X11Native.KeyPress || !_repeats.Pressed(keyEvent->Keycode))
            return;

        if (_grabbed.TryGetValue((keyEvent->Keycode, X11KeyGrabs.Significant(keyEvent->State)), out var action))
            _onHotkey(action);
    }

    private void DrainWakeups()
    {
        var buffer = stackalloc byte[64];
        X11Native.read(_wakeRead, buffer, 64);
    }

    private void ApplyRequested()
    {
        IReadOnlyList<LinuxHotkey>? requested;
        lock (_gate)
        {
            requested = _requested;
            _requested = null;
        }

        if (requested is null)
            return;

        UngrabAll();
        foreach (var hotkey in requested)
        {
            var keycode = X11Native.XKeysymToKeycode(_display, hotkey.Key.Value);
            if (keycode == 0)
            {
                Log.Warning("Tript.Shell: this keyboard has no key for the {Action} hotkey ({Key})",
                    hotkey.Action, hotkey.Key.Name);
                continue;
            }

            var mask = X11KeyGrabs.FromBindingModifiers(hotkey.Modifiers);
            if (!Grab(keycode, mask))
            {
                _onRegistrationFailed(
                    $"Could not register the {hotkey.Action} hotkey. Another application may already be using it.");
                continue;
            }

            _grabbed[(keycode, mask)] = hotkey.Action;
        }
    }

    private bool Grab(uint keycode, uint mask)
    {
        s_trappedError = 0;
        s_trappedDisplay = _display;
        s_previousErrorHandler = X11Native.XSetErrorHandler(&OnXError);
        try
        {
            foreach (var grabMask in X11KeyGrabs.GrabMasks(mask))
            {
                X11Native.XGrabKey(_display, (int)keycode, grabMask, _root, 0, X11Native.GrabModeAsync,
                    X11Native.GrabModeAsync);
            }

            X11Native.XSync(_display, 0);
            if (s_trappedError != BadAccess && s_trappedError != 0)
                Log.Warning("Tript.Shell: X11 reported error {Code} while grabbing a hotkey", s_trappedError);
            if (s_trappedError == 0)
                return true;

            foreach (var grabMask in X11KeyGrabs.GrabMasks(mask))
                X11Native.XUngrabKey(_display, (int)keycode, grabMask, _root);
            X11Native.XSync(_display, 0);
            return false;
        }
        finally
        {
            X11Native.XSetErrorHandler(
                (delegate* unmanaged<IntPtr, X11Native.XErrorEvent*, int>)s_previousErrorHandler);
            s_trappedDisplay = IntPtr.Zero;
        }
    }

    private void UngrabAll()
    {
        foreach (var (keycode, mask) in _grabbed.Keys)
        {
            foreach (var grabMask in X11KeyGrabs.GrabMasks(mask))
                X11Native.XUngrabKey(_display, (int)keycode, grabMask, _root);
        }

        _grabbed.Clear();
        _repeats.Clear();
        X11Native.XSync(_display, 0);
    }

    [UnmanagedCallersOnly]
    private static int OnXError(IntPtr display, X11Native.XErrorEvent* error)
    {
        if (display == s_trappedDisplay)
        {
            s_trappedError = error->ErrorCode;
            return 0;
        }

        var previous = (delegate* unmanaged<IntPtr, X11Native.XErrorEvent*, int>)s_previousErrorHandler;
        return previous is null ? 0 : previous(display, error);
    }
}
