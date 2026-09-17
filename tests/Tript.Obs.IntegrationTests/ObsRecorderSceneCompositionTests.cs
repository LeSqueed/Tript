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
    public void SessionAndReplayBuffer_ShareOneEncoder_AndBothWriteAFile()
    {
        using var session = ObsSession.StartWithSourceTypes();
        using var colour = ObsSource.CreatePrivate(ColourSourceId, "shared encoder colour");
        using var recorderSession = new ObsRecorderSession(session.Runtime, colour);

        var directory = Path.Combine(Path.GetTempPath(), $"tript-shared-encoder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = RecorderSettings(RecordingMode.SessionWithReplayBuffer,
                Path.Combine(directory, "session.mp4"));

            recorderSession.PlaceSourceOnChannel();
            string? replayPath = null;
            using (var output = recorderSession.CreateOutput(settings))
            {
                Skip.IfNot(output.Start(), $"The output did not start: {output.LastError}");
                Thread.Sleep(TimeSpan.FromSeconds(3));

                var replayOutput = Assert.IsAssignableFrom<IReplayBufferOutput>(output);
                Assert.True(replayOutput.SaveReplay(directory, "replay-%hh-%mm-%ss", path => replayPath = path));
                Assert.True(replayOutput.WaitForReplaySave(TimeSpan.FromSeconds(15)));

                Thread.Sleep(TimeSpan.FromSeconds(1));
                output.Stop();
                Assert.True(output.WaitForStop(TimeSpan.FromSeconds(15)));
            }

            recorderSession.ClearSourceFromChannel();

            Assert.True(new FileInfo(settings.OutputPath).Length > 0, "the session file is empty");
            Assert.NotNull(replayPath);
            Assert.True(new FileInfo(replayPath).Length > 0, "the replay file is empty");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
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

    private static ResolvedRecorderSettings RecorderSettings(RecordingMode mode = RecordingMode.Session,
        string? outputPath = null) => new()
    {
        Mode = mode,
        OutputPath = outputPath ?? Path.Combine(Path.GetTempPath(), $"tript-composition-{Guid.NewGuid():N}.mp4"),
        ResolutionWidth = CanvasWidth,
        ResolutionHeight = CanvasHeight,
        Fps = 30,
        Encoder = "x264",
        Quality = 12
    };
}
