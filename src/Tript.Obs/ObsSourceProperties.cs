// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

// The source-type introspection surface of ObsSource, split out because it is a distinct seam and
// the reason it exists is a distinct one: the capture-source settings keys are plugin-side, not in
// the libobs headers, so a recorder that must attach a game or a display cannot hardcode a key
// name. obs_get_source_properties is the runtime discovery route libobs offers; this is that route
// made typed. It answers for every registered source type — inputs, filters, transitions and scenes
// alike — and is the twin of the encoder and output type-level probes (ObsEncoder / ObsOutput).
public static class ObsSourceProperties
{
    // The ids every loaded module registered as an *input*, in registration order. The input
    // enumeration is the narrowest "what can a capture source be" account libobs offers: filters,
    // transitions and scenes are source types too but are not inputs, and obs_enum_input_types
    // never reports them.
    public static IReadOnlyList<string> EnumerateTypeIds()
    {
        var ids = new List<string>();
        for (nuint index = 0; ObsNative.obs_enum_input_types(index, out var id); index++)
        {
            var value = Utf8Marshal.ReadBorrowed(id);
            if (value is not null)
                ids.Add(value);
        }

        return ids;
    }

    // The settings object a plugin falls back on for a type, so a caller builds its own settings by
    // starting here. Null when the id is not registered.
    public static ObsSettings? GetTypeDefaults(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsSettings.FromOwnedPointerOrNull(ObsNative.obs_get_source_defaults(id));
    }

    // The properties a type exposes, free of the settings layer's round-trip assumptions: the keys
    // a plugin reads are the keys it declares here, so this is the authoritative account of what
    // can be configured and with which choices. This is the discovery route for the plugin-side
    // capture keys — game-capture's window/process selection on Windows, xshm_input's display
    // selection on Linux. Empty when the type is registered but declares no properties.
    public static IReadOnlyList<ObsSourceProperty> EnumerateTypeProperties(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return EnumerateProperties(ObsNative.obs_get_source_properties(id));
    }

    // The shared enumeration over an obs_properties_t, reached from both the type-level probe and
    // ObsSource.EnumerateProperties (the instance-level route that capture sources need).
    internal static IReadOnlyList<ObsSourceProperty> EnumerateProperties(nint propsPointer)
    {
        if (propsPointer == nint.Zero)
            return [];

        try
        {
            var properties = new List<ObsSourceProperty>();
            var property = ObsNative.obs_properties_first(propsPointer);

            while (property != nint.Zero)
            {
                var namePointer = ObsNative.obs_property_name(property);
                var name = Utf8Marshal.ReadBorrowed(namePointer) ?? string.Empty;
                var type = (ObsPropertyType)ObsNative.obs_property_get_type(property);
                var items = ReadListItems(property, type);

                properties.Add(new ObsSourceProperty(name, type, items));

                // Releases the current item and overwrites it with the next, so there is exactly one
                // live item at a time and nothing to release once it returns false.
                if (!ObsNative.obs_property_next(ref property))
                    property = nint.Zero;
            }

            return properties;
        }
        finally
        {
            ObsNative.obs_properties_destroy(propsPointer);
        }
    }

    // The items of a List property, each carrying the value in the format the list declares. An
    // item read with the wrong accessor returns 0 or null silently, so the format is what decides
    // which accessor is used.
    private static IReadOnlyList<ObsSourcePropertyItem> ReadListItems(nint property, ObsPropertyType type)
    {
        if (type is not (ObsPropertyType.List or ObsPropertyType.EditableList))
            return [];

        var format = (ObsComboFormat)ObsNative.obs_property_list_format(property);
        var count = ObsNative.obs_property_list_item_count(property);
        var items = new List<ObsSourcePropertyItem>((int)count);

        for (nuint i = 0; i < count; i++)
        {
            object? value = format switch
            {
                ObsComboFormat.String => Utf8Marshal.ReadBorrowed(ObsNative.obs_property_list_item_string(property, i)),
                ObsComboFormat.Int => ObsNative.obs_property_list_item_int(property, i),
                ObsComboFormat.Float => ObsNative.obs_property_list_item_float(property, i),
                ObsComboFormat.Bool => ObsNative.obs_property_list_item_int(property, i) != 0,
                _ => null
            };

            items.Add(new ObsSourcePropertyItem(value, format, Utf8Marshal.ReadBorrowed(
                ObsNative.obs_property_list_item_name(property, i))));
        }

        return items;
    }
}

// One property as enumeration sees it. The items are populated only for List and EditableList
// properties, which is what carries the accepted value strings a plugin compares byte for byte —
// for a capture source, the window/process/display handles the recorder must choose between.
public readonly record struct ObsSourceProperty(string Name, ObsPropertyType Type, IReadOnlyList<ObsSourcePropertyItem> Items);

// One accepted value in a List property. Format says how to interpret Value; the two are kept
// together because the format is precisely what a wrong read discards silently. Name is the item's
// display label — for a monitor list, the only place the plugin says which monitor an opaque id is.
public readonly record struct ObsSourcePropertyItem(object? Value, ObsComboFormat Format, string? Name = null);
