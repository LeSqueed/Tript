// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

internal sealed class CoalescingRunner
{
    private readonly Action _work;
    private readonly Action<Exception> _onFailure;
    private readonly Lock _gate = new();
    private bool _running;
    private bool _pending;

    internal CoalescingRunner(Action work, Action<Exception> onFailure)
    {
        _work = work;
        _onFailure = onFailure;
    }

    internal void Run()
    {
        lock (_gate)
        {
            if (_running)
            {
                _pending = true;
                return;
            }

            _running = true;
        }

        try
        {
            do
            {
                _work();
            }
            while (TakePending());
        }
        catch (Exception exception)
        {
            lock (_gate)
                _running = false;
            _onFailure(exception);
        }
    }

    private bool TakePending()
    {
        lock (_gate)
        {
            if (_pending)
            {
                _pending = false;
                return true;
            }

            _running = false;
            return false;
        }
    }
}
