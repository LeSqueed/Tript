#if TRIPT_TRAINING

using Tript.Detection;

namespace Tript.App.Training;

internal static class TrainingEventValidator
{
    internal static void ValidateRegions(IReadOnlyList<EventDefinition> events)
    {
        foreach (var eventDefinition in events)
        {
            var values = new[]
            {
                eventDefinition.ScreenRegionX,
                eventDefinition.ScreenRegionY,
                eventDefinition.ScreenRegionW,
                eventDefinition.ScreenRegionH,
            };
            var present = values.Count(value => value.HasValue);
            if (present == 0) continue;
            if (present != values.Length || values.Any(value => !float.IsFinite(value!.Value)))
                throw new InvalidDataException($"Training event '{eventDefinition.Name}' has an incomplete screen region.");

            var x = eventDefinition.ScreenRegionX!.Value;
            var y = eventDefinition.ScreenRegionY!.Value;
            var w = eventDefinition.ScreenRegionW!.Value;
            var h = eventDefinition.ScreenRegionH!.Value;
            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1 || y + h > 1)
                throw new InvalidDataException($"Training event '{eventDefinition.Name}' has an out-of-bounds screen region.");
        }
    }
}

#endif
