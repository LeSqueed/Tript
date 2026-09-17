// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Detection;

internal static class OcrFramePass
{
    internal static List<OcrMatch> Run(PaddleOcrRecognizer recognizer,
        IReadOnlyList<OcrRegionPlan> regionPlans, byte[] frame, int fW, int fH, CancellationToken token)
    {
        var matches = new List<OcrMatch>();
        if (fW <= 0 || fH <= 0) return matches;

        foreach (var plan in regionPlans)
        {
            token.ThrowIfCancellationRequested();
            if (!DetectionFramePreprocessor.TryGetCropRect(plan.Region, fW, fH,
                    out var cropX, out var cropY, out var cropW, out var cropH))
                continue;

            var recognitions = recognizer.RecognizeAll(frame, fW, fH, cropX, cropY, cropW, cropH);
            foreach (var recognition in recognitions)
            foreach (var binding in plan.Bindings)
            {
                var ocr = binding.Definition.Ocr;
                if (ocr is null
                    || recognition.Confidence < OcrTokenTemplateMatcher.EffectiveMinimumConfidence(ocr.MinimumConfidence))
                    continue;
                var match = OcrTokenTemplateMatcher.FindBestMatch(recognition.Text, ocr.Patterns);
                if (match is null) continue;
                matches.Add(new OcrMatch
                {
                    EventId = binding.Definition.Id,
                    Text = recognition.Text,
                    NormalizedText = match.NormalizedText,
                    LanguageTag = match.LanguageTag,
                    SegmentId = binding.SegmentId,
                    Confidence = recognition.Confidence,
                    X = (float)recognition.X / fW,
                    Y = (float)recognition.Y / fH,
                    Width = (float)recognition.Width / fW,
                    Height = (float)recognition.Height / fH,
                });
            }
        }

        return matches;
    }

    internal static List<OcrMatch> FreshMatches(List<OcrMatch>? matches, DateTime completedAtUtc,
        DateTime nowUtc, int maxAgeMs) =>
        matches is not null && (nowUtc - completedAtUtc).Duration() <= TimeSpan.FromMilliseconds(maxAgeMs)
            ? matches
            : [];
}
