// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using Tript.Core;
using Tript.Media;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class HotkeyActionsTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsStore _store;
    private readonly RecordingSessionTracker _sessionTracker;
    private readonly AppHost _host;
    private readonly RecordingClipEngine _clipEngine;

    public HotkeyActionsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-hotkey-actions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        _store = new SettingsStore(new SettingsFileProvider(settingsPath));
        _sessionTracker = new RecordingSessionTracker();
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, _store, runtime: null, _sessionTracker, recorderStopTimeout: TimeSpan.FromMilliseconds(50));

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
    public void ToggleRecording_StartsThenStopsRecording()
    {
        _host.ToggleRecording();
        Assert.True(_host.IsRecording);

        _host.ToggleRecording();
        Assert.False(_host.IsRecording);
    }

    [Fact]
    public void AddLiveBookmark_WhenNotRecording_DoesNothing()
    {
        _host.AddLiveBookmark();

        Assert.Null(_sessionTracker.Active);
    }

    [Fact]
    public void AddLiveBookmark_InSessionMode_AddsBookmarkToTheLiveSession()
    {
        _store.Load().Recording.Mode = RecordingMode.Session;
        _store.Save();
        Assert.True(_host.StartRecording("Overwatch"));

        _host.AddLiveBookmark();

        var bookmark = Assert.Single(_sessionTracker.Active!.Bookmarks);
        Assert.Equal(BookmarkType.Manual, bookmark.Type);
    }

    [Fact]
    public void AddLiveBookmark_InReplayBufferOnlyMode_DoesNotAddABookmark()
    {
        _store.Load().Recording.Mode = RecordingMode.ReplayBufferOnly;
        _store.Save();
        Assert.True(_host.StartRecording(gameId: null));

        _host.AddLiveBookmark();

        Assert.Empty(_sessionTracker.Active!.Bookmarks);
    }

    [Fact]
    public void CreateQuickClipFromBuffer_WhenNotRecording_DoesNotCreateAClip()
    {
        _host.CreateQuickClipFromBuffer();

        Assert.Equal(0, _clipEngine.CreateClipsCalls);
    }

    [Fact]
    public void CreateQuickClipFromBuffer_InPlainSessionMode_DoesNotCreateAClip()
    {
        _store.Load().Recording.Mode = RecordingMode.Session;
        _store.Save();
        Assert.True(_host.StartRecording("Overwatch"));

        _host.CreateQuickClipFromBuffer();

        Assert.Equal(0, _clipEngine.CreateClipsCalls);
    }

    [Fact]
    public void CreateQuickClipFromBuffer_WithAReplayBuffer_CreatesAClip()
    {
        _store.Load().Recording.Mode = RecordingMode.SessionWithReplayBuffer;
        _store.Save();
        Assert.True(_host.StartRecording("Overwatch"));
        Thread.Sleep(5);

        _host.CreateQuickClipFromBuffer();

        Assert.True(SpinWait.SpinUntil(() => _clipEngine.CreateClipsCalls > 0, TimeSpan.FromSeconds(5)));
    }

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
