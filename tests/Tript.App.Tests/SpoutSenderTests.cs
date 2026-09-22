// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Spout;
using Xunit;

namespace Tript.App.Tests;

public sealed class SpoutSenderTests
{
    private readonly SpoutObjectNames _objects;
    private readonly string _prefix = "TriptTest" + Guid.NewGuid().ToString("N")[..12];

    public SpoutSenderTests()
    {
        _objects = new SpoutObjectNames(_prefix + "Names", _prefix + "Active");
    }

    [Theory]
    [InlineData("Tript", true)]
    [InlineData("Tript Game 2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Tript é", false)]
    [InlineData("Tab\there", false)]
    public void SenderNames_ArePrintableAscii(string? name, bool valid) =>
        Assert.Equal(valid, SpoutNaming.IsValidSenderName(name));

    [Fact]
    public void ANameOfTheSpoutMaximum_IsRejected()
    {
        Assert.True(SpoutNaming.IsValidSenderName(new string('a', SpoutNaming.NameLength - 1)));
        Assert.False(SpoutNaming.IsValidSenderName(new string('a', SpoutNaming.NameLength)));
    }

    // The sender list is shared with every Spout application on the machine. On a lock timeout the
    // sender used to carry on and rewrite the list anyway, which could drop another application's
    // registration. It must now leave the list alone and register on a later Publish instead.
    // The lock is held from another thread because a Mutex is reentrant for its owner.
    [SkippableFact]
    public void WhileAnotherApplicationHoldsTheListLock_PublishLeavesTheSharedListAlone_AndRetries()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Spout shared memory is Windows-only.");

        var name = _prefix + "Sender";
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, _objects.SenderList + "_mutex");
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();

        using var sender = new SpoutSender(name, _objects);
        sender.Publish(0x1234, 1920, 1080);
        Assert.Empty(SpoutSender.ReadSenderList(_objects));

        release.Set();
        holder.Join();

        sender.Publish(0x1234, 1920, 1080);
        Assert.Equal([name], SpoutSender.ReadSenderList(_objects));
    }

    [SkippableFact]
    public void Publishing_RegistersTheSenderAndItsTexture()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Spout shared memory is Windows-only.");

        var name = _prefix + "Sender";
        using var sender = new SpoutSender(name, _objects);
        sender.Publish(0x1234, 1920, 1080);

        Assert.Equal([name], SpoutSender.ReadSenderList(_objects));
        Assert.Equal(name, SpoutSender.ReadActiveSender(_objects));
        var info = SpoutSender.ReadInfo(name);
        Assert.NotNull(info);
        Assert.Equal(0x1234u, info.Value.SharedHandle);
        Assert.Equal(1920u, info.Value.Width);
        Assert.Equal(1080u, info.Value.Height);
        Assert.Equal(SpoutSender.DxgiFormatB8G8R8A8Unorm, info.Value.Format);
        Assert.False(string.IsNullOrEmpty(info.Value.Description));
    }

    [SkippableFact]
    public void Republishing_UpdatesTheTextureWithoutListingTheSenderTwice()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Spout shared memory is Windows-only.");

        var name = _prefix + "Sender";
        using var sender = new SpoutSender(name, _objects);
        sender.Publish(1, 1280, 720);
        sender.Publish(2, 2560, 1440);

        Assert.Equal([name], SpoutSender.ReadSenderList(_objects));
        var info = SpoutSender.ReadInfo(name);
        Assert.Equal(2u, info!.Value.SharedHandle);
        Assert.Equal(2560u, info.Value.Width);
    }

    [SkippableFact]
    public void TheSenderList_StaysSortedAndKeepsOtherSenders()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Spout shared memory is Windows-only.");

        using var later = new SpoutSender(_prefix + "B", _objects);
        using var earlier = new SpoutSender(_prefix + "A", _objects);
        later.Publish(1, 10, 10);
        earlier.Publish(2, 10, 10);

        Assert.Equal([_prefix + "A", _prefix + "B"], SpoutSender.ReadSenderList(_objects));
        Assert.Equal(_prefix + "B", SpoutSender.ReadActiveSender(_objects));

        later.Withdraw();

        Assert.Equal([_prefix + "A"], SpoutSender.ReadSenderList(_objects));
        Assert.Equal(_prefix + "A", SpoutSender.ReadActiveSender(_objects));
    }

    [SkippableFact]
    public void Withdrawing_ClearsTheTextureInfoAndTheList()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Spout shared memory is Windows-only.");

        var name = _prefix + "Sender";
        using var sender = new SpoutSender(name, _objects);
        using var holder = new SpoutSender(_prefix + "Holder", _objects);
        holder.Publish(9, 1, 1);
        sender.Publish(7, 640, 480);

        sender.Withdraw();

        Assert.Equal([_prefix + "Holder"], SpoutSender.ReadSenderList(_objects));
        Assert.Null(SpoutSender.ReadInfo(name));
    }

    [SkippableFact]
    public void TheFrameLock_IsOnlyAvailableOncePublished()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Spout shared memory is Windows-only.");

        using var sender = new SpoutSender(_prefix + "Sender", _objects);
        Assert.False(sender.TryBeginFrame());

        sender.Publish(1, 1, 1);
        Assert.True(sender.TryBeginFrame());
        sender.EndFrame();
    }
}
