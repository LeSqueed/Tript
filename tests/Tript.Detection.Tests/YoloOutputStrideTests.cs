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

public class YoloOutputStrideTests
{
    private const string GameId = "57ZZVAZ0PJK8VQGPKB728QE57C";
    private const int ModelInput = 640;

    private const int YoloBoxChannels = 4;

    private sealed record ShippedOutput(Tensor<float> Tensor, float[] Values, int NumClasses, int Anchors);

    private static string ModelPath()
    {
        var modelPath = ModelService.GetModelPath(GameId);
        Assert.True(File.Exists(modelPath),
            $"ONNX model not found at {modelPath}. These tests pin the tensor layout the YOLO parser " +
            "walks and cannot be checked without the real model. They must fail, not skip.");
        return modelPath;
    }

    private static void WithShippedModelOutput(Action<ShippedOutput> body)
    {
        using var session = new InferenceSession(ModelPath());

        var gray = new byte[ModelInput * ModelInput];
        new Random(7).NextBytes(gray);

        var buffer = new float[ModelInput * ModelInput * 3];
        DetectionFramePreprocessor.FillInputTensor(gray, buffer, ModelInput);

        var input = new DenseTensor<float>(buffer.AsMemory(), new[] { 1, 3, ModelInput, ModelInput });
        var container = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(session.InputNames[0], input)
        };

        var outputName = session.OutputMetadata.Keys.First();
        var dimensions = session.OutputMetadata[outputName].Dimensions;

        Assert.True(OnnxModelInspector.TryDeriveClassCount(dimensions, out var numClasses),
            $"Output {outputName} has shape [{string.Join(',', dimensions)}] and carries no static " +
            "class dimension, so the stride under test is not defined.");

        using var runOptions = new RunOptions();
        using var results = session.Run(container, new[] { outputName }, runOptions);

        var tensor = results[0].AsTensor<float>();
        body(new ShippedOutput(tensor, tensor.ToArray(), numClasses, dimensions[2]));
    }

    [Fact]
    public void DerivedDetectionCount_EqualsTheGraphsAnchorDimension()
    {
        WithShippedModelOutput(output =>
        {
            var stride = YoloBoxChannels + output.NumClasses;

            Assert.Equal(0, output.Values.Length % stride);
            Assert.Equal(output.Anchors, output.Values.Length / stride);
            Assert.Equal(stride * output.Anchors, output.Values.Length);
        });
    }

    [Fact]
    public void AnOffByOneClassCount_MovesEveryRowBoundary()
    {
        WithShippedModelOutput(output =>
        {
            var correct = output.Values.Length / (YoloBoxChannels + output.NumClasses);

            Assert.NotEqual(correct, output.Values.Length / (YoloBoxChannels + output.NumClasses - 1));
            Assert.NotEqual(correct, output.Values.Length / (YoloBoxChannels + output.NumClasses + 1));
        });
    }

    [Fact]
    public void OutputTensor_IsDenseAndItsBufferIsExactlyTheTensor()
    {
        WithShippedModelOutput(output =>
        {
            var dense = Assert.IsType<DenseTensor<float>>(output.Tensor);

            Assert.Equal(output.Values.Length, dense.Buffer.Length);
            Assert.Equal(output.Values, dense.Buffer.Span.ToArray());
        });
    }

    [Fact]
    public void ClassRowsAreConfidences_AndBoxRowsAreInputPixels_AtTheDerivedStride()
    {
        WithShippedModelOutput(output =>
        {
            var anchors = output.Values.Length / (YoloBoxChannels + output.NumClasses);

            foreach (var confidence in output.Values.AsSpan(YoloBoxChannels * anchors))
            {
                Assert.InRange(confidence, 0f, 1f);
            }

            Assert.True(Max(output.Values.AsSpan(0, YoloBoxChannels * anchors)) > 1f,
                "Box rows are already normalized: the YOLO parser divides them by the model input " +
                "size and would shrink every box to nothing.");

            Assert.True(Max(output.Values.AsSpan((YoloBoxChannels - 1) * anchors)) > 1f,
                "Reading the class rows at the wrong offset stayed within [0,1], so the bound above " +
                "does not actually pin the row boundary.");
        });
    }

    private static float Max(ReadOnlySpan<float> values)
    {
        var max = float.NegativeInfinity;
        foreach (var value in values)
        {
            if (value > max) max = value;
        }
        return max;
    }
}
