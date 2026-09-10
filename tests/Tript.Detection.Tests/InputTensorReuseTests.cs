// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

[Collection(ModelSessionCollection.Name)]
public class InputTensorReuseTests
{
    private const string GameId = "57ZZVAZ0PJK8VQGPKB728QE57C";
    private const int ModelInput = 640;

    private static byte[] SyntheticGray(int seed)
    {
        var buf = new byte[ModelInput * ModelInput];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    private static bool BitwiseEqual(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                return false;
        }
        return true;
    }

    [Fact]
    public void ReusedInputTensor_ReflectsBufferMutationsBetweenRuns()
    {
        var modelPath = ModelService.GetModelPath(GameId);
        Assert.True(File.Exists(modelPath),
            $"ONNX model not found at {modelPath}. This test guards a runtime contract and " +
            "cannot be verified without the real model. It must fail, not skip.");

        var session = ModelService.LoadModel(GameId);
        try
        {
            var buffer = new float[ModelInput * ModelInput * 3];
            var tensor = new DenseTensor<float>(
                buffer.AsMemory(), new[] { 1, 3, ModelInput, ModelInput });
            var container = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor)
            };
            var outputNames = session.OutputMetadata.Keys.ToList();
            using var runOptions = new RunOptions();

            float[] RunWithSeed(int seed)
            {
                DetectionFramePreprocessor.FillInputTensor(SyntheticGray(seed), buffer, ModelInput);
                using var results = session.Run(container, outputNames, runOptions);
                return results[0].AsTensor<float>().ToArray();
            }

            var a1 = RunWithSeed(1);
            var b = RunWithSeed(200);
            var a2 = RunWithSeed(1);

            Assert.True(BitwiseEqual(a1, a2),
                "Reusing the container was not deterministic: the same input produced different " +
                "output on the first and third run.");

            Assert.False(BitwiseEqual(a1, b),
                "Two different inputs produced bit-identical output: the reused NamedOnnxValue " +
                "is not picking up mutations to the underlying buffer, so inference is running " +
                "on a stale frame.");
        }
        finally
        {
            ModelService.UnloadModel(GameId);
        }
    }
}
