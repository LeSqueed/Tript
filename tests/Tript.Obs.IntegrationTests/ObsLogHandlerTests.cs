// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using System.Text;
using Tript.Obs.Interop;
using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsLogHandlerTests
{
    private const int LogInfo = 300;
    private const int LogWarning = 200;

    [SkippableFact]
    public void AnUnformattedMessage_ArrivesThroughTheInstalledHandler()
    {
        ObsTestEnvironment.RequireUsableRuntime();
        var received = new List<(ObsLogLevel Level, string Message)>();

        using (ObsLog.Install((level, message) => received.Add((level, message))))
        {
            Blogva(LogWarning, "tript plain message");
        }

        var entry = Assert.Single(received);
        Assert.Equal(ObsLogLevel.Warning, entry.Level);
        Assert.Equal("tript plain message", entry.Message);
    }

    [SkippableFact]
    public void MixedFormatArguments_AreSubstitutedThroughTheVaList()
    {
        ObsTestEnvironment.RequireUsableRuntime();
        var received = new List<string>();

        using (ObsLog.Install((_, message) => received.Add(message)))
        {
            Blogva(LogInfo, "res %dx%d fps %u/%u scale %.2f name %s",
                VaArgument.Integer(1920), VaArgument.Integer(1080),
                VaArgument.Integer(60000), VaArgument.Integer(1001),
                VaArgument.Double(1.25),
                VaArgument.String("nv12"));
        }

        Assert.Equal("res 1920x1080 fps 60000/1001 scale 1.25 name nv12", Assert.Single(received));
    }

    [SkippableFact]
    public void ANonAsciiArgument_RoundTripsByteIdentically()
    {
        ObsTestEnvironment.RequireUsableRuntime();
        const string original = "café — 日本語 — Ω — 🎮 — ünïcödé";
        var received = new List<string>();

        using (ObsLog.Install((_, message) => received.Add(message)))
        {
            Blogva(LogInfo, "%s", VaArgument.String(original));
        }

        var message = Assert.Single(received);
        Assert.Equal(Encoding.UTF8.GetBytes(original), Encoding.UTF8.GetBytes(message));
    }

    [SkippableFact]
    public void ANonAsciiFormatString_SurvivesSubstitution()
    {
        ObsTestEnvironment.RequireUsableRuntime();
        const string format = "трипт %s ✅";
        var received = new List<string>();

        using (ObsLog.Install((_, message) => received.Add(message)))
        {
            Blogva(LogInfo, format, VaArgument.String("日本語"));
        }

        Assert.Equal("трипт 日本語 ✅", Assert.Single(received));
    }

    [SkippableFact]
    public void TheEndOfAScope_RestoresThePreviousHandler()
    {
        ObsTestEnvironment.RequireUsableRuntime();
        ObsNative.base_get_log_handler(out var before, out var beforeParameter);

        using (ObsLog.Install((_, _) => { }))
        {
            ObsNative.base_get_log_handler(out var during, out _);
            Assert.NotEqual(before, during);
        }

        ObsNative.base_get_log_handler(out var after, out var afterParameter);
        Assert.Equal(before, after);
        Assert.Equal(beforeParameter, afterParameter);
    }

    [SkippableFact]
    public void ASecondInstall_IsRefusedWithoutConsumingTheSlot()
    {
        ObsTestEnvironment.RequireUsableRuntime();
        using (ObsLog.Install((_, _) => { }))
        {
            Assert.Throws<InvalidOperationException>(() => ObsLog.Install((_, _) => { }));
        }

        using (ObsLog.Install((_, _) => { }))
        {
        }
    }

    [SkippableFact]
    public void AThrowingHandler_DoesNotPropagateIntoLibobs()
    {
        ObsTestEnvironment.RequireUsableRuntime();
        using (ObsLog.Install((_, _) => throw new InvalidOperationException("deliberate")))
        {
            Blogva(LogInfo, "tript throwing handler");
        }
    }

    private static unsafe void Blogva(int level, string format, params VaArgument[] arguments)
    {
        var blogva = (delegate* unmanaged[Cdecl]<int, nint, nint, void>)
            NativeLibrary.GetExport(ObsLibrary.EnsureLoaded(), "blogva");

        var formatPointer = Utf8Marshal.Allocate(format);
        var strings = new List<nint>();

        try
        {
            Span<long> slots = stackalloc long[Math.Max(arguments.Length, 1)];
            for (var i = 0; i < arguments.Length; i++)
                slots[i] = arguments[i].ToSlot(strings);

            fixed (long* overflow = slots)
            {
                var list = new VaListSystemV
                {
                    GpOffset = 48,
                    FpOffset = 176,
                    OverflowArgArea = (nint)overflow,
                    RegSaveArea = nint.Zero
                };

                blogva(level, formatPointer, (nint)(&list));
            }
        }
        finally
        {
            foreach (var pointer in strings)
                Utf8Marshal.Free(pointer);

            Utf8Marshal.Free(formatPointer);
        }
    }

    private readonly record struct VaArgument(bool IsText, long Bits, string? Text)
    {
        internal static VaArgument Integer(long value) => new(false, value, null);

        internal static VaArgument Double(double value) => new(false, BitConverter.DoubleToInt64Bits(value), null);

        internal static VaArgument String(string value) => new(true, 0, value);

        internal long ToSlot(List<nint> allocations)
        {
            if (!IsText)
                return Bits;

            var pointer = Utf8Marshal.Allocate(Text!);
            allocations.Add(pointer);
            return pointer;
        }
    }
}
