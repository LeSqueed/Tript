// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Recorder;
using Tript.Settings;
using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsRecorderSceneCompositionTests
{
    private const string ColourSourceId = "color_source";
    private const int CanvasWidth = 1280;
    private const int CanvasHeight = 720;

    [SkippableFact]
    public void TheRecordingScene_LayersTheDisplayCaptureOverTheColourBackground()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var colour = ObsSource.CreatePrivate(ColourSourceId, "composition colour");
        using var recorderSession = new ObsRecorderSession(session.Runtime, colour);

        Assert.True(recorderSession.HasDisplayFallback, "linux-capture is loaded, so a display layer must exist.");

        var layers = ReadLayerIds(session, recorderSession);

        Assert.Equal(ColourSourceId, layers[0]);
        Assert.Equal(ObsCaptureSource.FindDisplayCaptureId(), layers[1]);
    }

    [SkippableFact]
    public void EveryLayer_IsFittedToTheCanvasOnceTheOutputIsBuilt()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var colour = ObsSource.CreatePrivate(ColourSourceId, "bounds colour");
        using var recorderSession = new ObsRecorderSession(session.Runtime, colour);

        using var output = recorderSession.CreateOutput(RecorderSettings());

        recorderSession.PlaceSourceOnChannel();
        try
        {
            foreach (var item in EnumerateSceneItems(session))
            {
                using (item)
                {
                    Assert.Equal(ObsBoundsType.ScaleInner, item.BoundsType);
                    Assert.Equal(CanvasWidth, item.Bounds.X);
                    Assert.Equal(CanvasHeight, item.Bounds.Y);
                    Assert.Equal(0f, item.Position.X);
                    Assert.Equal(0f, item.Position.Y);
                }
            }
        }
        finally
        {
            recorderSession.ClearSourceFromChannel();
        }
    }

    [SkippableFact]
    public void WithoutAGameCaptureSource_TheHookStateIsFalseRatherThanUnknown()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var colour = ObsSource.CreatePrivate(ColourSourceId, "hook colour");
        using var recorderSession = new ObsRecorderSession(session.Runtime, colour);

        Assert.False(recorderSession.IsGameCaptureHooked);

        recorderSession.PlaceSourceOnChannel();
        Assert.False(recorderSession.IsGameCaptureHooked);
        recorderSession.ClearSourceFromChannel();
    }

    private static IReadOnlyList<string> ReadLayerIds(ObsSession session, ObsRecorderSession recorderSession)
    {
        recorderSession.PlaceSourceOnChannel();
        try
        {
            var ids = new List<string>();
            foreach (var item in EnumerateSceneItems(session))
            {
                using (item)
                using (var source = item.GetSource())
                {
                    ids.Add(source?.Id ?? string.Empty);
                }
            }

            return ids;
        }
        finally
        {
            recorderSession.ClearSourceFromChannel();
        }
    }

    private static IReadOnlyList<ObsSceneItem> EnumerateSceneItems(ObsSession session)
    {
        using var placed = session.Runtime.GetOutputSource(0);
        Assert.NotNull(placed);

        using var scene = ObsScene.FromSource(placed);
        Assert.NotNull(scene);

        return scene.EnumerateItems();
    }

    private static ResolvedRecorderSettings RecorderSettings() => new()
    {
        Mode = RecordingMode.Session,
        OutputPath = Path.Combine(Path.GetTempPath(), $"tript-composition-{Guid.NewGuid():N}.mp4"),
        ResolutionWidth = CanvasWidth,
        ResolutionHeight = CanvasHeight,
        Fps = 30,
        Encoder = "x264",
        Quality = 12
    };
}
