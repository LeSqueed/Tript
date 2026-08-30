// SPDX-License-Identifier: GPL-2.0-or-later

namespace Tript.GameDiscovery;

internal sealed class ValveKeyValues
{
    private readonly Dictionary<string, List<object>> _values = new(StringComparer.OrdinalIgnoreCase);

    public static ValveKeyValues Parse(string text)
    {
        var tokens = Tokenize(text);
        var index = 0;
        var root = new ValveKeyValues();
        ParseInto(root, tokens, ref index, false);
        return root;
    }

    public string? String(string key) =>
        _values.TryGetValue(key, out var values) ? values.OfType<string>().FirstOrDefault() : null;

    public ValveKeyValues? Object(string key) =>
        _values.TryGetValue(key, out var values) ? values.OfType<ValveKeyValues>().FirstOrDefault() : null;

    public IEnumerable<KeyValuePair<string, object>> Entries =>
        _values.SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, object>(pair.Key, value)));

    private void Add(string key, object value)
    {
        if (!_values.TryGetValue(key, out var values))
            _values[key] = values = [];
        values.Add(value);
    }

    private static void ParseInto(ValveKeyValues result, List<string> tokens, ref int index, bool expectsClose)
    {
        while (index < tokens.Count)
        {
            if (tokens[index] == "}")
            {
                if (!expectsClose)
                    throw new FormatException("Unexpected closing brace in Valve KeyValues data.");
                index++;
                return;
            }

            var key = tokens[index++];
            if (index >= tokens.Count)
                throw new FormatException("Valve KeyValues key has no value.");

            if (tokens[index] == "{")
            {
                index++;
                var child = new ValveKeyValues();
                ParseInto(child, tokens, ref index, true);
                result.Add(key, child);
            }
            else
            {
                result.Add(key, tokens[index++]);
            }
        }

        if (expectsClose)
            throw new FormatException("Unclosed object in Valve KeyValues data.");
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i += 2;
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (text[i] is '{' or '}') { tokens.Add(text[i++].ToString()); continue; }
            if (text[i] != '"')
                throw new FormatException($"Unexpected character at offset {i} in Valve KeyValues data.");

            i++;
            var value = new System.Text.StringBuilder();
            var closed = false;
            while (i < text.Length)
            {
                if (text[i] == '"') { i++; closed = true; break; }
                if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '\\' or '"')
                    value.Append(text[++i]);
                else
                    value.Append(text[i]);
                i++;
            }
            if (!closed) throw new FormatException("Unterminated string in Valve KeyValues data.");
            tokens.Add(value.ToString());
        }
        return tokens;
    }
}
