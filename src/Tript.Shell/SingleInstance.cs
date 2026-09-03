// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Tript.Core;

namespace Tript.Shell;

// Autostart and tray applications must not create a second host that loses the port race. The second
// launch sends a local activation request to the first instance and exits instead.
internal sealed class SingleInstance : IDisposable
{
    private const string ActivationMessage = "activate";

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
    private bool _activationPending;
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
        RequestActivation();
        return null;
    }

    private static void RequestActivation()
    {
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(750);
                    var bytes = Encoding.UTF8.GetBytes(ActivationMessage + "\n");
                    client.Write(bytes, 0, bytes.Length);
                    return;
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
            // The mutex owner may be between startup stages or shutting down. The second process has
            // no useful work left after the activation attempt, so it exits quietly.
        }
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
                if (reader.ReadLineAsync(_cancellation.Token).GetAwaiter().GetResult() == ActivationMessage)
                {
                    Action? handler;
                    lock (_activationGate)
                    {
                        handler = _activationRequested;
                        if (handler is null)
                            _activationPending = true;
                    }

                    try
                    {
                        handler?.Invoke();
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"Tript.Shell: activation failed: {exception.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!_cancellation.IsCancellationRequested)
            {
                // A transient client or pipe teardown should not kill the activation listener.
            }
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
