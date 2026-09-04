// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Detection;

// The single compatibility gate used by import, training, installation, and runtime startup. A
// model's class order is part of its binary contract. events.json may append classes for a newer
// model, but it cannot remove or change the identity of any class the loaded model emits.
public static class ModelEventCompatibility
{
    public static string? FindMismatch(IReadOnlyList<EventDefinition> definitions,
        OnnxModelMetadata model)
    {
        if (!model.ClassCount.HasValue)
            return "the model output does not expose a static YOLO class count";

        return FindMismatch(definitions, model.ClassCount.Value, model.ClassNames);
    }

    internal static string? FindMismatch(IReadOnlyList<EventDefinition> definitions,
        int classCount, IReadOnlyDictionary<int, string>? classNames)
    {
        var seenClassIds = new HashSet<int>();
        foreach (var definition in definitions)
        {
            if (definition.ClassId < 0)
                return $"classId {definition.ClassId} ('{definition.Name}') is negative";

            if (!seenClassIds.Add(definition.ClassId))
                return $"classId {definition.ClassId} is duplicated in events.json";

            // Extra event definitions are safe: an older model simply cannot emit those classes.
            if (definition.ClassId >= classCount || classNames is null)
                continue;

            if (!classNames.TryGetValue(definition.ClassId, out var modelName))
            {
                return $"classId {definition.ClassId} ('{definition.Name}') is missing from the model's " +
                    "class map";
            }

            if (!string.Equals(modelName, definition.Name, StringComparison.OrdinalIgnoreCase))
            {
                return $"classId {definition.ClassId} is '{definition.Name}' in events.json but " +
                    $"'{modelName}' in the model";
            }
        }

        if (definitions.Count < classCount)
        {
            return $"model declares {classCount} classes but events.json contains " +
                $"{definitions.Count} definitions";
        }

        for (var classId = 0; classId < classCount; classId++)
        {
            if (!seenClassIds.Contains(classId))
                return $"classId {classId} is missing from events.json";
        }

        return null;
    }
}
