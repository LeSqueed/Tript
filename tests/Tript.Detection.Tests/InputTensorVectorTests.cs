// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Generic;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class InputTensorVectorTests
{
    private static void AssertBitIdentical(float[] expected, float[] actual, string context)
    {
        Assert.Equal(expected.Length, actual.Length);
        var diverged = new List<string>();
        for (int i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
            {
                diverged.Add($"index {i}: expected {expected[i]:R} " +
                             $"(0x{BitConverter.SingleToInt32Bits(expected[i]):X8}) but got {actual[i]:R} " +
                             $"(0x{BitConverter.SingleToInt32Bits(actual[i]):X8})");
                if (diverged.Count == 10) break;
            }
        }
        Assert.True(diverged.Count == 0,
            $"{context}: output is not bit-identical to the reference.\n" + string.Join("\n", diverged));
    }

    private static byte[] EveryByteValueOnce()
    {
        var gray = new byte[256];
        for (int b = 0; b < 256; b++) gray[b] = (byte)b;
        return gray;
    }

    [Fact]
    public void EveryByteValue_VectorPath_IsBitIdenticalToReference()
    {
        var gray = EveryByteValueOnce();
        var expected = ReferenceImplementations.BuildInputTensor(gray, 16);

        var actual = new float[256 * 3];
        DetectionFramePreprocessor.FillInputTensor(gray, actual, 16, useVectorPath: true);

        AssertBitIdentical(expected, actual, "vector path, all 256 byte values");
    }

    [Fact]
    public void EveryByteValue_ScalarFallback_IsBitIdenticalToReference()
    {
        var gray = EveryByteValueOnce();
        var expected = ReferenceImplementations.BuildInputTensor(gray, 16);

        var actual = new float[256 * 3];
        DetectionFramePreprocessor.FillInputTensor(gray, actual, 16, useVectorPath: false);

        AssertBitIdentical(expected, actual, "scalar fallback, all 256 byte values");
    }

    [Fact]
    public void DefaultEntryPoint_AgreesWithBothExplicitPaths()
    {
        var gray = EveryByteValueOnce();

        var byDefault = new float[256 * 3];
        var vector = new float[256 * 3];
        var scalar = new float[256 * 3];
        DetectionFramePreprocessor.FillInputTensor(gray, byDefault, 16);
        DetectionFramePreprocessor.FillInputTensor(gray, vector, 16, useVectorPath: true);
        DetectionFramePreprocessor.FillInputTensor(gray, scalar, 16, useVectorPath: false);

        AssertBitIdentical(scalar, byDefault, "default entry point vs scalar");
        AssertBitIdentical(scalar, vector, "vector path vs scalar");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(31)]
    [InlineData(63)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(64)]
    public void Tail_And_WholeVector_Sizes_AreBitIdenticalToReference(int inputSize)
    {
        var pixels = inputSize * inputSize;
        var gray = new byte[pixels];
        new Random(inputSize * 7919).NextBytes(gray);

        var expected = ReferenceImplementations.BuildInputTensor(gray, inputSize);

        var vector = new float[pixels * 3];
        var scalar = new float[pixels * 3];
        DetectionFramePreprocessor.FillInputTensor(gray, vector, inputSize, useVectorPath: true);
        DetectionFramePreprocessor.FillInputTensor(gray, scalar, inputSize, useVectorPath: false);

        AssertBitIdentical(expected, vector, $"vector path at inputSize {inputSize} (tail {pixels % 4})");
        AssertBitIdentical(expected, scalar, $"scalar fallback at inputSize {inputSize}");
    }

    [Fact]
    public void OneElementTail_CoversEveryByteValue()
    {
        const int InputSize = 3;
        const int Pixels = InputSize * InputSize;
        Assert.Equal(1, Pixels % 4);

        for (int b = 0; b < 256; b++)
        {
            var gray = new byte[Pixels];
            new Random(b).NextBytes(gray);
            gray[Pixels - 1] = (byte)b;

            var expected = ReferenceImplementations.BuildInputTensor(gray, InputSize);
            var actual = new float[Pixels * 3];
            DetectionFramePreprocessor.FillInputTensor(gray, actual, InputSize, useVectorPath: true);

            AssertBitIdentical(expected, actual, $"tail element holding byte {b}");
        }
    }

    [Fact]
    public void UndersizedBuffers_Throw()
    {
        Assert.Throws<ArgumentException>(() =>
            DetectionFramePreprocessor.FillInputTensor(new byte[15], new float[16 * 3], 4));
        Assert.Throws<ArgumentException>(() =>
            DetectionFramePreprocessor.FillInputTensor(new byte[16], new float[16 * 3 - 1], 4));
    }

    [Fact]
    public void OversizedSourceBuffer_IsAccepted()
    {
        const int InputSize = 8;
        const int Pixels = InputSize * InputSize;
        var gray = new byte[Pixels + 500];
        new Random(1234).NextBytes(gray);

        var expected = ReferenceImplementations.BuildInputTensor(gray, InputSize);
        var actual = new float[Pixels * 3];
        DetectionFramePreprocessor.FillInputTensor(gray, actual, InputSize, useVectorPath: true);

        AssertBitIdentical(expected, actual, "oversized source buffer");
    }
}
