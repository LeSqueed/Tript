// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Reflection;
using Tript.App.Content;
using Tript.Settings;
using Xunit;

namespace Tript.App.Tests;

public sealed class ShutdownFinalizationTests : IDisposable
{
    private readonly string _root;
    private readonly AppHost _host;

    public ShutdownFinalizationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-shutdown-finalize", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        _host = new AppHost(new AppOptions
        {
            ContentRoot = _root,
            SettingsPath = settingsPath,
            WebRoot = _root,
            FakeRecorder = true,
        }, new SettingsStore(new SettingsFileProvider(settingsPath)), runtime: null, new RecordingSessionTracker(),
            recorderStopTimeout: TimeSpan.FromMilliseconds(100),
            pendingStopFinalizeTimeout: TimeSpan.FromSeconds(5),
            storageProbe: AmpleStorage.Probe);
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

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    // Dispose used to set _disposed before calling StopRecording, and CompletePendingStop bailed on
    // _disposed, so an output that was slow to stop lost the session's .metadata.json and every
    // bookmark in it. Dispose now waits for the handed-off finalization instead.
    [Fact]
    public void DisposeDuringASlowStop_StillWritesTheSessionMetadata()
    {
        Assert.True(_host.StartRecording("Overwatch"));

        var outputPath = (string?)typeof(AppHost)
            .GetField("_activeOutputPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_host);
        Assert.NotNull(outputPath);

        // The fake output never creates a file, and WriteMetadataRecord skips a missing video.
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath!)!);
        File.WriteAllText(outputPath!, "fake video");

        var session = Field<FakeRecorderSession>(_host, "_recorderSession");
        var output = Field<FakeRecorderSession.FakeOutput>(session, "_output");
        output.CompleteStopSynchronously = false;

        // Fires after StopRecording's own WaitForIdle has already given up, so the finalization is
        // genuinely handed to CompletePendingStop while Dispose is running.
        var completer = new Thread(() =>
        {
            Thread.Sleep(400);
            session.CompleteStop();
        });
        completer.Start();

        _host.Dispose();
        completer.Join();

        var metadataPath = Path.Combine(ContentLayout.MetadataRoot(_root),
            Path.GetFileName(outputPath!) + ".metadata.json");
        Assert.True(File.Exists(metadataPath),
            $"the session metadata was not written to {metadataPath}");
    }
}
