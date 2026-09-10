// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

public static class ObsSourceProperties
{
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

    public static ObsSettings? GetTypeDefaults(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ObsSettings.FromOwnedPointerOrNull(ObsNative.obs_get_source_defaults(id));
    }

    public static IReadOnlyList<ObsSourceProperty> EnumerateTypeProperties(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return EnumerateProperties(ObsNative.obs_get_source_properties(id));
    }

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

public readonly record struct ObsSourceProperty(string Name, ObsPropertyType Type, IReadOnlyList<ObsSourcePropertyItem> Items);

public readonly record struct ObsSourcePropertyItem(object? Value, ObsComboFormat Format, string? Name = null);
