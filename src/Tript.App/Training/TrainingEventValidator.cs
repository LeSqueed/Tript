#if TRIPT_TRAINING

using System.Globalization;
using Tript.Detection;

namespace Tript.App.Training;

internal static class TrainingEventValidator
{
    internal static void ValidateDetectionKinds(IReadOnlyList<EventDefinition> events)
    {
        var objectEvents = events.Where(eventDefinition =>
            eventDefinition.DetectionKind == DetectionKind.Object).ToList();
        if (objectEvents.Select(eventDefinition => eventDefinition.ClassId).Distinct().Count()
            != objectEvents.Count)
        {
            throw new InvalidDataException("Object detection event class ids must be unique.");
        }

        foreach (var eventDefinition in events)
        {
            if (eventDefinition.DetectionKind == DetectionKind.Object)
            {
                if (eventDefinition.ClassId < 0)
                    throw new InvalidDataException($"Object event '{eventDefinition.Name}' has a negative class id.");
                continue;
            }

            if (eventDefinition.Ocr is null || eventDefinition.Ocr.Patterns.Count == 0)
                throw new InvalidDataException($"OCR event '{eventDefinition.Name}' requires at least one pattern.");
            foreach (var pattern in eventDefinition.Ocr.Patterns)
            {
                try
                {
                    _ = System.Globalization.CultureInfo.GetCultureInfo(pattern.LanguageTag);
                    OcrTokenTemplateMatcher.Validate(pattern);
                }
                catch (Exception exception) when (exception is CultureNotFoundException or FormatException)
                {
                    throw new InvalidDataException(
                        $"OCR event '{eventDefinition.Name}' has an invalid pattern: {exception.Message}", exception);
                }
            }
            if (!float.IsFinite(eventDefinition.Ocr.MinimumConfidence)
                || eventDefinition.Ocr.MinimumConfidence is < 0 or > 1)
            {
                throw new InvalidDataException(
                    $"OCR event '{eventDefinition.Name}' has an invalid minimum confidence.");
            }
            ValidateOcrSegments(eventDefinition);
        }
    }

    internal static void ValidateRegions(IReadOnlyList<EventDefinition> events)
    {
        foreach (var eventDefinition in events)
        {
            ValidateRegion($"Training event '{eventDefinition.Name}'", eventDefinition.ScreenRegionX,
                eventDefinition.ScreenRegionY, eventDefinition.ScreenRegionW, eventDefinition.ScreenRegionH);
        }
    }

    internal static void ValidateRegionGroups(IReadOnlyList<TrainingRegionGroup> groups)
    {
        if (groups.Select(group => group.Id).Distinct().Count() != groups.Count)
            throw new InvalidDataException("Training region group ids must be unique.");
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
                throw new InvalidDataException("Every training region group requires a name.");
            ValidateRegion($"Training region group '{group.Name}'", group.ScreenRegionX,
                group.ScreenRegionY, group.ScreenRegionW, group.ScreenRegionH);
        }
    }

    internal static void ValidateRegionGroupReferences(IReadOnlyList<EventDefinition> events,
        IReadOnlyList<TrainingRegionGroup> groups)
    {
        var ids = groups.Select(group => group.Id).ToHashSet();
        var invalid = events.FirstOrDefault(eventDefinition =>
            eventDefinition.RegionGroupId is int id && !ids.Contains(id));
        if (invalid is not null)
            throw new InvalidDataException(
                $"Training event '{invalid.Name}' references a missing region group.");
    }

    internal static void ValidateFixedPositions(IReadOnlyList<EventDefinition> events)
    {
        foreach (var eventDefinition in events.Where(eventDefinition =>
                     eventDefinition.DetectionKind == DetectionKind.Object && eventDefinition.FixedPosition))
        {
            var values = new[]
            {
                eventDefinition.FixedLabelCenterX, eventDefinition.FixedLabelCenterY,
                eventDefinition.FixedLabelWidth, eventDefinition.FixedLabelHeight,
            };
            var present = values.Count(value => value.HasValue);
            if (present == 0) continue;
            if (present != values.Length || values.Any(value => !double.IsFinite(value!.Value)))
                throw new InvalidDataException(
                    $"Training event '{eventDefinition.Name}' has an incomplete fixed label position.");
            var error = TrainingLabelValidator.FindBlockingError(
                [new TrainingLabel
                {
                    ClassId = eventDefinition.ClassId,
                    CenterX = eventDefinition.FixedLabelCenterX!.Value,
                    CenterY = eventDefinition.FixedLabelCenterY!.Value,
                    Width = eventDefinition.FixedLabelWidth!.Value,
                    Height = eventDefinition.FixedLabelHeight!.Value,
                }], events);
            if (error is not null)
                throw new InvalidDataException(
                    $"Training event '{eventDefinition.Name}' has an invalid fixed label position: {error}.");
        }
    }

    private static void ValidateOcrSegments(EventDefinition eventDefinition)
    {
        var segmentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in eventDefinition.Ocr!.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Id) || !segmentIds.Add(segment.Id))
                throw new InvalidDataException(
                    $"OCR event '{eventDefinition.Name}' segment ids must be non-empty and unique.");
            if (!float.IsFinite(segment.X) || !float.IsFinite(segment.Y)
                || !float.IsFinite(segment.Width) || !float.IsFinite(segment.Height)
                || segment.X < 0 || segment.Y < 0 || segment.Width <= 0 || segment.Height <= 0
                || segment.X + segment.Width > 1 || segment.Y + segment.Height > 1)
            {
                throw new InvalidDataException(
                    $"OCR event '{eventDefinition.Name}' has an out-of-bounds segment.");
            }
        }
    }

    internal static void ValidateSubtractorReferences(IReadOnlyList<EventDefinition> events)
    {
        if (events.Select(eventDefinition => eventDefinition.Id).Distinct().Count() != events.Count)
            throw new InvalidDataException("Training event ids must be unique.");
        var byId = events.ToDictionary(eventDefinition => eventDefinition.Id);
        foreach (var eventDefinition in events.Where(eventDefinition => eventDefinition.Type == EventType.Subtractor))
        {
            if (eventDefinition.SubtractsEventId is not int targetId)
                throw new InvalidDataException(
                    $"Training subtractor '{eventDefinition.Name}' must reference a trigger event.");
            if (targetId == eventDefinition.Id)
                throw new InvalidDataException(
                    $"Training subtractor '{eventDefinition.Name}' cannot subtract itself.");
            if (!byId.TryGetValue(targetId, out var target))
                throw new InvalidDataException(
                    $"Training subtractor '{eventDefinition.Name}' references a missing event.");
            if (target.Type != EventType.Trigger)
                throw new InvalidDataException(
                    $"Training subtractor '{eventDefinition.Name}' must reference a trigger event.");
        }
    }

    private static void ValidateRegion(string subject, float? x, float? y, float? width, float? height)
    {
        var values = new[] { x, y, width, height };
        var present = values.Count(value => value.HasValue);
        if (present == 0) return;
        if (present != values.Length || values.Any(value => !float.IsFinite(value!.Value)))
            throw new InvalidDataException($"{subject} has an incomplete screen region.");
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1 || y + height > 1)
            throw new InvalidDataException($"{subject} has an out-of-bounds screen region.");
    }
}

#endif
