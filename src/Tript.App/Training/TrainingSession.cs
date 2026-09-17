// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

namespace Tript.App.Training;

internal sealed class TrainingSession
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cancellation;
    private string? _gameId;
    private string? _phase;

    internal TrainingRunner Runner { get; } = new();

    internal void ThrowIfRunning(string message)
    {
        lock (_gate)
        {
            if (_cancellation is not null)
                throw new InvalidOperationException(message);
        }
    }

    internal (bool Active, string? Phase) StateFor(string gameId)
    {
        lock (_gate)
        {
            var active = string.Equals(_gameId, gameId, StringComparison.OrdinalIgnoreCase);
            return (active, active ? _phase : null);
        }
    }

    internal CancellationTokenSource Begin(string gameId, string phase, Action beforeStart)
    {
        lock (_gate)
        {
            if (_cancellation is not null)
                throw new InvalidOperationException("Training is already running.");

            beforeStart();
            _cancellation = new CancellationTokenSource();
            _gameId = gameId;
            _phase = phase;
            return _cancellation;
        }
    }

    internal void SetPhase(string? phase)
    {
        lock (_gate)
            _phase = phase;
    }

    internal void End(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                _gameId = null;
                _phase = null;
            }
        }
        cancellation.Dispose();
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            Runner.Cancel();
            _cancellation?.Cancel();
        }
    }
}

#endif
