// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using System.Text;

namespace Tript.Shell.Linux;

internal static class GVariantText
{
    internal static string String(string value)
    {
        var text = new StringBuilder(value.Length + 2);
        text.Append('\'');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\0':
                    break;
                case '\\':
                    text.Append(@"\\");
                    break;
                case '\'':
                    text.Append(@"\'");
                    break;
                case < ' ' or '\u007f':
                    text.Append(@"\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    text.Append(character);
                    break;
            }
        }

        return text.Append('\'').ToString();
    }

    internal static string ObjectPath(string path) => "objectpath " + String(path);

    internal static string UInt32(uint value) => "uint32 " + value.ToString(CultureInfo.InvariantCulture);

    internal static string Int32(int value) => "int32 " + value.ToString(CultureInfo.InvariantCulture);

    internal static string Boolean(bool value) => value ? "true" : "false";

    internal static string Variant(string value) => "<" + value + ">";

    internal static string Tuple(params string[] items) => items.Length == 1
        ? "(" + items[0] + ",)"
        : "(" + string.Join(", ", items) + ")";

    internal static string Array(string elementType, IEnumerable<string> items) =>
        "@a" + elementType + " [" + string.Join(", ", items) + "]";

    internal static string StringArray(IEnumerable<string> items) => Array("s", items.Select(String));

    internal static string VariantDictionary(IEnumerable<KeyValuePair<string, string>> entries) =>
        "@a{sv} {" + string.Join(", ", entries.Select(entry => String(entry.Key) + ": " + Variant(entry.Value))) + "}";
}
