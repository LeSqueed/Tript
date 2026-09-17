// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

internal sealed class SerialWorkQueue<T>
{
    private readonly Action<T> _process;
    private readonly Action<T, Exception> _onFailure;
    private readonly Lock _gate = new();
    private readonly Queue<T> _items = [];
    private bool _active;

    internal SerialWorkQueue(Action<T> process, Action<T, Exception> onFailure)
    {
        _process = process;
        _onFailure = onFailure;
    }

    internal bool IsActive
    {
        get
        {
            lock (_gate)
                return _active;
        }
    }

    internal void Enqueue(T item)
    {
        lock (_gate)
        {
            _items.Enqueue(item);
            if (_active)
                return;

            _active = true;
        }

        ThreadPool.QueueUserWorkItem(_ => Drain());
    }

    private void Drain()
    {
        while (true)
        {
            T item;
            lock (_gate)
            {
                if (!_items.TryDequeue(out item!))
                {
                    _active = false;
                    return;
                }
            }

            try
            {
                _process(item);
            }
            catch (Exception exception)
            {
                _onFailure(item, exception);
            }
        }
    }
}
