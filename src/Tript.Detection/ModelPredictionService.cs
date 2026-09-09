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
        var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
        if (mismatch is not null)
            throw new InvalidDataException($"The model and training events do not match: {mismatch}");

        var inputWidth = metadata.InputWidth!.Value;
        var inputHeight = metadata.InputHeight!.Value;
        var classCount = metadata.ClassCount!.Value;

        var inputBuffer = new float[checked(inputWidth * inputHeight * 3)];
        var inputTensor = new DenseTensor<float>(inputBuffer.AsMemory(), [1, 3, inputHeight, inputWidth]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(metadata.InputName, inputTensor),
        };
        var groups = DetectionFramePreprocessor.BuildRegionGroups(definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object).ToList());
        var detections = new List<DetectionResult>();

        foreach (var group in groups)
        {
            if (!DetectionFramePreprocessor.TryGetCropRect(group, image.Width, image.Height,
                    out var cropX, out var cropY, out var cropW, out var cropH))
                continue;

            var resized = DetectionFramePreprocessor.CropAndResizeGray(image.Pixels, image.Width, image.Height,
                cropX, cropY, cropW, cropH, inputWidth, inputHeight);
            try
            {
                DetectionFramePreprocessor.FillInputTensor(resized, inputBuffer, inputWidth, inputHeight, useVectorPath: false);
                using var results = session.Run(inputs, [metadata.OutputName]);
                var tensor = results[0].AsTensor<float>();
                var output = tensor is DenseTensor<float> dense ? dense.Buffer.Span : tensor.ToArray().AsSpan();
                var groupDetections = DetectionFramePreprocessor.ParseYoloOutputForInput(output,
                    inputWidth, inputHeight, classCount);
                DetectionFramePreprocessor.MapDetectionsToFullFrame(groupDetections, cropX, cropY, cropW, cropH,
                    image.Width, image.Height);
                DetectionFramePreprocessor.FilterDetectionsToEventRegions(groupDetections, definitions);
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
