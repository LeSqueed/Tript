// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Detection;

public static class ModelApiV1Compatibility
{
    public const int Version = 1;

    private static readonly int[] InputDimensions = [1, 3, 640, 640];

    public static string? FindMismatch(IReadOnlyList<EventDefinition> definitions,
        OnnxModelMetadata model)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(model);

        if (model.InputElementType != typeof(float))
            return $"the model input uses {model.InputElementType.Name} values instead of float values";

        if (!model.InputDimensions.SequenceEqual(InputDimensions))
        {
            return $"the model input shape is [{string.Join(',', model.InputDimensions)}] instead of " +
                "the required NCHW shape [1,3,640,640]";
        }

        if (model.OutputElementType != typeof(float))
            return $"the model output uses {model.OutputElementType.Name} values instead of float values";

        if (model.OutputDimensions.Count != 3)
            return "the model output must use YOLO shape [batch,4+classes,anchors]";

        var batch = model.OutputDimensions[0];
        if (batch > 0 && batch != 1)
            return $"the model output declares batch size {batch} instead of 1";

        return ModelEventCompatibility.FindMismatch(definitions, model);
    }
}
