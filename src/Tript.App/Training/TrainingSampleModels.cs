// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if TRIPT_TRAINING

namespace Tript.App.Training;

internal sealed class TrainingLabel
{
    public int ClassId { get; set; }

    public double CenterX { get; set; }

    public double CenterY { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

internal sealed class TrainingOcrRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Text { get; set; } = string.Empty;
}

internal sealed class TrainingLabelSuggestion
{
    public TrainingLabel Label { get; init; } = new();

    public float Confidence { get; init; }
}

internal sealed class TrainingSampleRecord
{
    public string Id { get; set; } = string.Empty;

    public string ImageFile { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public double TimestampSeconds { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public List<TrainingLabel> Labels { get; set; } = [];

    public List<TrainingOcrRegion> OcrRegions { get; set; } = [];

    public string? DatasetImagePath { get; set; }
}

#endif
