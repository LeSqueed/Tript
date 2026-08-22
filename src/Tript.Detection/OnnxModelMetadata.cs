// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;

namespace Tript.Detection;

// Structural facts read from an ONNX model. EventDefinition remains the source of application
// behaviour; this type only describes what the exported graph can accept and produce.
public sealed class OnnxModelMetadata
{
    public required string ModelPath { get; init; }

    public required string InputName { get; init; }

    public required IReadOnlyList<int> InputDimensions { get; init; }

    public required string OutputName { get; init; }

    public required IReadOnlyList<int> OutputDimensions { get; init; }

    public IReadOnlyDictionary<int, string>? ClassNames { get; init; }

    public int? InputChannels => InputDimensions.Count == 4 && InputDimensions[1] > 0
        ? InputDimensions[1]
        : null;

    public int? InputHeight => InputDimensions.Count == 4 && InputDimensions[2] > 0
        ? InputDimensions[2]
        : null;

    public int? InputWidth => InputDimensions.Count == 4 && InputDimensions[3] > 0
        ? InputDimensions[3]
        : null;

    public int? ClassCount => OnnxModelInspector.TryDeriveClassCount(OutputDimensions, out var count)
        ? count
        : null;
}

public static class OnnxModelInspector
{
    private const int YoloBoxChannels = 4;

    // Ultralytics stores names as a Python dict literal in metadata_props. The JSON parser below
    // also accepts exporters that write an object or an array instead.
    private static readonly Regex ClassNamePattern = new(
        @"(?<id>\d+)\s*:\s*(?:'(?<name>[^']*)'|""(?<name>[^""]*)"")",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static OnnxModelMetadata Inspect(string modelPath)
    {
        using var session = new InferenceSession(modelPath);
        return Inspect(session, modelPath);
    }

    internal static OnnxModelMetadata Inspect(InferenceSession session, string modelPath)
    {
        var input = session.InputMetadata.FirstOrDefault();
        var output = session.OutputMetadata.FirstOrDefault();
        if (input.Key is null || output.Key is null)
            throw new InvalidDataException("The ONNX model must declare an input and an output.");

        session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var rawNames);

        return new OnnxModelMetadata
        {
            ModelPath = modelPath,
            InputName = input.Key,
            InputDimensions = input.Value.Dimensions.ToArray(),
            OutputName = output.Key,
            OutputDimensions = output.Value.Dimensions.ToArray(),
            ClassNames = ParseClassNames(rawNames),
        };
    }

    internal static bool TryDeriveClassCount(IReadOnlyList<int>? outputDimensions, out int classCount)
    {
        classCount = 0;
        if (outputDimensions is null || outputDimensions.Count != 3)
            return false;

        var channels = outputDimensions[1];
        if (channels <= YoloBoxChannels)
            return false;

        classCount = channels - YoloBoxChannels;
        return true;
    }

    internal static IReadOnlyDictionary<int, string>? ParseClassNames(string? rawNames)
    {
        if (string.IsNullOrWhiteSpace(rawNames))
            return null;

        var json = TryParseJsonClassNames(rawNames);
        if (json is not null)
            return json;

        var literal = new Dictionary<int, string>();
        foreach (Match match in ClassNamePattern.Matches(rawNames))
        {
            if (int.TryParse(match.Groups["id"].ValueSpan, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var classId))
            {
                literal[classId] = match.Groups["name"].Value;
            }
        }

        return literal.Count > 0 ? literal : null;
    }

    private static IReadOnlyDictionary<int, string>? TryParseJsonClassNames(string rawNames)
    {
        try
        {
            using var document = JsonDocument.Parse(rawNames);
            var names = new Dictionary<int, string>();

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var classId) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        names[classId] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var classId = 0;
                foreach (var value in document.RootElement.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String)
                        return null;

                    names[classId++] = value.GetString() ?? string.Empty;
                }
            }

            return names.Count > 0 ? names : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
