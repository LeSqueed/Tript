// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Tript.Core;

namespace Tript.Shell;

internal sealed class SingleInstance : IDisposable
{
    internal const string ActivationMessage = "activate";
    internal const string ExitMessage = "exit";

    private static string MutexName => $"Local\\Tript.SingleInstance.{InstanceScope}";
    private static string PipeName => $"Tript.SingleInstance.Activate.{InstanceScope}";

    private static string InstanceScope
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                return "default";

            var normalized = FilePaths.CaseFold(Path.GetFullPath(processPath));

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        }
    }

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _serverThread;
    private readonly object _activationGate = new();
    private Action? _activationRequested;
    private Action? _exitRequested;
    private bool _activationPending;
    private bool _exitPending;
    private bool _disposed;

    internal event Action? ActivationRequested
    {
        add
        {
            var invokeImmediately = false;
            lock (_activationGate)
            {
                _activationRequested += value;
                if (_activationPending)
                {
                    _activationPending = false;
                    invokeImmediately = true;
                }
            }

            if (invokeImmediately)
                value?.Invoke();
        }
        remove
        {
            lock (_activationGate)
                _activationRequested -= value;
        }
    }

    internal event Action? ExitRequested
    {
        add
        {
            var invokeImmediately = false;
            lock (_activationGate)
            {
                _exitRequested += value;
                if (_exitPending)
                {
                    _exitPending = false;
                    invokeImmediately = true;
                }
            }

            if (invokeImmediately)
                value?.Invoke();
        }
        remove
        {
            lock (_activationGate)
                _exitRequested -= value;
        }
    }

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _serverThread = new Thread(ServerLoop)
        {
            IsBackground = true,
            Name = "Tript.Shell.Activation",
        };
        _serverThread.Start();
    }

    internal static SingleInstance? TryAcquire()
    {
        Mutex mutex;
        bool created;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out created);
        }
        catch (AbandonedMutexException exception)
        {
            mutex = exception.Mutex!;
            created = true;
        }

        if (created)
            return new SingleInstance(mutex);

        mutex.Dispose();
        Send(ActivationMessage);
        return null;
    }

    // The mutex is only opened, never acquired. The exe image lock outlives it by a few ms,
    // so deploy scripts should retry the copy briefly.
    internal static bool RequestExit(TimeSpan timeout)
    {
        if (!ScopeIsTaken())
            return true;

        if (!Send(ExitMessage))
            return false;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!ScopeIsTaken())
                return true;

            Thread.Sleep(100);
        }

        return false;
    }

    private static bool ScopeIsTaken()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var existing))
            return false;

        existing.Dispose();
        return true;
    }

    internal static bool Send(string message)
    {
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(750);
                    var bytes = Encoding.UTF8.GetBytes(message + "\n");
                    client.Write(bytes, 0, bytes.Length);
                    return true;
                }
                catch (TimeoutException) when (attempt < 7)
                {
                    Thread.Sleep(250);
                }
                catch (IOException) when (attempt < 7)
                {
                    Thread.Sleep(250);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
        }

        return false;
    }

    private void ServerLoop()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(_cancellation.Token).GetAwaiter().GetResult();
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                Dispatch(reader.ReadLineAsync(_cancellation.Token).GetAwaiter().GetResult());
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!_cancellation.IsCancellationRequested)
            {
            }
        }
    }

    private void Dispatch(string? message)
    {
        Action? handler;
        lock (_activationGate)
        {
            switch (message)
            {
                case ActivationMessage:
                    handler = _activationRequested;
                    if (handler is null)
                        _activationPending = true;
                    break;
                case ExitMessage:
                    handler = _exitRequested;
                    if (handler is null)
                        _exitPending = true;
                    break;
                default:
                    return;
            }
        }

        try
        {
            handler?.Invoke();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Tript.Shell: the {message} request failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cancellation.Cancel();
        _serverThread.Join(TimeSpan.FromSeconds(2));
        _cancellation.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
