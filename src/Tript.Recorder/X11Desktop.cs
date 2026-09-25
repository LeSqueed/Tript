// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Serilog;

namespace Tript.Recorder;

internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    internal int Right => X + Width;

    internal int Bottom => Y + Height;
}

internal sealed record X11ActiveWindow(
    int? ProcessId,
    IReadOnlyList<ulong> StateAtoms,
    ulong FullscreenAtom,
    IReadOnlyList<ScreenRect> WindowRects,
    IReadOnlyList<ScreenRect> Monitors);

internal interface IX11Desktop : IDisposable
{
    bool IsConnected { get; }

    X11ActiveWindow? ReadActiveWindow();
}

internal static class X11Properties
{
    internal const int Format32 = 32;

    internal static IReadOnlyList<ulong> DecodeFormat32(ReadOnlySpan<byte> data, int format, int itemCount, int nativeLongSize)
    {
        if (format != Format32 || itemCount <= 0 || nativeLongSize is not (4 or 8))
            return [];

        var count = Math.Min(itemCount, data.Length / nativeLongSize);
        var values = new ulong[count];
        for (var index = 0; index < count; index++)
        {
            var item = data.Slice(index * nativeLongSize, nativeLongSize);
            values[index] = nativeLongSize == 8
                ? MemoryMarshal.Read<ulong>(item) & uint.MaxValue
                : MemoryMarshal.Read<uint>(item);
        }

        return values;
    }

    internal static ulong? DecodeWindow(IReadOnlyList<ulong> values)
        => values.Count > 0 && values[0] != 0 ? values[0] : null;

    internal static int? DecodeProcessId(IReadOnlyList<ulong> values)
        => values.Count > 0 && values[0] is > 0 and <= int.MaxValue ? (int)values[0] : null;
}

internal sealed unsafe class NativeX11Desktop : IX11Desktop
{
    private const string X11Library = "libX11.so.6";
    private const string RandRLibrary = "libXrandr.so.2";
    private const int Success = 0;
    private const nuint AtomType = 4;
    private const nuint CardinalType = 6;
    private const nuint WindowType = 33;
    private const int MaximumStateAtoms = 64;
    private static readonly object ErrorTrapGate = new();
    private static nint _trappedDisplay;
    private static nint _previousErrorHandler;
    private static int _trappedErrorCode;
    private static nint _lostDisplay;
    private static int _missingExitHandlerLogged;

    private readonly nint _display;
    private readonly nuint _root;
    private readonly nuint _activeWindowAtom;
    private readonly nuint _stateAtom;
    private readonly nuint _fullscreenAtom;
    private readonly nuint _processIdAtom;
    private readonly bool _hasMonitors;
    private bool _disposed;

    private NativeX11Desktop(nint display)
    {
        _display = display;
        _root = XDefaultRootWindow(display);
        _activeWindowAtom = XInternAtom(display, "_NET_ACTIVE_WINDOW", 0);
        _stateAtom = XInternAtom(display, "_NET_WM_STATE", 0);
        _fullscreenAtom = XInternAtom(display, "_NET_WM_STATE_FULLSCREEN", 0);
        _processIdAtom = XInternAtom(display, "_NET_WM_PID", 0);
        _hasMonitors = SupportsMonitors(display);
    }

    public bool IsConnected => Volatile.Read(ref _lostDisplay) != _display;

    internal static IX11Desktop? Open()
    {
        var display = XOpenDisplay(null);
        if (display == nint.Zero)
            return null;

        try
        {
            XSetIOErrorExitHandler(display, &OnConnectionLost, nint.Zero);
        }
        catch (EntryPointNotFoundException)
        {
            XCloseDisplay(display);
            if (Interlocked.Exchange(ref _missingExitHandlerLogged, 1) == 0)
                Log.Information("X11 fullscreen detection is off: this libX11 predates 1.8 and would end Tript if the X server went away.");
            return null;
        }

        return new NativeX11Desktop(display);
    }

    public X11ActiveWindow? ReadActiveWindow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected)
            return null;

        lock (ErrorTrapGate)
        {
            _trappedDisplay = _display;
            _trappedErrorCode = Success;
            _previousErrorHandler = XSetErrorHandler(&OnProtocolError);
            try
            {
                var window = ReadActiveWindowUntrapped();
                return _trappedErrorCode == Success && IsConnected ? window : null;
            }
            finally
            {
                XSetErrorHandler((delegate* unmanaged<nint, nint, int>)_previousErrorHandler);
                _trappedDisplay = nint.Zero;
                _previousErrorHandler = nint.Zero;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (IsConnected)
            XCloseDisplay(_display);
    }

    private X11ActiveWindow? ReadActiveWindowUntrapped()
    {
        var active = X11Properties.DecodeWindow(ReadFormat32(_root, _activeWindowAtom, WindowType, 1));
        if (active is null || _trappedErrorCode != Success)
            return null;

        var window = (nuint)active.Value;
        var state = ReadFormat32(window, _stateAtom, AtomType, MaximumStateAtoms);
        var processId = X11Properties.DecodeProcessId(ReadFormat32(window, _processIdAtom, CardinalType, 1));

        var rects = new List<ScreenRect>(2);
        if (RootRelativeRect(window) is { } client)
            rects.Add(client);
        var topLevel = TopLevelAncestor(window);
        if (topLevel != window && topLevel != 0 && RootRelativeRect(topLevel) is { } frame)
            rects.Add(frame);

        return new X11ActiveWindow(processId, state, _fullscreenAtom, rects, Monitors());
    }

    private IReadOnlyList<ulong> ReadFormat32(nuint window, nuint property, nuint type, int maximumItems)
    {
        if (property == 0)
            return [];

        var status = XGetWindowProperty(_display, window, property, 0, maximumItems, 0, type,
            out var actualType, out var format, out var itemCount, out _, out var data);
        try
        {
            if (status != Success || data == nint.Zero || actualType != type || itemCount == 0)
                return [];

            var count = (int)Math.Min(itemCount, (nuint)maximumItems);
            var bytes = new ReadOnlySpan<byte>((void*)data, count * sizeof(nint));
            return X11Properties.DecodeFormat32(bytes, format, count, sizeof(nint));
        }
        finally
        {
            if (data != nint.Zero)
                XFree(data);
        }
    }

    private ScreenRect? RootRelativeRect(nuint window)
    {
        if (XGetWindowAttributes(_display, window, out var attributes) == 0 || _trappedErrorCode != Success)
            return null;

        if (XTranslateCoordinates(_display, window, _root, 0, 0, out var x, out var y, out _) == 0
            || _trappedErrorCode != Success)
            return null;

        return new ScreenRect(x, y, attributes.Width, attributes.Height);
    }

    private nuint TopLevelAncestor(nuint window)
    {
        const int MaximumDepth = 16;
        var current = window;
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            if (XQueryTree(_display, current, out _, out var parent, out var children, out _) == 0
                || _trappedErrorCode != Success)
                return 0;
            if (children != nint.Zero)
                XFree(children);
            if (parent == 0 || parent == _root)
                return current;
            current = parent;
        }

        return 0;
    }

    private IReadOnlyList<ScreenRect> Monitors()
    {
        if (_hasMonitors)
        {
            var monitors = XRRGetMonitors(_display, _root, 1, out var count);
            if (monitors != null)
            {
                try
                {
                    var rects = new List<ScreenRect>(count);
                    for (var index = 0; index < count; index++)
                        rects.Add(new ScreenRect(monitors[index].X, monitors[index].Y, monitors[index].Width, monitors[index].Height));
                    if (rects.Count > 0)
                        return rects;
                }
                finally
                {
                    XRRFreeMonitors(monitors);
                }
            }
        }

        return XGetWindowAttributes(_display, _root, out var root) != 0
            ? [new ScreenRect(0, 0, root.Width, root.Height)]
            : [];
    }

    private static bool SupportsMonitors(nint display)
    {
        try
        {
            return XRRQueryVersion(display, out var major, out var minor) != 0
                && (major > 1 || (major == 1 && minor >= 5));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [UnmanagedCallersOnly]
    private static int OnProtocolError(nint display, nint errorEvent)
    {
        if (display == _trappedDisplay)
        {
            if (_trappedErrorCode == Success)
                _trappedErrorCode = Math.Max(1, (int)((XErrorEvent*)errorEvent)->ErrorCode);
            return 0;
        }

        var previous = (delegate* unmanaged<nint, nint, int>)_previousErrorHandler;
        return previous is null ? 0 : previous(display, errorEvent);
    }

    [UnmanagedCallersOnly]
    private static void OnConnectionLost(nint display, nint userData)
        => Volatile.Write(ref _lostDisplay, display);

    [StructLayout(LayoutKind.Sequential)]
    private struct XErrorEvent
    {
        public int Type;
        public nint Display;
        public nuint ResourceId;
        public nuint Serial;
        public byte ErrorCode;
        public byte RequestCode;
        public byte MinorCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int BorderWidth;
        public int Depth;
        public nint Visual;
        public nuint Root;
        public int Class;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public nuint BackingPlanes;
        public nuint BackingPixel;
        public int SaveUnder;
        public nuint Colormap;
        public int MapInstalled;
        public int MapState;
        public nint AllEventMasks;
        public nint YourEventMask;
        public nint DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRMonitorInfo
    {
        public nuint Name;
        public int Primary;
        public int Automatic;
        public int OutputCount;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int MillimetreWidth;
        public int MillimetreHeight;
        public nint Outputs;
    }

    [DllImport(X11Library, CharSet = CharSet.Ansi)]
    private static extern nint XOpenDisplay(string? name);

    [DllImport(X11Library)]
    private static extern int XCloseDisplay(nint display);

    [DllImport(X11Library)]
    private static extern nuint XDefaultRootWindow(nint display);

    [DllImport(X11Library, CharSet = CharSet.Ansi)]
    private static extern nuint XInternAtom(nint display, string name, int onlyIfExists);

    [DllImport(X11Library)]
    private static extern int XGetWindowProperty(
        nint display, nuint window, nuint property, nint offset, nint length, int delete, nuint requestedType,
        out nuint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out nint data);

    [DllImport(X11Library)]
    private static extern int XGetWindowAttributes(nint display, nuint window, out XWindowAttributes attributes);

    [DllImport(X11Library)]
    private static extern int XTranslateCoordinates(
        nint display, nuint source, nuint destination, int sourceX, int sourceY,
        out int destinationX, out int destinationY, out nuint child);

    [DllImport(X11Library)]
    private static extern int XQueryTree(
        nint display, nuint window, out nuint root, out nuint parent, out nint children, out uint childCount);

    [DllImport(X11Library)]
    private static extern int XFree(nint data);

    [DllImport(X11Library)]
    private static extern nint XSetErrorHandler(delegate* unmanaged<nint, nint, int> handler);

    [DllImport(X11Library)]
    private static extern void XSetIOErrorExitHandler(
        nint display, delegate* unmanaged<nint, nint, void> handler, nint userData);

    [DllImport(RandRLibrary)]
    private static extern int XRRQueryVersion(nint display, out int major, out int minor);

    [DllImport(RandRLibrary)]
    private static extern XRRMonitorInfo* XRRGetMonitors(nint display, nuint window, int getActive, out int count);

    [DllImport(RandRLibrary)]
    private static extern void XRRFreeMonitors(XRRMonitorInfo* monitors);
}
