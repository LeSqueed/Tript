// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using System.Text;

namespace Tript.Shell.Linux;

internal static class GVariantPrinted
{
    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    {
        "boolean", "byte", "int16", "uint16", "int32", "uint32", "int64", "uint64", "handle", "double", "string",
        "signature",
    };

    internal static GVariantNode Parse(string text)
    {
        var reader = new Reader(text);
        var value = reader.ReadValue();
        reader.SkipWhitespace();
        if (!reader.AtEnd)
            throw reader.Error("unexpected text after the value");

        return value;
    }

    private sealed class Reader(string text)
    {
        private int _position;

        internal bool AtEnd => _position >= text.Length;

        private char Current => text[_position];

        internal FormatException Error(string problem) =>
            new($"Unreadable GVariant text at offset {_position}: {problem}.");

        internal void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
                _position++;
        }

        internal GVariantNode ReadValue()
        {
            SkipWhitespace();
            if (AtEnd)
                throw Error("expected a value");

            if (Current == '@')
            {
                SkipTypeAnnotation();
                return ReadValue();
            }

            switch (Current)
            {
                case '\'' or '"':
                    return GVariantNode.Text(ReadString());
                case '(':
                    return GVariantNode.TupleOf(ReadSequence('(', ')'));
                case '[':
                    return GVariantNode.ArrayOf(ReadSequence('[', ']'));
                case '{':
                    return ReadDictionary();
                case '<':
                    return ReadVariant();
            }

            if (Current == 'b' && _position + 1 < text.Length && text[_position + 1] is '\'' or '"')
            {
                _position++;
                return GVariantNode.Text(ReadString());
            }

            var word = ReadWord();
            switch (word)
            {
                case "true":
                    return GVariantNode.Flag(true);
                case "false":
                    return GVariantNode.Flag(false);
                case "nothing":
                    return GVariantNode.None;
                case "just":
                    return ReadValue();
                case "objectpath":
                    SkipWhitespace();
                    return AtEnd || Current is not ('\'' or '"')
                        ? throw Error("an object path needs a quoted value")
                        : GVariantNode.Path(ReadString());
            }

            if (TypeKeywords.Contains(word))
                return ReadValue();

            return word.Length > 0 ? GVariantNode.Number(word) : throw Error("expected a value");
        }

        private void SkipTypeAnnotation()
        {
            _position++;
            var depth = 0;
            while (!AtEnd)
            {
                var character = Current;
                if (character is '(' or '{')
                    depth++;
                else if (character is ')' or '}')
                    depth--;
                else if (depth == 0 && char.IsWhiteSpace(character))
                    return;

                _position++;
            }
        }

        private string ReadWord()
        {
            var start = _position;
            while (!AtEnd && !char.IsWhiteSpace(Current) && Current is not (',' or ':' or ')' or ']' or '}' or '>'))
                _position++;

            return text[start.._position];
        }

        private string ReadString()
        {
            var quote = Current;
            _position++;
            var value = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Error("a string is not closed");

                var character = Current;
                _position++;
                if (character == quote)
                    return value.ToString();

                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }

                if (AtEnd)
                    throw Error("a string ends in an escape");

                var escape = Current;
                _position++;
                switch (escape)
                {
                    case 'a': value.Append('\a'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'v': value.Append('\v'); break;
                    case 'u': value.Append(ReadCodePoint(4)); break;
                    case 'U': value.Append(ReadCodePoint(8)); break;
                    default: value.Append(escape); break;
                }
            }
        }

        private string ReadCodePoint(int digits)
        {
            if (_position + digits > text.Length
                || !int.TryParse(text.AsSpan(_position, digits), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var codePoint)
                || codePoint > 0x10FFFF)
            {
                throw Error("a unicode escape is malformed");
            }

            _position += digits;
            return char.ConvertFromUtf32(codePoint);
        }

        private List<GVariantNode> ReadSequence(char open, char close)
        {
            Expect(open);
            var items = new List<GVariantNode>();
            while (true)
            {
                SkipWhitespace();
                if (TryConsume(close))
                    return items;

                items.Add(ReadValue());
                SkipWhitespace();
                if (TryConsume(','))
                    continue;

                Expect(close);
                return items;
            }
        }

        private GVariantNode ReadDictionary()
        {
            Expect('{');
            var entries = new List<KeyValuePair<GVariantNode, GVariantNode>>();
            SkipWhitespace();
            if (TryConsume('}'))
                return GVariantNode.DictionaryOf(entries);

            while (true)
            {
                var key = ReadValue();
                SkipWhitespace();
                if (TryConsume(','))
                {
                    var single = ReadValue();
                    SkipWhitespace();
                    Expect('}');
                    entries.Add(new(key, single));
                    return GVariantNode.DictionaryOf(entries);
                }

                Expect(':');
                entries.Add(new(key, ReadValue()));
                SkipWhitespace();
                if (TryConsume(','))
                    continue;

                Expect('}');
                return GVariantNode.DictionaryOf(entries);
            }
        }

        private GVariantNode ReadVariant()
        {
            Expect('<');
            var inner = ReadValue();
            SkipWhitespace();
            Expect('>');
            return GVariantNode.Boxed(inner);
        }

        private bool TryConsume(char expected)
        {
            if (AtEnd || Current != expected)
                return false;

            _position++;
            return true;
        }

        private void Expect(char expected)
        {
            SkipWhitespace();
            if (!TryConsume(expected))
                throw Error($"expected '{expected}'");
        }
    }
}
