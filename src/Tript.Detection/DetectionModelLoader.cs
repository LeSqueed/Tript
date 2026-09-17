// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;

namespace Tript.Detection;

internal static class DetectionModelLoader
{
    internal const int ModelInputSize = 640;

    internal static (List<EventDefinition> Definitions, InferenceSession? Session) LoadCompatibleModel(string gameId)
    {
        InferenceSession? session = null;
        try
        {
            while (true)
            {
                var definitions = ModelService.LoadEventDefinitions(gameId);
                if (!definitions.Any(definition => definition.DetectionKind == DetectionKind.Object))
                    return (definitions, null);

                session = ModelService.LoadModel(gameId);
                var metadata = OnnxModelInspector.Inspect(session, ModelService.GetModelPath(gameId));
                var apiMismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
                if (apiMismatch is null)
                    return (definitions, session);

                ModelService.UnloadModel(gameId);
                session = null;
                if (!ModelService.RejectCurrentBundle(gameId, out var rejectedPath))
                {
                    throw new InvalidDataException(
                        $"Model API v1 compatibility failed for {gameId}: {apiMismatch}");
                }

                Log.Warning(
                    "VisualEventDetector: skipping incompatible model bundle {ModelPath} for {GameId}: {Mismatch}",
                    rejectedPath, gameId, apiMismatch);
            }
        }
        catch
        {
            if (session is not null)
                ModelService.UnloadModel(gameId);
            throw;
        }
    }

    internal sealed record InferenceState(
        float[] InputBuffer,
        DenseTensor<float> InputTensor,
        List<string> OutputNames,
        List<NamedOnnxValue> InputContainer,
        int NumClasses,
        List<RegionGroup> RegionGroups);

    internal static InferenceState CreateInferenceState(InferenceSession session, List<EventDefinition> definitions,
        string gameId)
    {
        var inputBuffer = new float[ModelInputSize * ModelInputSize * 3];
        var inputTensor = new DenseTensor<float>(inputBuffer.AsMemory(), new[] { 1, 3, ModelInputSize, ModelInputSize });
        var outputNames = session.OutputMetadata.Keys.ToList();
        List<NamedOnnxValue> inputContainer = [NamedOnnxValue.CreateFromTensor(session.InputNames[0], inputTensor)];
        var numClasses = ResolveClassCount(session, outputNames[0], definitions, gameId);
        return new InferenceState(inputBuffer, inputTensor, outputNames, inputContainer, numClasses,
            BuildRuntimeRegionGroups(definitions, numClasses));
    }

    private static int ResolveClassCount(InferenceSession session, string outputName,
        List<EventDefinition> definitions, string gameId)
    {
        var dimensions = session.OutputMetadata[outputName].Dimensions;
        var modelClassNames = ReadModelClassNames(session);

        if (!OnnxModelInspector.TryDeriveClassCount(dimensions, out var numClasses))
        {
            numClasses = definitions.Count(definition => definition.DetectionKind == DetectionKind.Object);
            Log.Warning("VisualEventDetector: output {OutputName} of model {GameId} has no static class dimension ({Dimensions}), falling back to {NumClasses} classes from events.json",
                outputName, gameId, string.Join('x', dimensions), numClasses);
        }

        var mismatch = ModelEventCompatibility.FindMismatch(definitions, numClasses, modelClassNames);
        if (mismatch != null)
        {
            Log.Error("VisualEventDetector: events.json does not match model.onnx for {GameId}: {Mismatch}",
                gameId, mismatch);
            throw new InvalidOperationException(
                $"Event definitions for {gameId} do not match model.onnx: {mismatch}");
        }

        Log.Information("VisualEventDetector: model {GameId} declares {NumClasses} classes for {EventCount} event definitions",
            gameId, numClasses, definitions.Count);
        return numClasses;
    }

    private static IReadOnlyDictionary<int, string>? ReadModelClassNames(InferenceSession session)
    {
        try
        {
            return session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var names)
                ? OnnxModelInspector.ParseClassNames(names)
                : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VisualEventDetector: could not read the model class map, skipping the events.json name check");
            return null;
        }
    }

    internal static List<RegionGroup> BuildRuntimeRegionGroups(
        IReadOnlyList<EventDefinition> definitions, int numClasses)
    {
        var modelDefinitions = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object
                && (uint)definition.ClassId < (uint)numClasses)
            .ToList();
        return DetectionFramePreprocessor.BuildRegionGroups(modelDefinitions);
    }
}
