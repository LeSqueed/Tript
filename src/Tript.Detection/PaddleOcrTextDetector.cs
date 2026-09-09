// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tript.Detection;

internal sealed record OcrTextRegion(int X, int Y, int Width, int Height, float Confidence);

internal sealed class PaddleOcrTextDetector : IDisposable
{
    private const int MaximumSideLength = 960;
    private const float PixelThreshold = 0.3f;
    private const float BoxThreshold = 0.6f;
    private const float UnclipRatio = 1.5f;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    internal PaddleOcrTextDetector(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("PaddleOCR detector not found.", modelPath);

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AppendExecutionProvider_CPU();
        _session = new InferenceSession(modelPath, options);

        _inputName = _session.InputMetadata.Keys.Single();
        _outputName = _session.OutputMetadata.Keys.Single();
        var inputDimensions = _session.InputMetadata[_inputName].Dimensions;
        if (inputDimensions.Length != 4 || inputDimensions[1] != 3)
            throw new InvalidDataException("PaddleOCR detection input must use NCHW shape [N,3,H,W].");
    }

    internal IReadOnlyList<OcrTextRegion> Detect(byte[] bgra, int frameWidth, int frameHeight,
        int cropX, int cropY, int cropWidth, int cropHeight)
    {
        var (inputWidth, inputHeight) = ResizeDimensions(cropWidth, cropHeight);
        var input = PrepareInput(bgra, frameWidth, frameHeight, cropX, cropY, cropWidth, cropHeight,
            inputWidth, inputHeight);
        var tensor = new DenseTensor<float>(input, [1, 3, inputHeight, inputWidth]);
        using var results = _session.Run(
            [NamedOnnxValue.CreateFromTensor(_inputName, tensor)], [_outputName]);
        var output = results.Single().AsTensor<float>();
        var dimensions = output.Dimensions;
        if (dimensions.Length != 4 || dimensions[0] != 1 || dimensions[1] != 1
            || dimensions[2] <= 0 || dimensions[3] <= 0)
        {
            throw new InvalidDataException("PaddleOCR detection output must use shape [1,1,H,W].");
        }

        var span = output is DenseTensor<float> dense ? dense.Buffer.Span : output.ToArray().AsSpan();
        return FindRegions(span, dimensions[3], dimensions[2], cropWidth, cropHeight);
    }

    internal static (int Width, int Height) ResizeDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var ratio = Math.Max(width, height) > MaximumSideLength
            ? (double)MaximumSideLength / Math.Max(width, height)
            : 1d;
        var resizedWidth = Math.Max(32, (int)Math.Round(width * ratio / 32d) * 32);
        var resizedHeight = Math.Max(32, (int)Math.Round(height * ratio / 32d) * 32);
        return (resizedWidth, resizedHeight);
    }

    internal static float[] PrepareInput(byte[] bgra, int frameWidth, int frameHeight,
        int cropX, int cropY, int cropWidth, int cropHeight, int inputWidth, int inputHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || cropWidth <= 0 || cropHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(cropWidth));
        if (cropX < 0 || cropY < 0 || cropX + cropWidth > frameWidth || cropY + cropHeight > frameHeight)
            throw new ArgumentOutOfRangeException(nameof(cropX));
        if (bgra.Length < checked(frameWidth * frameHeight * 4))
            throw new ArgumentException("BGRA frame buffer is too short.", nameof(bgra));

        var planeSize = inputWidth * inputHeight;
        var output = new float[planeSize * 3];
        for (var targetY = 0; targetY < inputHeight; targetY++)
        {
            var sourceY = (targetY + 0.5) * cropHeight / inputHeight - 0.5;
            for (var targetX = 0; targetX < inputWidth; targetX++)
            {
                var sourceX = (targetX + 0.5) * cropWidth / inputWidth - 0.5;
                var target = targetY * inputWidth + targetX;
                output[target] = (PaddleOcrRecognizer.SampleBilinear(bgra, frameWidth, cropX, cropY,
                    cropWidth, cropHeight, sourceX, sourceY, 0) / 255f - 0.485f) / 0.229f;
                output[planeSize + target] = (PaddleOcrRecognizer.SampleBilinear(bgra, frameWidth,
                    cropX, cropY, cropWidth, cropHeight, sourceX, sourceY, 1) / 255f - 0.456f) / 0.224f;
                output[planeSize * 2 + target] = (PaddleOcrRecognizer.SampleBilinear(bgra, frameWidth,
                    cropX, cropY, cropWidth, cropHeight, sourceX, sourceY, 2) / 255f - 0.406f) / 0.225f;
            }
        }
        return output;
    }

    internal static IReadOnlyList<OcrTextRegion> FindRegions(ReadOnlySpan<float> probabilities,
        int mapWidth, int mapHeight, int destinationWidth, int destinationHeight)
    {
        if (mapWidth <= 0 || mapHeight <= 0 || probabilities.Length != mapWidth * mapHeight)
            throw new ArgumentException("The detection probability map has invalid dimensions.", nameof(probabilities));

        var visited = new bool[probabilities.Length];
        var queue = new int[probabilities.Length];
        var regions = new List<OcrTextRegion>();
        for (var start = 0; start < probabilities.Length; start++)
        {
            if (visited[start] || probabilities[start] <= PixelThreshold) continue;
            var head = 0;
            var tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            var minX = mapWidth;
            var minY = mapHeight;
            var maxX = 0;
            var maxY = 0;
            var score = 0d;
            var pixels = 0;
            while (head < tail)
            {
                var index = queue[head++];
                var x = index % mapWidth;
                var y = index / mapWidth;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                score += probabilities[index];
                pixels++;

                for (var adjacentY = Math.Max(0, y - 1); adjacentY <= Math.Min(mapHeight - 1, y + 1); adjacentY++)
                for (var adjacentX = Math.Max(0, x - 1); adjacentX <= Math.Min(mapWidth - 1, x + 1); adjacentX++)
                {
                    var adjacent = adjacentY * mapWidth + adjacentX;
                    if (visited[adjacent] || probabilities[adjacent] <= PixelThreshold) continue;
                    visited[adjacent] = true;
                    queue[tail++] = adjacent;
                }
            }

            var confidence = (float)(score / pixels);
            var width = maxX - minX + 1;
            var height = maxY - minY + 1;
            if (confidence < BoxThreshold || Math.Min(width, height) < 3) continue;
            var distance = width * height * UnclipRatio / (2f * (width + height));
            var left = Math.Max(0, minX - distance);
            var top = Math.Max(0, minY - distance);
            var right = Math.Min(mapWidth, maxX + 1 + distance);
            var bottom = Math.Min(mapHeight, maxY + 1 + distance);
            var destinationX = Math.Clamp((int)Math.Round(left / mapWidth * destinationWidth), 0,
                destinationWidth - 1);
            var destinationY = Math.Clamp((int)Math.Round(top / mapHeight * destinationHeight), 0,
                destinationHeight - 1);
            var destinationRight = Math.Clamp((int)Math.Round(right / mapWidth * destinationWidth),
                destinationX + 1, destinationWidth);
            var destinationBottom = Math.Clamp((int)Math.Round(bottom / mapHeight * destinationHeight),
                destinationY + 1, destinationHeight);
            regions.Add(new OcrTextRegion(destinationX, destinationY,
                destinationRight - destinationX, destinationBottom - destinationY, confidence));
        }

        return regions
            .OrderBy(region => region.Y + region.Height / 2)
            .ThenBy(region => region.X)
            .ToList();
    }

    public void Dispose() => _session.Dispose();
}
