// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text;

namespace Tript.Detection;

public sealed record OcrTemplateMatch(
    string LanguageTag,
    string Template,
    string NormalizedText,
    double Score,
    IReadOnlyDictionary<string, string> Captures);

public static class OcrTextNormalizer
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // FormD splits accented letters into base + combining mark; dropping the marks folds
        // player-name diacritics (SVA\u010cINA -> SVACINA) to the ASCII the dictionary can represent.
        // Non-Latin letters have no ASCII form and become word breaks.
        var normalized = text.Normalize(NormalizationForm.FormD).ToUpperInvariant();
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (character is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '\'')
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(character);
                pendingSpace = false;
                continue;
            }

            if (character is '\u2018' or '\u2019')
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append('\'');
                pendingSpace = false;
                continue;
            }

            pendingSpace = builder.Length > 0;
        }

        return builder.ToString();
    }
}

public static class OcrTokenTemplateMatcher
{
    // The per-event minimumConfidence gates a recognition before it is matched. On small in-game
    // fonts a correct read routinely scores 0.5–0.7, so when the value is left near the default the
    // template match is the real precision gate and a lower confidence is accepted.
    public static float EffectiveMinimumConfidence(float configured)
        => configured >= 0.5f ? 0.45f : configured;

    private const int MaximumTemplateLength = 512;
    private const int MaximumTemplateTokens = 64;
    private const int MaximumCaptureTokens = 16;

    // Older events.json bakes maximumEditDistance:1 / minimumScore:0.85, both too strict for small
    // in-game fonts. A pattern still at those defaults is "unconfigured": the matcher then scales
    // edit tolerance with literal length and caps the score gate. A pattern with any other values
    // is honoured exactly. Relaxation is threaded as a negative maximumEditDistance sentinel so the
    // recursive helpers keep their signatures.
    private const int RelaxSentinel = -1;
    private const double LiteralBudgetFraction = 0.35;
    private const int LiteralBudgetFloor = 2;
    private const double InternalMinScoreCap = 0.72;
    private const double CompactLiteralFloor = 0.4;

    private static bool IsUnconfigured(OcrPatternDefinition pattern)
        => pattern.MaximumEditDistance <= 1 && pattern.MinimumScore >= 0.85;

    private static int EffectiveBudget(int maxEditDistance, int literalLength)
        => maxEditDistance >= 0
            ? maxEditDistance
            : Math.Max(LiteralBudgetFloor, (int)Math.Ceiling(literalLength * LiteralBudgetFraction));

    public static OcrTemplateMatch? FindBestMatch(string text,
        IReadOnlyList<OcrPatternDefinition> patterns)
    {
        var normalizedText = OcrTextNormalizer.Normalize(text);
        if (normalizedText.Length == 0) return null;

        var textTokens = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        OcrTemplateMatch? best = null;
        foreach (var pattern in patterns)
        {
            var match = MatchPattern(textTokens, normalizedText, pattern);
            if (match is not null && (best is null || match.Score > best.Score))
                best = match;
        }

        return best;
    }

    public static void Validate(OcrPatternDefinition pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern.LanguageTag))
            throw new FormatException("An OCR pattern requires a language tag.");
        if (pattern.MaximumEditDistance is < 0 or > 4)
            throw new FormatException("OCR pattern maximumEditDistance must be between 0 and 4.");
        if (!double.IsFinite(pattern.MinimumScore) || pattern.MinimumScore is < 0 or > 1)
            throw new FormatException("OCR pattern minimumScore must be between 0 and 1.");

        var tokens = Parse(pattern.Template);
        if (tokens.All(token => token is CaptureToken))
            throw new FormatException("An OCR pattern must contain at least one literal token.");
    }

    private static OcrTemplateMatch? MatchPattern(string[] textTokens, string normalizedText,
        OcrPatternDefinition pattern)
    {
        IReadOnlyList<TemplateToken> templateTokens;
        try
        {
            Validate(pattern);
            templateTokens = Parse(pattern.Template);
        }
        catch (FormatException)
        {
            return null;
        }

        var relax = IsUnconfigured(pattern);
        var editArg = relax ? RelaxSentinel : pattern.MaximumEditDistance;
        var minimumScore = relax
            ? Math.Min(pattern.MinimumScore, InternalMinScoreCap)
            : pattern.MinimumScore;

        MatchState? tokenBest = null;
        for (var start = 0; start < textTokens.Length; start++)
        {
            var captures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FindMatches(templateTokens, textTokens, editArg,
                templateIndex: 0, textIndex: start, literalScore: 0, literalCount: 0, captures,
                candidate =>
                {
                    if (tokenBest is null || candidate.Score > tokenBest.Score) tokenBest = candidate;
                });
        }

        // OCR routinely drops inter-word spaces, so the compact (space-insensitive) pass and the
        // suffix anchor are tried every time, not only as a fallback.
        var compactBest = FindCompactMatch(templateTokens, normalizedText, editArg, minimumScore);
        var suffixBest = relax
            ? MatchAnchoredSuffix(templateTokens, normalizedText, editArg, minimumScore)
            : null;

        var best = Pick(tokenBest, compactBest, suffixBest);
        if (best is null || best.Score < minimumScore) return null;

        // The trailing literal run is the discriminator (e.g. "TURRET" in ELIMINATED {p} TURRET).
        // A relaxed average can let a well-read "ELIMINATED" carry a barely-read tail, so require
        // the tail to actually be present near the end of the text on its own.
        if (relax && templateTokens.Any(token => token is CaptureToken)
            && !TrailingLiteralRunReadable(templateTokens, normalizedText, editArg))
        {
            return null;
        }

        return new OcrTemplateMatch(pattern.LanguageTag, pattern.Template, normalizedText,
            best.Score, best.Captures);
    }

    private const double TrailingRunFloor = 0.78;

    private static bool TrailingLiteralRunReadable(IReadOnlyList<TemplateToken> tokens,
        string normalizedText, int editArg)
    {
        var run = new List<string>();
        for (var i = tokens.Count - 1; i >= 0 && tokens[i] is LiteralToken literal; i--)
            run.Insert(0, literal.Value);
        var tail = string.Concat(run);
        if (tail.Length < 3) return true;

        var compact = Compact(normalizedText).Value;
        if (compact.Length < 3) return false;
        var budget = EffectiveBudget(editArg, tail.Length);
        for (var length = Math.Max(3, tail.Length - budget);
             length <= Math.Min(compact.Length, tail.Length + budget); length++)
        {
            var candidate = compact[^length..];
            var distance = LevenshteinDistance(tail, candidate, budget);
            if (distance > budget) continue;
            if (1d - (double)distance / Math.Max(tail.Length, candidate.Length) >= TrailingRunFloor)
                return true;
        }
        return false;
    }

    // Highest score wins, but a candidate that captured the pattern's variables beats a bare
    // literal/suffix match unless the latter scores much higher.
    private static MatchState? Pick(params MatchState?[] candidates)
    {
        MatchState? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate is null) continue;
            if (best is null)
            {
                best = candidate;
                continue;
            }

            var candidateHasCaptures = candidate.Captures.Count > 0;
            var bestHasCaptures = best.Captures.Count > 0;
            if (candidateHasCaptures == bestHasCaptures)
            {
                if (candidate.Score > best.Score) best = candidate;
            }
            else if (candidateHasCaptures)
            {
                if (candidate.Score >= best.Score - 0.15) best = candidate;
            }
            else
            {
                if (candidate.Score > best.Score + 0.15) best = candidate;
            }
        }
        return best;
    }

    // Matches only the template's trailing literal run (e.g. "STEEL TRAP") as a fuzzy end-of-text
    // anchor, ignoring a mangled "ELIMINATED {player}" prefix. Requires the leading literal to
    // appear earlier with real capture text between it and the tail, so an unrelated line that
    // merely ends in the same words is not accepted.
    private static MatchState? MatchAnchoredSuffix(IReadOnlyList<TemplateToken> tokens,
        string normalizedText, int editArg, double minimumScore)
    {
        var lastCapture = -1;
        var firstCapture = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is not CaptureToken) continue;
            if (firstCapture < 0) firstCapture = i;
            lastCapture = i;
        }
        if (firstCapture <= 0 || lastCapture >= tokens.Count - 1) return null;

        var leadRun = string.Concat(tokens.Take(firstCapture).OfType<LiteralToken>().Select(t => t.Value));
        var tailRun = string.Concat(tokens.Skip(lastCapture + 1).OfType<LiteralToken>().Select(t => t.Value));
        if (leadRun.Length < 3 || tailRun.Length < 4) return null;

        var compact = Compact(normalizedText).Value;
        if (compact.Length < tailRun.Length + 3) return null;

        var budget = EffectiveBudget(editArg, tailRun.Length);
        var tailScore = 0d;
        var tailStart = compact.Length;
        for (var length = Math.Max(1, tailRun.Length - budget);
             length <= Math.Min(compact.Length, tailRun.Length + budget); length++)
        {
            var candidate = compact[^length..];
            var distance = LevenshteinDistance(tailRun, candidate, budget);
            if (distance > budget) continue;
            var score = 1d - (double)distance / Math.Max(tailRun.Length, candidate.Length);
            if (score <= tailScore) continue;
            tailScore = score;
            tailStart = compact.Length - length;
        }
        if (tailScore < minimumScore) return null;

        var leadEnd = FuzzySpanEnd(compact[..tailStart], leadRun, editArg, 0.5);
        if (leadEnd < 0 || tailStart - leadEnd < 2) return null;

        return new MatchState(tailScore,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    // The end index (exclusive) of the best fuzzy occurrence of needle in haystack, or -1.
    private static int FuzzySpanEnd(string haystack, string needle, int editArg, double minimumSimilarity)
    {
        if (needle.Length < 3 || haystack.Length < 3) return -1;
        var budget = EffectiveBudget(editArg, needle.Length);
        var bestEnd = -1;
        var bestSimilarity = minimumSimilarity;
        for (var start = 0; start + 3 <= haystack.Length; start++)
        for (var length = Math.Max(3, needle.Length - budget);
             length <= Math.Min(haystack.Length - start, needle.Length + budget); length++)
        {
            var candidate = haystack.Substring(start, length);
            var distance = LevenshteinDistance(needle, candidate, budget);
            if (distance > budget) continue;
            var similarity = 1d - (double)distance / Math.Max(needle.Length, candidate.Length);
            if (similarity < bestSimilarity) continue;
            bestSimilarity = similarity;
            bestEnd = start + length;
        }
        return bestEnd;
    }

    private static MatchState? FindCompactMatch(IReadOnlyList<TemplateToken> templateTokens,
        string normalizedText, int maximumEditDistance, double minimumScore)
    {
        var compactText = Compact(normalizedText);
        var bestScore = double.NegativeInfinity;
        MatchState? bestValid = null;
        for (var start = 0; start < compactText.Value.Length; start++)
        {
            if (start > 0 && compactText.TokenIndexes[start] == compactText.TokenIndexes[start - 1]) continue;
            var captures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FindCompactMatches(templateTokens, compactText, maximumEditDistance, minimumScore,
                templateIndex: 0, textIndex: start, literalScore: 0, literalCount: 0,
                capturesValid: true, captures,
                candidate =>
                {
                    if (candidate.Score > bestScore)
                    {
                        bestScore = candidate.Score;
                        bestValid = candidate.CapturesValid ? candidate.Match : null;
                    }
                    else if (candidate.Score == bestScore && candidate.CapturesValid && bestValid is null)
                    {
                        bestValid = candidate.Match;
                    }
                });
        }
        return bestValid;
    }

    private static void FindCompactMatches(IReadOnlyList<TemplateToken> templateTokens,
        CompactText text, int maximumEditDistance, double minimumScore, int templateIndex, int textIndex,
        double literalScore, int literalCount, bool capturesValid,
        Dictionary<string, string> captures, Action<CompactMatchState> onMatch)
    {
        if (templateIndex == templateTokens.Count)
        {
            if (literalCount > 0)
                onMatch(new CompactMatchState(new MatchState(literalScore / literalCount,
                    new Dictionary<string, string>(captures, StringComparer.OrdinalIgnoreCase)), capturesValid));
            return;
        }

        if (textIndex >= text.Value.Length) return;

        if (templateTokens[templateIndex] is LiteralToken literal)
        {
            var budget = EffectiveBudget(maximumEditDistance, literal.Value.Length);
            var minimumLength = Math.Max(1, literal.Value.Length - budget);
            var maximumLength = Math.Min(text.Value.Length - textIndex, literal.Value.Length + budget);
            for (var length = minimumLength; length <= maximumLength; length++)
            {
                var candidate = text.Value.Substring(textIndex, length);
                var distance = LevenshteinDistance(literal.Value, candidate, budget);
                if (distance > budget) continue;

                var score = 1d - (double)distance / Math.Max(literal.Value.Length, candidate.Length);
                // A single badly-mangled literal must not veto a run whose average still clears
                // the gate applied to the completed match, so this floor is deliberately low.
                if (score < CompactLiteralFloor) continue;
                FindCompactMatches(templateTokens, text, maximumEditDistance, minimumScore, templateIndex + 1,
                    textIndex + length, literalScore + score, literalCount + 1, capturesValid, captures, onMatch);
            }
            return;
        }

        var capture = (CaptureToken)templateTokens[templateIndex];
        for (var end = textIndex; end <= text.Value.Length; end++)
        {
            var tokenCount = end == textIndex
                ? 0
                : text.TokenIndexes[end - 1] - text.TokenIndexes[textIndex] + 1;
            if (tokenCount > capture.MaximumTokens) break;

            var capturedChars = end - textIndex;
            if (end == textIndex)
            {
                captures[capture.Name] = string.Empty;
            }
            else
            {
                var sourceStart = text.SourceIndexes[textIndex];
                var sourceEnd = text.SourceIndexes[end - 1] + 1;
                captures[capture.Name] = text.Source[sourceStart..sourceEnd];
            }
            // A required capture standing for a player name is implausible at one or two glyphs;
            // without this, "ELIMINATED {p} MINE" matches every "ELIMINATED …" line by letting the
            // capture eat almost nothing and fuzzy-matching MINE against noise.
            var enoughCapture = capture.MinimumTokens == 0 || capturedChars >= 3;
            FindCompactMatches(templateTokens, text, maximumEditDistance, minimumScore, templateIndex + 1,
                end, literalScore, literalCount,
                capturesValid && tokenCount >= capture.MinimumTokens && enoughCapture, captures, onMatch);
        }
        captures.Remove(capture.Name);
    }

    private static CompactText Compact(string text)
    {
        var value = new StringBuilder(text.Length);
        var sourceIndexes = new List<int>(text.Length);
        var tokenIndexes = new List<int>(text.Length);
        var tokenIndex = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == ' ')
            {
                tokenIndex++;
                continue;
            }

            value.Append(text[index]);
            sourceIndexes.Add(index);
            tokenIndexes.Add(tokenIndex);
        }
        return new CompactText(text, value.ToString(), sourceIndexes.ToArray(), tokenIndexes.ToArray());
    }

    private static void FindMatches(IReadOnlyList<TemplateToken> templateTokens, string[] textTokens,
        int maximumEditDistance, int templateIndex, int textIndex, double literalScore,
        int literalCount, Dictionary<string, string> captures, Action<MatchState> onMatch)
    {
        if (templateIndex == templateTokens.Count)
        {
            if (literalCount > 0)
                onMatch(new MatchState(literalScore / literalCount,
                    new Dictionary<string, string>(captures, StringComparer.OrdinalIgnoreCase)));
            return;
        }

        if (textIndex >= textTokens.Length) return;

        if (templateTokens[templateIndex] is LiteralToken literal)
        {
            var budget = EffectiveBudget(maximumEditDistance, literal.Value.Length);
            var distance = LevenshteinDistance(literal.Value, textTokens[textIndex], budget);
            if (distance > budget) return;

            var score = 1d - (double)distance / Math.Max(literal.Value.Length, textTokens[textIndex].Length);
            FindMatches(templateTokens, textTokens, maximumEditDistance, templateIndex + 1,
                textIndex + 1, literalScore + score, literalCount + 1, captures, onMatch);
            return;
        }

        var capture = (CaptureToken)templateTokens[templateIndex];
        var remaining = textTokens.Length - textIndex;
        for (var count = capture.MinimumTokens; count <= Math.Min(capture.MaximumTokens, remaining); count++)
        {
            captures[capture.Name] = string.Join(' ', textTokens, textIndex, count);
            FindMatches(templateTokens, textTokens, maximumEditDistance, templateIndex + 1,
                textIndex + count, literalScore, literalCount, captures, onMatch);
        }
        captures.Remove(capture.Name);
    }

    private static IReadOnlyList<TemplateToken> Parse(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw new FormatException("An OCR pattern template is required.");
        if (template.Length > MaximumTemplateLength)
            throw new FormatException($"An OCR pattern template cannot exceed {MaximumTemplateLength} characters.");

        var tokens = new List<TemplateToken>();
        var literal = new StringBuilder();
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '{')
            {
                if (template[index] == '}') throw new FormatException("OCR pattern contains an unmatched '}''.");
                literal.Append(template[index]);
                continue;
            }

            AddLiteralTokens(tokens, literal.ToString());
            literal.Clear();
            var end = template.IndexOf('}', index + 1);
            if (end < 0) throw new FormatException("OCR pattern contains an unmatched '{'.");
            var specification = template[(index + 1)..end];
            tokens.Add(ParseCapture(specification));
            index = end;
        }
        AddLiteralTokens(tokens, literal.ToString());

        if (tokens.Count == 0) throw new FormatException("An OCR pattern template is required.");
        if (tokens.Count > MaximumTemplateTokens)
            throw new FormatException($"An OCR pattern cannot exceed {MaximumTemplateTokens} tokens.");
        return tokens;
    }

    private static void AddLiteralTokens(List<TemplateToken> tokens, string literal)
    {
        var normalized = OcrTextNormalizer.Normalize(literal);
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            tokens.Add(new LiteralToken(token));
    }

    private static CaptureToken ParseCapture(string specification)
    {
        var separator = specification.IndexOf(':');
        var name = separator < 0 ? specification : specification[..separator];
        if (name.Length == 0 || !char.IsLetter(name[0])
            || name.Any(character => !char.IsLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new FormatException($"OCR capture name '{name}' is invalid.");
        }

        if (separator < 0) return new CaptureToken(name, 1, 4);
        var range = specification[(separator + 1)..].Split("..", StringSplitOptions.None);
        if (range.Length != 2 || !int.TryParse(range[0], out var minimum)
            || !int.TryParse(range[1], out var maximum) || minimum < 1
            || maximum < minimum || maximum > MaximumCaptureTokens)
        {
            throw new FormatException(
                $"OCR capture '{name}' must use a range between 1 and {MaximumCaptureTokens} tokens.");
        }
        return new CaptureToken(name, minimum, maximum);
    }

    private static int LevenshteinDistance(string left, string right, int cutoff)
    {
        if (Math.Abs(left.Length - right.Length) > cutoff) return cutoff + 1;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++) previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            var rowMinimum = current[0];
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1), substitution);
                rowMinimum = Math.Min(rowMinimum, current[column]);
            }
            if (rowMinimum > cutoff) return cutoff + 1;
            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private abstract record TemplateToken;
    private sealed record LiteralToken(string Value) : TemplateToken;
    private sealed record CaptureToken(string Name, int MinimumTokens, int MaximumTokens) : TemplateToken;
    private sealed record MatchState(double Score, IReadOnlyDictionary<string, string> Captures);
    private sealed record CompactMatchState(MatchState Match, bool CapturesValid)
    {
        internal double Score => Match.Score;
    }
    private sealed record CompactText(string Source, string Value, int[] SourceIndexes, int[] TokenIndexes);
}
