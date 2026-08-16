// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;

namespace Tript.Obs.IntegrationTests;

// One started OBS context, torn down at the end of the test that made it. There is deliberately no
// shared runtime fixture: with a single global context, a fixture spanning tests makes every test
// depend on what ran before it, and the lifecycle tests need to own startup themselves. Paying a
// fresh startup per test buys total independence, and startup is around a tenth of a second.
internal sealed class ObsSession : IDisposable
{
    private readonly ConcurrentQueue<(ObsLogLevel Level, string Message)> _messages = new();
    private IDisposable? _logScope;
    private ObsRuntime? _runtime;
    private int _disposed;

    private ObsSession()
    {
    }

    internal ObsRuntime Runtime => _runtime ?? throw new InvalidOperationException("The session has no runtime.");

    internal IReadOnlyCollection<(ObsLogLevel Level, string Message)> Messages => _messages.ToArray();

    internal static ObsSession Start()
    {
        // A previous test that leaked a context would otherwise show up as an unrelated failure
        // somewhere later. Report it here, where the state is still attributable.
        if (ObsRuntime.Current is not null || ObsRuntime.IsInitialized)
            throw new InvalidOperationException(
                "An OBS context was still running when this test began. A previous test did not dispose its session.");

        var session = new ObsSession();

        // Installed before startup so the startup banner is captured too — it is the richest
        // formatted output libobs produces, and the log tests assert on it.
        var logScope = ObsLog.Install(session.Enqueue);

        try
        {
            session._runtime = ObsRuntime.Start(new ObsStartupOptions
            {
                Locale = "en-US",
                NixPlatform = ObsNixPlatform.X11Egl,
                NixPlatformDisplay = ObsTestEnvironment.XDisplay
            });
        }
        catch
        {
            logScope.Dispose();
            throw;
        }

        session._logScope = logScope;
        return session;
    }

    // A context in which source types exist: video and audio mixes reset, then the plugins that
    // register the sources these tests use. Sources and scenes can be created without a video mix at
    // all — measured — but nothing a capture source does is meaningful without one.
    internal static ObsSession StartWithSourceTypes()
    {
        var session = Start();

        try
        {
            session.ResetVideoOrThrow(new ObsVideoSettings
            {
                BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720
            });

            if (!session.Runtime.ResetAudio(new ObsAudioSettings()))
                throw new InvalidOperationException("obs_reset_audio refused the default settings.");

            var report = session.StartModules();
            if (!report.AllLoaded)
                throw new InvalidOperationException($"Modules failed to load: {string.Join(", ", report.FailedModules)}");
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return session;
    }

    // A context in which audio device capture sources exist: the base source types plus the
    // linux-pulseaudio module, which registers pulse_input_capture / pulse_output_capture — the
    // sources the multi-track routing actually routes into mixers. Creating one needs no PulseAudio
    // server to be reachable (the source exists regardless; it just captures silence), so this is
    // safe on a machine without audio.
    internal static ObsSession StartWithAudioSources()
    {
        var session = Start();

        try
        {
            session.ResetVideoOrThrow(new ObsVideoSettings
            {
                BaseWidth = 1280, BaseHeight = 720, OutputWidth = 1280, OutputHeight = 720
            });

            if (!session.Runtime.ResetAudio(new ObsAudioSettings()))
                throw new InvalidOperationException("obs_reset_audio refused the default settings.");

            foreach (var module in ObsTestEnvironment.AudioModules)
                session.Runtime.AddSafeModule(module);

            var report = session.StartModules();
            if (!report.AllLoaded)
                throw new InvalidOperationException($"Modules failed to load: {string.Join(", ", report.FailedModules)}");
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return session;
    }

    // The full ordering the headers imply: startup, paths, video and audio, modules, post-load.
    internal ObsModuleLoadReport StartModules()
    {
        foreach (var module in ObsTestEnvironment.SafeModules)
            Runtime.AddSafeModule(module);

        Runtime.AddModulePath($"{ObsTestEnvironment.PluginBinaryPath}/%module%.so", ObsTestEnvironment.PluginDataPath);
        var report = Runtime.LoadAllModules();
        Runtime.PostLoadModules();
        return report;
    }

    internal void ResetVideoOrThrow(ObsVideoSettings settings)
    {
        var result = Runtime.ResetVideo(settings);
        if (result != ObsVideoResetResult.Success)
            throw new InvalidOperationException($"obs_reset_video reported {result} for {settings.BaseWidth}x{settings.BaseHeight}.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _runtime?.Dispose();
        _logScope?.Dispose();
    }

    // The message queue is filled from libobs's own threads, so a test that asserts on it has to
    // ask after the call that produced it has returned.
    internal bool AnyMessageContains(string fragment) =>
        _messages.Any(entry => entry.Message.Contains(fragment, StringComparison.Ordinal));

    private void Enqueue(ObsLogLevel level, string message) => _messages.Enqueue((level, message));
}
