// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Tript.Core;

namespace Tript.Obs.Spout;

internal sealed record SpoutObjectNames(string SenderList, string ActiveSender)
{
    internal static SpoutObjectNames Default { get; } = new("SpoutSenderNames", "ActiveSenderName");
}

internal readonly record struct SpoutSenderInfo(uint SharedHandle, uint Width, uint Height, uint Format,
    string Description);

public static class SpoutNaming
{
    internal const int NameLength = 256;

    public static bool IsValidSenderName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length < NameLength &&
        name.All(character => character is >= ' ' and <= '~');
}

[SupportedOSPlatform("windows")]
internal sealed class SpoutSender : ISharedTextureSink, IDisposable
{
    internal const int NameLength = SpoutNaming.NameLength;
    internal const int SharedInfoSize = 280;
    internal const uint DxgiFormatB8G8R8A8Unorm = 87;
    internal const int DefaultMaxSenders = 10;

    private const int HandleOffset = 0;
    private const int WidthOffset = 4;
    private const int HeightOffset = 8;
    private const int FormatOffset = 12;
    private const int UsageOffset = 16;
    private const int DescriptionOffset = 20;
    private const int PartnerIdOffset = 276;
    private const string MutexSuffix = "_mutex";
    private const string AccessMutexSuffix = "_SpoutAccessMutex";
    private const string SpoutRegistryKey = @"Software\Leading Edge\Spout";

    private static readonly TimeSpan ListLockTimeout = TimeSpan.FromMilliseconds(67);
    private static readonly TimeSpan FrameLockTimeout = TimeSpan.FromMilliseconds(4);

    private readonly SpoutObjectNames _objects;
    private readonly Lock _gate = new();
    private SharedBlock? _info;
    private SharedBlock? _list;
    private SharedBlock? _active;
    private Mutex? _accessMutex;
    private bool _registered;
    private int _listLockTimeoutReported;
    private bool _disposed;

    internal SpoutSender(string name, SpoutObjectNames? objects = null)
    {
        if (!SpoutNaming.IsValidSenderName(name))
            throw new ArgumentException("A Spout sender name is 1 to 255 printable ASCII characters.", nameof(name));

        Name = name;
        _objects = objects ?? SpoutObjectNames.Default;
    }

    internal string Name { get; }

    public void Publish(uint sharedHandle, uint width, uint height)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _info ??= SharedBlock.Open(Name, SharedInfoSize);
            _accessMutex ??= new Mutex(false, Name + AccessMutexSuffix);

            using (var infoLock = _info.Lock(ListLockTimeout))
            {
                if (infoLock.Acquired)
                    WriteInfo(_info.Accessor, sharedHandle, width, height);
            }

            if (_registered)
                return;

            _list ??= SharedBlock.Open(_objects.SenderList, MaxSenders() * NameLength);
            _active ??= SharedBlock.Open(_objects.ActiveSender, NameLength);

            using (var listLock = _list.Lock(ListLockTimeout))
            {
                // The sender list is shared by every Spout application on the machine. Writing it
                // without the lock could clobber another sender's registration, so on a timeout this
                // leaves _registered false and the next Publish tries again.
                if (!listLock.Acquired)
                {
                    Diagnostics.ReportFirst(ref _listLockTimeoutReported, DiagnosticLevel.Warning,
                        "The Spout sender list was locked by another application; registration will be retried");
                    return;
                }

                var names = ReadNames(_list.Accessor, SlotCount(_list));
                names.Add(Name);
                WriteNames(_list.Accessor, SlotCount(_list), names);

                var active = ReadString(_active.Accessor, 0, NameLength);
                if (active.Length == 0 || !names.Contains(active))
                    WriteString(_active.Accessor, 0, NameLength, Name);
            }

            _registered = true;
        }
    }

    public bool TryBeginFrame()
    {
        var mutex = _accessMutex;
        if (mutex is null)
            return false;

        try
        {
            return mutex.WaitOne(FrameLockTimeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    public void EndFrame() => _accessMutex?.ReleaseMutex();

    public void Withdraw()
    {
        lock (_gate)
            WithdrawLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            WithdrawLocked();
            _list?.Dispose();
            _active?.Dispose();
            _list = null;
            _active = null;
            _disposed = true;
        }
    }

    internal static IReadOnlyList<string> ReadSenderList(SpoutObjectNames? objects = null)
    {
        var names = objects ?? SpoutObjectNames.Default;
        using var list = SharedBlock.OpenExisting(names.SenderList);
        if (list is null)
            return [];

        using (list.Lock(ListLockTimeout))
            return ReadNames(list.Accessor, SlotCount(list)).ToList();
    }

    internal static string? ReadActiveSender(SpoutObjectNames? objects = null)
    {
        var names = objects ?? SpoutObjectNames.Default;
        using var active = SharedBlock.OpenExisting(names.ActiveSender);
        return active is null ? null : ReadString(active.Accessor, 0, NameLength);
    }

    internal static SpoutSenderInfo? ReadInfo(string name)
    {
        using var info = SharedBlock.OpenExisting(name);
        if (info is null)
            return null;

        var accessor = info.Accessor;
        return new SpoutSenderInfo(
            accessor.ReadUInt32(HandleOffset),
            accessor.ReadUInt32(WidthOffset),
            accessor.ReadUInt32(HeightOffset),
            accessor.ReadUInt32(FormatOffset),
            ReadString(accessor, DescriptionOffset, NameLength));
    }

    private void WithdrawLocked()
    {
        if (_registered && _list is not null && _active is not null)
        {
            using (var listLock = _list.Lock(ListLockTimeout))
            {
                if (listLock.Acquired)
                {
                    var names = ReadNames(_list.Accessor, SlotCount(_list));
                    names.Remove(Name);
                    WriteNames(_list.Accessor, SlotCount(_list), names);

                    if (ReadString(_active.Accessor, 0, NameLength) == Name)
                        WriteString(_active.Accessor, 0, NameLength, names.Min ?? string.Empty);
                }
                else
                {
                    // A stale name in the list is far less harmful than corrupting another sender's.
                    Diagnostics.Report(DiagnosticLevel.Warning,
                        "The Spout sender list stayed locked; Tript's entry may linger until the next start");
                }
            }
        }

        _registered = false;

        if (_info is not null)
        {
            using (var infoLock = _info.Lock(ListLockTimeout))
            {
                if (infoLock.Acquired)
                    _info.Accessor.WriteArray(0, new byte[SharedInfoSize], 0, SharedInfoSize);
            }
            _info.Dispose();
            _info = null;
        }

        _accessMutex?.Dispose();
        _accessMutex = null;
    }

    private static void WriteInfo(MemoryMappedViewAccessor accessor, uint sharedHandle, uint width, uint height)
    {
        accessor.Write(HandleOffset, sharedHandle);
        accessor.Write(WidthOffset, width);
        accessor.Write(HeightOffset, height);
        accessor.Write(FormatOffset, DxgiFormatB8G8R8A8Unorm);
        accessor.Write(UsageOffset, 0u);
        WriteString(accessor, DescriptionOffset, NameLength, HostDescription());
        accessor.Write(PartnerIdOffset, 0u);
    }

    private static string HostDescription()
    {
        var path = Environment.ProcessPath ?? "Tript";
        var ascii = new string(path.Select(character => character is >= ' ' and <= '~' ? character : '_').ToArray());
        return ascii.Length < NameLength ? ascii : ascii[..(NameLength - 1)];
    }

    private static int MaxSenders()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SpoutRegistryKey);
            if (key?.GetValue("MaxSenders") is int configured && configured > 0)
                return configured;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException
            or IOException)
        {
        }

        return DefaultMaxSenders;
    }

    private static int SlotCount(SharedBlock block) =>
        Math.Min((int)(block.Accessor.Capacity / NameLength), MaxSenders());

    private static SortedSet<string> ReadNames(MemoryMappedViewAccessor accessor, int slots)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (var slot = 0; slot < slots; slot++)
        {
            var name = ReadString(accessor, slot * (long)NameLength, NameLength);
            if (name.Length == 0)
                break;
            names.Add(name);
        }

        return names;
    }

    private static void WriteNames(MemoryMappedViewAccessor accessor, int slots, SortedSet<string> names)
    {
        var slot = 0;
        foreach (var name in names.Take(slots))
            WriteString(accessor, slot++ * (long)NameLength, NameLength, name);

        if (slot < slots)
            accessor.Write(slot * (long)NameLength, (byte)0);
    }

    private static string ReadString(MemoryMappedViewAccessor accessor, long offset, int length)
    {
        var buffer = new byte[length];
        accessor.ReadArray(offset, buffer, 0, length);
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? length : end);
    }

    private static void WriteString(MemoryMappedViewAccessor accessor, long offset, int length, string value)
    {
        var buffer = new byte[length];
        Encoding.ASCII.GetBytes(value, 0, Math.Min(value.Length, length - 1), buffer, 0);
        accessor.WriteArray(offset, buffer, 0, length);
    }

    private sealed class SharedBlock : IDisposable
    {
        private readonly MemoryMappedFile _map;
        private readonly Mutex _mutex;

        private SharedBlock(MemoryMappedFile map, string name)
        {
            _map = map;
            try
            {
                Accessor = map.CreateViewAccessor(0, 0);
                _mutex = new Mutex(false, name + MutexSuffix);
            }
            catch
            {
                // The factory methods have no try of their own, so a throw here leaked a named
                // section handle per failure.
                Accessor?.Dispose();
                map.Dispose();
                throw;
            }
        }

        internal MemoryMappedViewAccessor Accessor { get; }

        internal static SharedBlock Open(string name, int size) =>
            new(MemoryMappedFile.CreateOrOpen(name, size, MemoryMappedFileAccess.ReadWrite), name);

        internal static SharedBlock? OpenExisting(string name)
        {
            try
            {
                return new SharedBlock(MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite), name);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        // AbandonedMutexException means the wait succeeded and this thread now owns the mutex; the
        // previous owner simply died holding it. It is reported as acquired because it is: treating
        // it as a failure would skip the release and hold the mutex forever. A timeout is the only
        // real failure, and callers must check Acquired before writing shared memory.
        internal LockScope Lock(TimeSpan timeout)
        {
            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            return new LockScope(acquired ? _mutex : null);
        }

        public void Dispose()
        {
            Accessor.Dispose();
            _map.Dispose();
            _mutex.Dispose();
        }

        internal sealed class LockScope(Mutex? mutex) : IDisposable
        {
            internal bool Acquired => mutex is not null;

            public void Dispose() => mutex?.ReleaseMutex();
        }
    }
}
