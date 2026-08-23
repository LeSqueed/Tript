// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Buffers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tript.Detection;

public static class ModelPredictionService
{
    public static List<DetectionResult> Predict(string modelPath, ReadOnlySpan<byte> png,
        IReadOnlyList<EventDefinition> definitions)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException("The training model is missing.", modelPath);

        var image = PngImageDecoder.Decode(png);
        using var session = new InferenceSession(modelPath);
        var metadata = OnnxModelInspector.Inspect(session, modelPath);
        if (metadata.InputChannels != 3 || metadata.InputWidth is not int inputWidth
            || metadata.InputHeight is not int inputHeight || inputWidth <= 0 || inputHeight <= 0)
        {
            throw new InvalidDataException("The model must use a static three-channel image input.");
        }
        if (!OnnxModelInspector.TryDeriveClassCount(metadata.OutputDimensions, out var classCount))
            throw new InvalidDataException("The model output does not declare a static YOLO class count.");

        var mismatch = ModelEventCompatibility.FindMismatch(definitions.ToList(), classCount, metadata.ClassNames);
        if (mismatch is not null)
            throw new InvalidDataException($"The model and training events do not match: {mismatch}");

        var inputBuffer = new float[checked(inputWidth * inputHeight * 3)];
        var inputTensor = new DenseTensor<float>(inputBuffer.AsMemory(), [1, 3, inputHeight, inputWidth]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(metadata.InputName, inputTensor),
        };
        var groups = VisualEventDetector.BuildRegionGroups(definitions.ToList());
        var detections = new List<DetectionResult>();

        foreach (var group in groups)
        {
            if (!VisualEventDetector.TryGetCropRect(group, image.Width, image.Height,
                    out var cropX, out var cropY, out var cropW, out var cropH))
                continue;

            var resized = VisualEventDetector.CropAndResizeGray(image.Pixels, image.Width, image.Height,
                cropX, cropY, cropW, cropH, inputWidth, inputHeight);
            try
            {
                VisualEventDetector.FillInputTensor(resized, inputBuffer, inputWidth, inputHeight, useVectorPath: false);
                using var results = session.Run(inputs, [metadata.OutputName]);
                var tensor = results[0].AsTensor<float>();
                var output = tensor is DenseTensor<float> dense ? dense.Buffer.Span : tensor.ToArray().AsSpan();
                var groupDetections = VisualEventDetector.ParseYoloOutputForInput(output,
                    inputWidth, inputHeight, classCount);
                VisualEventDetector.MapDetectionsToFullFrame(groupDetections, cropX, cropY, cropW, cropH,
                    image.Width, image.Height);
                detections.AddRange(groupDetections);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(resized);
            }
        }

        return detections;
    }
}
