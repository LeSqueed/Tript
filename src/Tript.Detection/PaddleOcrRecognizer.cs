// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tript.Detection;

internal sealed record OcrRecognition(string Text, float Confidence, int X = 0, int Y = 0,
    int Width = 0, int Height = 0);

internal sealed class PaddleOcrRecognizer : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _characters;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _inputHeight;
    private readonly int _inputWidth;
    private readonly PaddleOcrTextDetector? _detector;
    private readonly float[] _input;

    internal PaddleOcrRecognizer(string modelPath, string dictionaryPath, string? detectorPath = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("PaddleOCR model not found.", modelPath);
        if (!File.Exists(dictionaryPath)) throw new FileNotFoundException("PaddleOCR dictionary not found.", dictionaryPath);

        var lines = File.ReadAllLines(dictionaryPath)
            .Select(line => line.TrimEnd('\r', '\n'))
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0) throw new InvalidDataException("PaddleOCR dictionary is empty.");
        lines.Add(" ");
        _characters = lines.ToArray();

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AppendExecutionProvider_CPU();
        _session = new InferenceSession(modelPath, options);

        _inputName = _session.InputMetadata.Keys.Single();
        _outputName = _session.OutputMetadata.Keys.Single();
        var dimensions = _session.InputMetadata[_inputName].Dimensions;
        if (dimensions.Length != 4 || dimensions[1] != 3)
            throw new InvalidDataException("PaddleOCR recognition input must use NCHW shape [N,3,H,W].");
        _inputHeight = dimensions[2] > 0 ? dimensions[2] : 48;
        _inputWidth = dimensions[3] > 0 ? dimensions[3] : 320;
        _input = new float[_inputWidth * _inputHeight * 3];
        if (!string.IsNullOrWhiteSpace(detectorPath) && File.Exists(detectorPath))
            _detector = new PaddleOcrTextDetector(detectorPath);
    }

    internal IReadOnlyList<OcrRecognition> RecognizeAll(byte[] bgra, int frameWidth, int frameHeight,
        int cropX, int cropY, int cropWidth, int cropHeight)
    {
        if (_detector is null)
        {
            var recognition = Recognize(bgra, frameWidth, frameHeight, cropX, cropY, cropWidth, cropHeight);
            return [recognition with { X = cropX, Y = cropY, Width = cropWidth, Height = cropHeight }];
        }

        return _detector.Detect(bgra, frameWidth, frameHeight, cropX, cropY, cropWidth, cropHeight)
            .Select(region => Recognize(bgra, frameWidth, frameHeight,
                cropX + region.X, cropY + region.Y, region.Width, region.Height) with
            {
                X = cropX + region.X,
                Y = cropY + region.Y,
                Width = region.Width,
                Height = region.Height,
            })
            .ToList();
    }

    internal OcrRecognition Recognize(byte[] bgra, int frameWidth, int frameHeight,
        int cropX, int cropY, int cropWidth, int cropHeight)
    {
        PrepareInput(bgra, frameWidth, frameHeight, cropX, cropY, cropWidth, cropHeight,
            _inputWidth, _inputHeight, _input);
        var tensor = new DenseTensor<float>(_input, [1, 3, _inputHeight, _inputWidth]);
        var inputValue = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
        using var results = _session.Run([inputValue], [_outputName]);
        var output = results.Single().AsTensor<float>();
        var dimensions = output.Dimensions;
        if (dimensions.Length != 3 || dimensions[0] != 1)
            throw new InvalidDataException("PaddleOCR recognition output must use shape [1,T,C].");

        var span = output is DenseTensor<float> dense ? dense.Buffer.Span : output.ToArray().AsSpan();
        return DecodeCtc(span, dimensions[1], dimensions[2], _characters);
    }

    internal static float[] PrepareInput(byte[] bgra, int frameWidth, int frameHeight,
        int cropX, int cropY, int cropWidth, int cropHeight, int inputWidth, int inputHeight)
    {
        var output = new float[inputWidth * inputHeight * 3];
        PrepareInput(bgra, frameWidth, frameHeight, cropX, cropY, cropWidth, cropHeight, inputWidth, inputHeight,
            output);
        return output;
    }

    internal static void PrepareInput(byte[] bgra, int frameWidth, int frameHeight,
        int cropX, int cropY, int cropWidth, int cropHeight, int inputWidth, int inputHeight, Span<float> output)
    {
        BgraBilinearSampler.Validate(bgra, frameWidth, frameHeight, cropX, cropY, cropWidth, cropHeight);

        var resizedWidth = Math.Min(inputWidth,
            Math.Max(1, (int)Math.Round((double)cropWidth * inputHeight / cropHeight)));
        var planeSize = inputWidth * inputHeight;
        output = output[..(planeSize * 3)];
        output.Clear();
        BgraBilinearSampler.SamplePlanes(bgra, frameWidth, cropX, cropY, cropWidth, cropHeight,
            resizedWidth, inputHeight, inputWidth, output);

        for (var channel = 0; channel < 3; channel++)
        {
            for (var targetY = 0; targetY < inputHeight; targetY++)
            {
                var row = output.Slice(planeSize * channel + targetY * inputWidth, resizedWidth);
                for (var targetX = 0; targetX < row.Length; targetX++)
                    row[targetX] = row[targetX] / 127.5f - 1f;
            }
        }
    }

    internal static OcrRecognition DecodeCtc(ReadOnlySpan<float> output, int timeSteps, int classCount,
        IReadOnlyList<string> characters)
    {
        if (timeSteps <= 0 || classCount <= 1 || output.Length != timeSteps * classCount)
            throw new InvalidDataException("PaddleOCR recognition output has invalid dimensions.");
        if (classCount != characters.Count + 1)
            throw new InvalidDataException("PaddleOCR output class count does not match its dictionary.");

        var text = new System.Text.StringBuilder();
        var confidenceTotal = 0d;
        var emitted = 0;
        var previousClass = -1;
        for (var time = 0; time < timeSteps; time++)
        {
            var offset = time * classCount;
            var bestClass = 0;
            var bestValue = output[offset];
            for (var candidate = 1; candidate < classCount; candidate++)
            {
                if (output[offset + candidate] <= bestValue) continue;
                bestValue = output[offset + candidate];
                bestClass = candidate;
            }

            if (bestClass != 0 && bestClass != previousClass)
            {
                var characterIndex = bestClass - 1;
                if (characterIndex < characters.Count)
                {
                    text.Append(characters[characterIndex]);
                    confidenceTotal += ToProbability(output, offset, classCount, bestClass);
                    emitted++;
                }
            }
            previousClass = bestClass;
        }

        return new OcrRecognition(text.ToString(), emitted == 0 ? 0 : (float)(confidenceTotal / emitted));
    }

    private static double ToProbability(ReadOnlySpan<float> values, int offset, int count, int selected)
    {
        var value = values[offset + selected];

        if (value is >= 0 and <= 1) return value;

        var maximum = float.NegativeInfinity;
        for (var index = 0; index < count; index++)
            if (values[offset + index] > maximum) maximum = values[offset + index];
        var denominator = 0d;
        for (var index = 0; index < count; index++)
            denominator += Math.Exp(values[offset + index] - maximum);
        return Math.Exp(value - maximum) / denominator;
    }

    public void Dispose()
    {
        _detector?.Dispose();
        _session.Dispose();
    }
}
