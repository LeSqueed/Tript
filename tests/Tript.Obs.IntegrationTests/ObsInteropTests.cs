// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Tript.Obs.Interop;
using Xunit;

namespace Tript.Obs.IntegrationTests;

// That the binding can call into libobs at all: the library loads, every entry point it declares
// exists, and the structs it mirrors have the layout a C compiler gives them. No OBS concepts and
// no context — nothing here starts one.
public sealed class ObsInteropTests
{
    [Fact]
    public void TheObsRuntime_LoadsToASingleHandle()
    {
        var handle = ObsLibrary.EnsureLoaded();

        Assert.NotEqual(nint.Zero, handle);
        Assert.Equal(handle, ObsLibrary.EnsureLoaded());
    }

    // Tript ships its own OBS runtime, so where it is loaded from is a decision the application
    // makes once at startup. Once the library is mapped the decision cannot be revisited, and
    // saying so is better than appearing to accept a new directory that changes nothing.
    [Fact]
    public void ChangingTheRuntimeDirectory_IsRefusedOnceTheLibraryIsLoaded()
    {
        ObsLibrary.EnsureLoaded();

        Assert.Throws<InvalidOperationException>(() => ObsRuntime.SetRuntimeDirectory("/nonexistent/obs-runtime"));
    }

    [Fact]
    public void TheLoadedRuntime_IsThePinnedVersionLine()
    {
        // 32.2.x is the pinned target. A mismatch here is not a test failure so much as a warning
        // that everything below is being verified against the wrong library.
        Assert.Equal(32, ObsRuntime.Version.Major);
        Assert.Equal(2, ObsRuntime.Version.Minor);
        Assert.StartsWith("32.2.", ObsRuntime.VersionString, StringComparison.Ordinal);
    }

    // The point of the exercise: a declared entry point that does not exist fails at the moment it
    // is first called, which may be deep in a recording session. Resolving all of them up front
    // turns that into one loud failure naming every offender.
    [Fact]
    public void EveryDeclaredEntryPoint_ResolvesAgainstTheLoadedRuntime()
    {
        var handle = ObsLibrary.EnsureLoaded();
        var declarations = DeclaredEntryPoints().ToArray();

        // Guards against the test passing because reflection found nothing.
        Assert.True(declarations.Length >= 30,
            $"Expected the binding to declare at least 30 libobs entry points, found {declarations.Length}.");

        var unresolved = declarations
            .Where(entryPoint => !NativeLibrary.TryGetExport(handle, entryPoint, out _))
            .ToArray();

        Assert.True(unresolved.Length == 0,
            $"These symbols are declared by the binding but not exported by the loaded OBS runtime: {string.Join(", ", unresolved)}");
    }

    [Fact]
    public void EveryDeclaredEntryPoint_TargetsTheResolvedLibrary()
    {
        var strays = typeof(ObsNative)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<LibraryImportAttribute>())
            .Where(attribute => attribute is not null && attribute.LibraryName != ObsLibrary.Name)
            .Select(attribute => attribute!.LibraryName)
            .Distinct()
            .ToArray();

        // A second native dependency would not go through our resolver, so it would silently bind
        // to whatever the system happens to have.
        Assert.Empty(strays);
    }

    // The layouts the binding mirrors, checked against the sizes and offsets a C compiler produces
    // for the same headers on this platform. A wrong offset here corrupts every field after it.
    [Fact]
    public void ObsVideoInfo_MatchesTheNativeStructLayout()
    {
        Assert.Equal(56, Marshal.SizeOf<ObsVideoInfoNative>());

        Assert.Equal(0, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.GraphicsModule)));
        Assert.Equal(8, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.FpsNumerator)));
        Assert.Equal(16, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.BaseWidth)));
        Assert.Equal(24, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.OutputWidth)));
        Assert.Equal(32, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.OutputFormat)));
        Assert.Equal(36, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.Adapter)));

        // The one-byte C bool and the three bytes of padding behind it. A managed bool would take
        // four bytes here and push ColorSpace, Range and ScaleType off by one field each.
        Assert.Equal(40, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.GpuConversion)));
        Assert.Equal(44, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.ColorSpace)));
        Assert.Equal(48, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.Range)));
        Assert.Equal(52, (int)Marshal.OffsetOf<ObsVideoInfoNative>(nameof(ObsVideoInfoNative.ScaleType)));
    }

    [Fact]
    public void ObsAudioInfo_MatchesTheNativeStructLayout()
    {
        Assert.Equal(8, Marshal.SizeOf<ObsAudioInfoNative>());
        Assert.Equal(4, (int)Marshal.OffsetOf<ObsAudioInfoNative>(nameof(ObsAudioInfoNative.Speakers)));
    }

    // The scene-item transform, which is passed by pointer into libobs in both directions: a field
    // at the wrong offset here is read as the next one, silently.
    [Fact]
    public void ObsTransformInfo_MatchesTheNativeStructLayout()
    {
        Assert.Equal(8, Marshal.SizeOf<Vec2Native>());
        Assert.Equal(16, Marshal.SizeOf<ObsSceneItemCropNative>());
        Assert.Equal(44, Marshal.SizeOf<ObsTransformInfoNative>());

        Assert.Equal(0, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Position)));
        Assert.Equal(8, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Rotation)));
        Assert.Equal(12, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Scale)));
        Assert.Equal(20, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Alignment)));
        Assert.Equal(24, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.BoundsType)));
        Assert.Equal(28, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.BoundsAlignment)));
        Assert.Equal(32, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.Bounds)));

        // The trailing one-byte C bool, and the three bytes of tail padding behind it that keep the
        // struct a multiple of its four-byte alignment.
        Assert.Equal(40, (int)Marshal.OffsetOf<ObsTransformInfoNative>(nameof(ObsTransformInfoNative.CropToBounds)));
    }

    [Fact]
    public void VaList_MatchesTheSystemVStructLayout()
    {
        // __va_list_tag: two 32-bit offsets then two pointers. 24 bytes on x86-64, and the reason
        // the log handler cannot treat its third argument as a pointer to copy.
        Assert.Equal(24, Marshal.SizeOf<VaListSystemV>());
        Assert.Equal(4, (int)Marshal.OffsetOf<VaListSystemV>(nameof(VaListSystemV.FpOffset)));
        Assert.Equal(8, (int)Marshal.OffsetOf<VaListSystemV>(nameof(VaListSystemV.OverflowArgArea)));
        Assert.Equal(16, (int)Marshal.OffsetOf<VaListSystemV>(nameof(VaListSystemV.RegSaveArea)));
    }

    // A bmem allocation round-trips through the binding's owned-string path and is freed on libobs's
    // heap. Non-ASCII throughout, compared as bytes rather than as strings, because a comparison of
    // two strings that were both mangled the same way passes.
    [Fact]
    public unsafe void AnOwnedString_RoundTripsByteIdenticallyAndIsFreedOnLibobsHeap()
    {
        const string original = "café — 日本語 — Ω — 🎮 — ünïcödé";
        var expected = Encoding.UTF8.GetBytes(original);

        // Reached through the already-loaded handle rather than a second DllImport, so the test
        // cannot accidentally pull in a different copy of libobs than the binding is using.
        var bmemdup = (delegate* unmanaged[Cdecl]<nint, nuint, nint>)
            NativeLibrary.GetExport(ObsLibrary.EnsureLoaded(), "bmemdup");

        var native = Utf8Marshal.Allocate(original);
        try
        {
            var copied = bmemdup(native, (nuint)expected.Length + 1);
            Assert.NotEqual(nint.Zero, copied);

            var actual = new byte[expected.Length];
            Marshal.Copy(copied, actual, 0, expected.Length);
            Assert.Equal(expected, actual);

            // ReadOwned frees through bfree, so the count returns to where it started.
            var before = ObsRuntime.LiveAllocationCount;
            Assert.Equal(original, Utf8Marshal.ReadOwned(copied));
            Assert.Equal(before - 1, ObsRuntime.LiveAllocationCount);
        }
        finally
        {
            Utf8Marshal.Free(native);
        }
    }

    private static IEnumerable<string> DeclaredEntryPoints() =>
        typeof(ObsNative)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(method => (Method: method, Import: method.GetCustomAttribute<LibraryImportAttribute>()))
            .Where(entry => entry.Import is not null && entry.Import.LibraryName == ObsLibrary.Name)
            .Select(entry => entry.Import!.EntryPoint ?? entry.Method.Name)
            .Distinct();
}
