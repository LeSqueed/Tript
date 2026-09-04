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

        // Both cases opt into automatic clips; only the mode differs, and that is what the gate keys on.
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

        // Drive the automatic-clip worker through a recording engine, so a queued job is observed as a
        // CreateClips call rather than an ffmpeg run against a file the fake recorder never produced.
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
    public void FailedRecordingAttempt_ClearsAStaleSessionStartGate()
    {
        var gate = typeof(AppHost).GetField("_liveHighlightsEnabledAtSessionStart",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        gate.SetValue(_host, true);
        _store.Load().Capture.Method = DisplayCaptureMethod.Game;
        _store.Save();

        // Game-only capture without a detected process is refused before the recorder starts.
        Assert.False(_host.StartRecording(gameId: null));
        Assert.False(SessionStartGate());
    }

    // Starts a recording in the given mode, leaves one unsaved bookmark in it, materializes the output
    // file the fake recorder never writes, and stops. The only variable is the mode, which decides the
    // start-of-session effective flag the post-stop gate keys on.
    private void RecordAndStopInMode(RecordingMode mode)
    {
        _store.Load().Recording.Mode = mode;
        _store.Save();

        Assert.True(_host.StartRecording("Overwatch"));

        // The post-stop gate cuts clips only for a recording that exists on disk, so materialize the
        // output path: that leaves the start-of-session flag as the only thing under test.
        var outputPath = (string)typeof(AppHost).GetField("_activeOutputPath",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host)!;
        File.WriteAllText(outputPath, string.Empty);

        // A bookmark recorded during the session but not already saved as a live highlight.
        var bookmarks = (List<Bookmark>)typeof(AppHost).GetField("_automaticClipBookmarks",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host)!;
        bookmarks.Add(new Bookmark { Type = BookmarkType.Kill, Time = TimeSpan.FromSeconds(10) });

        Assert.True(_host.StopRecording());

        // The automatic-clip worker runs on the thread pool; wait for it to finish so the CreateClips
        // count is final rather than a race against the job tearing itself down.
        var jobField = typeof(AppHost).GetField("_automaticClipJob",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(SpinWait.SpinUntil(() => jobField.GetValue(_host) is null, TimeSpan.FromSeconds(5)));
    }

    private bool SessionStartGate() =>
        (bool)typeof(AppHost).GetField("_liveHighlightsEnabledAtSessionStart",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host)!;

    // Records how many times the automatic-clip worker asked to cut, which is set synchronously when the
    // job is queued and therefore reflects the gate's decision, not ffmpeg's success.
    private sealed class RecordingClipEngine : IClipEngine
    {
        private int _createClipsCalls;

        internal int CreateClipsCalls => Volatile.Read(ref _createClipsCalls);

        public IReadOnlyList<string> CreateClips(ClipRequest request)
        {
            Interlocked.Increment(ref _createClipsCalls);
            return [];
        }
    }
}
