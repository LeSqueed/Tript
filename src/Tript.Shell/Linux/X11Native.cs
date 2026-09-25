// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Shell.Linux;

internal static unsafe partial class X11Native
{
    private const string X11 = "libX11.so.6";
    private const string LibC = "libc.so.6";

    internal const int KeyPress = 2;
    internal const int KeyRelease = 3;
    internal const int GrabModeAsync = 1;
    internal const int PollIn = 0x0001;
    internal const int XEventSize = 192;

    [StructLayout(LayoutKind.Sequential)]
    internal struct XKeyEvent
    {
        internal int Type;
        internal nuint Serial;
        internal int SendEvent;
        internal IntPtr Display;
        internal nuint Window;
        internal nuint Root;
        internal nuint Subwindow;
        internal nuint Time;
        internal int X;
        internal int Y;
        internal int XRoot;
        internal int YRoot;
        internal uint State;
        internal uint Keycode;
        internal int SameScreen;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XErrorEvent
    {
        internal int Type;
        internal IntPtr Display;
        internal nuint ResourceId;
        internal nuint Serial;
        internal byte ErrorCode;
        internal byte RequestCode;
        internal byte MinorCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollDescriptor
    {
        internal int Descriptor;
        internal short Events;
        internal short ReturnedEvents;
    }

    [LibraryImport(X11)]
    internal static partial IntPtr XOpenDisplay(IntPtr name);

    [LibraryImport(X11)]
    internal static partial int XCloseDisplay(IntPtr display);

    [LibraryImport(X11)]
    internal static partial nuint XDefaultRootWindow(IntPtr display);

    [LibraryImport(X11)]
    internal static partial int XConnectionNumber(IntPtr display);

    [LibraryImport(X11)]
    internal static partial byte XKeysymToKeycode(IntPtr display, nuint keysym);

    [LibraryImport(X11)]
    internal static partial int XGrabKey(IntPtr display, int keycode, uint modifiers, nuint grabWindow,
        int ownerEvents, int pointerMode, int keyboardMode);

    [LibraryImport(X11)]
    internal static partial int XUngrabKey(IntPtr display, int keycode, uint modifiers, nuint grabWindow);

    [LibraryImport(X11)]
    internal static partial int XSync(IntPtr display, int discard);

    [LibraryImport(X11)]
    internal static partial int XPending(IntPtr display);

    [LibraryImport(X11)]
    internal static partial int XNextEvent(IntPtr display, byte* eventReturn);

    [LibraryImport(X11)]
    internal static partial int XkbSetDetectableAutoRepeat(IntPtr display, int detectable, IntPtr supported);

    [LibraryImport(X11)]
    internal static partial IntPtr XSetErrorHandler(delegate* unmanaged<IntPtr, XErrorEvent*, int> handler);

    [LibraryImport(LibC, SetLastError = true)]
    internal static partial int pipe(int* descriptors);

    [LibraryImport(LibC, SetLastError = true)]
    internal static partial int poll(PollDescriptor* descriptors, nuint count, int timeoutMilliseconds);

    [LibraryImport(LibC, SetLastError = true)]
    internal static partial nint read(int descriptor, byte* buffer, nuint count);

    [LibraryImport(LibC, SetLastError = true)]
    internal static partial nint write(int descriptor, byte* buffer, nuint count);

    [LibraryImport(LibC)]
    internal static partial int close(int descriptor);
}
