// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text;
using System.Text.Json;
using Tript.Settings;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

internal static class SettingsPatch
{
    internal static void Apply(SettingsModel settings, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in patch.EnumerateObject())
        {
            switch (property.Name)
            {
                case "recording" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyToPage(settings.Recording, property.Value);
                    break;
                case "buffer" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyToPage(settings.Buffer, property.Value);
                    break;
                case "audio" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyToPage(settings.Audio, property.Value);
                    break;
                case "capture" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyToPage(settings.Capture, property.Value);
                    break;
                case "game" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyToPage(settings.Game, property.Value);
                    break;
                case "general" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyToPage(settings.General, property.Value);
                    break;
                case "hotkeys" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyToPage(settings.Hotkeys, property.Value);
                    break;
                case "streaming" when property.Value.ValueKind == JsonValueKind.Object:
                    ApplyToPage(settings.Streaming, property.Value);
                    break;
            }
        }
    }

    internal static bool UpdatesGameList(JsonElement patch) =>
        patch.ValueKind == JsonValueKind.Object
        && patch.TryGetProperty("game", out var game)
        && game.ValueKind == JsonValueKind.Object
        && game.TryGetProperty("gameList", out _);

    internal static bool UpdatesAutomaticClipWindows(JsonElement patch) =>
        patch.ValueKind == JsonValueKind.Object
        && ((patch.TryGetProperty("recording", out var recording)
            && recording.ValueKind == JsonValueKind.Object
            && (recording.TryGetProperty("automaticClipBeforeSeconds", out _)
                || recording.TryGetProperty("automaticClipAfterSeconds", out _)))
            || UpdatesGameList(patch));

    private static void ApplyToPage(object page, JsonElement patch)
    {
        var current = JsonSerializer.Serialize(page, SettingsSerialization.Options);
        var merged = MergeObjects(JsonDocument.Parse(current).RootElement, patch);
        var clone = JsonSerializer.Deserialize(merged, page.GetType(), SettingsSerialization.Options);
        if (clone is null)
            return;

        foreach (var property in page.GetType().GetProperties().Where(p => p.CanWrite))
        {
            var value = clone.GetType().GetProperty(property.Name)?.GetValue(clone);
            property.SetValue(page, value);
        }

        if (page is GeneralSettings general)
            SettingsSerialization.RemoveRemovedGeneralProperties(general);
    }

    private static string MergeObjects(JsonElement baseObject, JsonElement patch)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteMergedObject(writer, baseObject, patch);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteMergedObject(Utf8JsonWriter writer, JsonElement baseObject, JsonElement patch)
    {
        writer.WriteStartObject();

        foreach (var property in baseObject.EnumerateObject())
        {
            if (!patch.TryGetProperty(property.Name, out var replacement))
            {
                property.WriteTo(writer);
                continue;
            }

            writer.WritePropertyName(property.Name);
            if (property.Value.ValueKind == JsonValueKind.Object && replacement.ValueKind == JsonValueKind.Object)
                WriteMergedObject(writer, property.Value, replacement);
            else
                replacement.WriteTo(writer);
        }

        foreach (var property in patch.EnumerateObject())
        {
            if (baseObject.TryGetProperty(property.Name, out _))
                continue;

            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
