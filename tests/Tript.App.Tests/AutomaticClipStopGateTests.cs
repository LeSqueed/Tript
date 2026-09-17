// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using Tript.Core;
using Tript.Media;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class AutomaticClipStopGateTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsStore _store;
    private readonly AppHost _host;
    private readonly RecordingClipEngine _clipEngine;

    public AutomaticClipStopGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-automatic-clip-stop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");

        _store = new SettingsStore(new SettingsFileProvider(settingsPath));
        _store.Load().Recording.AutomaticClipsEnabled = true;
        _store.Save();

        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, _store, runtime: null, new RecordingSessionTracker(),
            recorderStopTimeout: TimeSpan.FromMilliseconds(50));

        _clipEngine = new RecordingClipEngine();
        typeof(AppHost).GetField("_clipEngine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_host, _clipEngine);
    }

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void PlainSessionWithAutomaticClipsEnabled_CutsNoAutomaticClipsOnStop()
    {
        RecordAndStopInMode(RecordingMode.Session);

        Assert.Equal(0, _clipEngine.CreateClipsCalls);
    }

    [Fact]
    public void ReplayBufferSessionWithAutomaticClipsEnabled_CutsAutomaticClipsOnStop()
    {
        RecordAndStopInMode(RecordingMode.SessionWithReplayBuffer);

        Assert.True(_clipEngine.CreateClipsCalls > 0);
        Assert.False(SessionStartGate());
    }

    [Fact]
    public void ReplayBufferOnly_CreatesNoSessionOutputOrFallbackClips()
    {
        _store.Load().Recording.Mode = RecordingMode.ReplayBufferOnly;
        _store.Save();

        Assert.True(_host.StartRecording(gameId: null));
        Assert.Null(typeof(AppHost).GetField("_activeOutputPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_host));
        Assert.NotNull(typeof(AppHost).GetField("_activeSessionPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_host));

        LiveHighlights().AddCandidate(new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(10) });

        Assert.True(_host.StopRecording());
        Assert.Equal(0, _clipEngine.CreateClipsCalls);
        Assert.False(Directory.Exists(Path.Combine(_root, "sessions")));
    }

    [Fact]
    public void ReplayBufferOnly_LiveHighlightBelongsToAHighlightsOnlySession()
    {
        _store.Load().Recording.Mode = RecordingMode.ReplayBufferOnly;
        _store.Save();
        Assert.True(_host.StartRecording(gameId: null));

        typeof(AppHost).GetMethod("RememberAutomaticClipBookmark",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_host,
            [new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(10) }]);

        Assert.True(_host.StopRecording());

        var clipTitles = (Tript.App.Content.ClipTitleStore)typeof(AppHost)
            .GetField("_clipTitles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host)!;
        var record = Assert.Single(clipTitles.EnumerateRecords()).Record;
        Assert.True(record.IsAutomatic);
        Assert.True(record.SourceSessionHighlightsOnly);
        Assert.Contains("sessions/", record.SourceSessionPath, StringComparison.Ordinal);
        Assert.Equal(1, _clipEngine.CreateClipsCalls);
    }

    [Fact]
    public void FailedRecordingAttempt_ClearsAStaleSessionStartGate()
    {
        LiveHighlights().Begin(DateTime.UtcNow, enabled: true);
        Assert.True(SessionStartGate());
        _store.Load().Capture.Method = DisplayCaptureMethod.Game;
        _store.Save();

        Assert.False(_host.StartRecording(gameId: null));
        Assert.False(SessionStartGate());
    }

    private void RecordAndStopInMode(RecordingMode mode)
    {
        _store.Load().Recording.Mode = mode;
        _store.Save();

        Assert.True(_host.StartRecording("Overwatch"));

        var outputPath = (string)typeof(AppHost).GetField("_activeOutputPath",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host)!;
        File.WriteAllText(outputPath, string.Empty);

        LiveHighlights().AddCandidate(new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(10) });

        Assert.True(_host.StopRecording());

        var jobField = typeof(AppHost).GetField("_automaticClipJob",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(SpinWait.SpinUntil(() => jobField.GetValue(_host) is null, TimeSpan.FromSeconds(5)));
    }

    private bool SessionStartGate() => LiveHighlights().EnabledAtSessionStart;

    private LiveHighlightTracker LiveHighlights() =>
        (LiveHighlightTracker)typeof(AppHost).GetField("_liveHighlights",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host)!;

    private sealed class RecordingClipEngine : IClipEngine
    {
        private int _createClipsCalls;

        internal int CreateClipsCalls => Volatile.Read(ref _createClipsCalls);

        public IReadOnlyList<string> CreateClips(ClipRequest request)
        {
            Interlocked.Increment(ref _createClipsCalls);
            var outputPath = request.OutputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, string.Empty);
            return [outputPath];
        }
    }
}
