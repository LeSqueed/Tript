// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using Serilog;

namespace Tript.Shell.Linux;

internal sealed unsafe class GMainLoopThread : IDisposable
{
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(30);

    private readonly Thread _thread;
    private readonly IntPtr _context;
    private readonly IntPtr _loop;
    private int _disposed;

    internal GMainLoopThread(string name)
    {
        _context = GioNative.g_main_context_new();
        _loop = GioNative.g_main_loop_new(_context, 0);
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    internal bool IsCurrent => Thread.CurrentThread == _thread;

    internal bool Post(Action action) => Volatile.Read(ref _disposed) == 0 && Enqueue(action);

    internal T Invoke<T>(Func<T> function)
    {
        if (IsCurrent)
            return function();

        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var posted = Post(() =>
        {
            try
            {
                result = function();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                done.Set();
            }
        });
        if (!posted || !done.Wait(InvokeTimeout))
            throw new DBusException("The desktop bus thread is not running.");

        return failure is null ? result : throw failure;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var loop = _loop;
        Enqueue(() => GioNative.g_main_loop_quit(loop));
        if (!IsCurrent)
            _thread.Join(TimeSpan.FromSeconds(2));
    }

    private bool Enqueue(Action action)
    {
        var handle = GCHandle.Alloc(action);
        GioNative.g_main_context_invoke_full(_context, GioNative.DefaultPriority, &RunPosted,
            GCHandle.ToIntPtr(handle), &ReleasePosted);
        return true;
    }

    private void Run()
    {
        GioNative.g_main_context_push_thread_default(_context);
        try
        {
            GioNative.g_main_loop_run(_loop);
        }
        finally
        {
            GioNative.g_main_context_pop_thread_default(_context);
            GioNative.g_main_loop_unref(_loop);
        }
    }

    [UnmanagedCallersOnly]
    private static int RunPosted(IntPtr data)
    {
        try
        {
            ((Action)GCHandle.FromIntPtr(data).Target!)();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Tript.Shell: work on the desktop bus thread failed");
        }

        return GioNative.SourceRemove;
    }

    [UnmanagedCallersOnly]
    private static void ReleasePosted(IntPtr data) => GCHandle.FromIntPtr(data).Free();
}
