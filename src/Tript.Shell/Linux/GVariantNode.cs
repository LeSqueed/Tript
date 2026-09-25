// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;

namespace Tript.Shell.Linux;

internal enum GVariantKind
{
    String,
    ObjectPath,
    Number,
    Boolean,
    Nothing,
    Variant,
    Tuple,
    Array,
    Dictionary,
}

internal sealed class GVariantNode
{
    private GVariantNode(GVariantKind kind, string? scalar, IReadOnlyList<GVariantNode>? items,
        IReadOnlyList<KeyValuePair<GVariantNode, GVariantNode>>? entries)
    {
        Kind = kind;
        Scalar = scalar;
        Items = items ?? [];
        Entries = entries ?? [];
    }

    internal GVariantKind Kind { get; }

    internal string? Scalar { get; }

    internal IReadOnlyList<GVariantNode> Items { get; }

    internal IReadOnlyList<KeyValuePair<GVariantNode, GVariantNode>> Entries { get; }

    internal static GVariantNode Text(string value) => new(GVariantKind.String, value, null, null);

    internal static GVariantNode Path(string value) => new(GVariantKind.ObjectPath, value, null, null);

    internal static GVariantNode Number(string literal) => new(GVariantKind.Number, literal, null, null);

    internal static GVariantNode Flag(bool value) => new(GVariantKind.Boolean, value ? "true" : "false", null, null);

    internal static GVariantNode None { get; } = new(GVariantKind.Nothing, null, null, null);

    internal static GVariantNode Boxed(GVariantNode inner) => new(GVariantKind.Variant, null, [inner], null);

    internal static GVariantNode TupleOf(IReadOnlyList<GVariantNode> items) => new(GVariantKind.Tuple, null, items, null);

    internal static GVariantNode ArrayOf(IReadOnlyList<GVariantNode> items) => new(GVariantKind.Array, null, items, null);

    internal static GVariantNode DictionaryOf(IReadOnlyList<KeyValuePair<GVariantNode, GVariantNode>> entries) =>
        new(GVariantKind.Dictionary, null, null, entries);

    internal GVariantNode Unboxed => Kind == GVariantKind.Variant ? Items[0].Unboxed : this;

    internal GVariantNode? this[int index]
    {
        get
        {
            var node = Unboxed;
            return index >= 0 && index < node.Items.Count ? node.Items[index] : null;
        }
    }

    internal string? AsString()
    {
        var node = Unboxed;
        return node.Kind is GVariantKind.String or GVariantKind.ObjectPath ? node.Scalar : null;
    }

    internal bool? AsBoolean()
    {
        var node = Unboxed;
        return node.Kind == GVariantKind.Boolean ? node.Scalar == "true" : null;
    }

    internal uint? AsUInt32() => AsUInt64() is { } value && value <= uint.MaxValue ? (uint)value : null;

    internal ulong? AsUInt64()
    {
        var node = Unboxed;
        if (node.Kind != GVariantKind.Number || node.Scalar is not { } literal)
            return null;

        if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(literal.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out var hex) ? hex : null;
        }

        return ulong.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    internal GVariantNode? Lookup(string key)
    {
        foreach (var (entryKey, value) in Unboxed.Entries)
        {
            if (entryKey.AsString() == key)
                return value.Unboxed;
        }

        return null;
    }
}
