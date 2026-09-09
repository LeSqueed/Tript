// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class OcrTokenTemplateMatcherTests
{
    [Fact]
    public void DirectPatternMatchesNormalizedText()
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch("  Kill\u2019cam! ",
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en",
                Template = "KILL'CAM",
                MaximumEditDistance = 0,
            },
        ]);

        Assert.NotNull(match);
        Assert.Equal("KILL'CAM", match.NormalizedText);
        Assert.Equal("en", match.LanguageTag);
    }

    [Fact]
    public void CaptureMatchesVariablePlayerName()
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch("Eliminated Some Long Player's Sentry Turret",
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en-US",
                Template = "ELIMINATED {owner:1..4} SENTRY TURRET",
                MaximumEditDistance = 0,
            },
        ]);

        Assert.NotNull(match);
        Assert.Equal("SOME LONG PLAYER'S", match.Captures["owner"]);
    }

    [Theory]
    [InlineData("RESPAWNIN", "RESPAWN IN")]
    [InlineData("PLAY OF THEGAME", "PLAY OF THE GAME")]
    public void LiteralPatternMatchesWhenOcrDropsSpaces(string text, string template)
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch(text,
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en",
                Template = template,
                MaximumEditDistance = 1,
                MinimumScore = 0.85,
            },
        ]);

        Assert.NotNull(match);
    }

    [Theory]
    [InlineData("ELIMINATED HELOISE'S STEELTRAP", "ELIMINATED {owner} STEEL TRAP", "HELOISE'S")]
    [InlineData("ELIMINATED OKASHISVENOMMINE", "ELIMINATED {ownerAndType} MINE", "OKASHISVENOM")]
    [InlineData("ELIMINATED OKASHIPROXIMITYMINE", "ELIMINATED {ownerAndType} MINE", "OKASHIPROXIMITY")]
    [InlineData("ELIMINATED OKASHI PROXIMITYMINE", "ELIMINATED {ownerAndType} MINE", "OKASHI PROXIMITY")]
    [InlineData("ELIMINATED OKASHIMINE", "ELIMINATED {ownerAndType} MINE", "OKASHI")]
    [InlineData("ELIMNATED OKASHISVENOMMINE", "ELIMINATED {ownerAndType} MINE", "OKASHISVENOM")]
    public void CapturePatternMatchesWhenOcrDropsSpaces(string text, string template, string expectedCapture)
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch(text,
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en",
                Template = template,
                MaximumEditDistance = 1,
                MinimumScore = 0.85,
            },
        ]);

        Assert.NotNull(match);
        Assert.Equal(expectedCapture, Assert.Single(match.Captures).Value);
    }

    // Real reads the sample harness produced from Overwatch kill-feed frames (installed events.json
    // uses maximumEditDistance:1 / minimumScore:0.85, so the relaxed path is what runs).
    [Theory]
    [InlineData("ELIMINATED AMON'S SENTRY TURRE", "ELIMINATED {playerAndType} TURRET")]
    [InlineData("ELMNATEWISTSENTRYTURRE", "ELIMINATED {playerAndType} TURRET")]
    [InlineData("ELMATE KULTOOHEALING PLON", "ELIMINATED {player} HEALING PYLON")]
    [InlineData("LMNATE AMONELPORTER PAD", "ELIMINATED {player} TELEPORTER PAD")]
    [InlineData("ELIMINATED SVAČINA'S IMMORTALITY FIELD", "ELIMINATED {player} IMMORTALITY FIELD")]
    public void MangledFeedLinesStillMatch(string text, string template)
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch(text,
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en-US",
                Template = template,
                MaximumEditDistance = 1,
                MinimumScore = 0.85,
            },
        ]);

        Assert.NotNull(match);
    }

    // The relaxed matcher must not let the common "ELIMINATED" prefix carry a weak tail: a turret
    // or "eliminated by" line is not a Mine.
    [Theory]
    [InlineData("ELIMINATED AMON'S SENTRY TURRET")]
    [InlineData("YOU WERE ELIMINATED BY ERIEK")]
    public void EliminatedPrefixAloneDoesNotMatchAWrongAbility(string text)
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch(text,
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en-US",
                Template = "ELIMINATED {playerAndType} MINE",
                MaximumEditDistance = 1,
                MinimumScore = 0.85,
            },
        ]);

        Assert.Null(match);
    }

    [Fact]
    public void CompactCaptureDoesNotConsumePartOfRequiredLiteral()
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch("ELIMINATED MINE",
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en",
                Template = "ELIMINATED {owner} MINE",
                MaximumEditDistance = 1,
                MinimumScore = 0.85,
            },
        ]);

        Assert.Null(match);
    }

    [Fact]
    public void BestTranslationWinsWithoutDoubleMatchingTheEvent()
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch("ELIMINIERT AMONS GESCHUETZ",
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en",
                Template = "ELIMINATED {owner} SENTRY TURRET",
                MaximumEditDistance = 0,
            },
            new OcrPatternDefinition
            {
                LanguageTag = "de",
                Template = "ELIMINIERT {owner} GESCHUETZ",
                MaximumEditDistance = 0,
            },
        ]);

        Assert.NotNull(match);
        Assert.Equal("de", match.LanguageTag);
        Assert.Equal(1, match.Score);
    }

    [Fact]
    public void LiteralTokensAllowBoundedOcrErrors()
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch("ELIMINATFD AMON SENTRY TURRET",
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en",
                Template = "ELIMINATED {owner} SENTRY TURRET",
                MaximumEditDistance = 1,
                MinimumScore = 0.95,
            },
        ]);

        Assert.NotNull(match);
    }

    [Fact]
    public void PatternBelowMinimumScoreDoesNotMatch()
    {
        var match = OcrTokenTemplateMatcher.FindBestMatch("ELIMINATXX AMON SENTRY TURRET",
        [
            new OcrPatternDefinition
            {
                LanguageTag = "en",
                Template = "ELIMINATED {owner} SENTRY TURRET",
                MaximumEditDistance = 2,
                MinimumScore = 0.99,
            },
        ]);

        Assert.Null(match);
    }

    [Theory]
    [InlineData("{player}")]
    [InlineData("ELIMINATED {player:0..4}")]
    [InlineData("ELIMINATED {player:1..99}")]
    [InlineData("ELIMINATED {player")]
    public void InvalidOrUnboundedPatternsAreRejected(string template)
    {
        var pattern = new OcrPatternDefinition { LanguageTag = "en", Template = template };

        Assert.Throws<FormatException>(() => OcrTokenTemplateMatcher.Validate(pattern));
    }
}
