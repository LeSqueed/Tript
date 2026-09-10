// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Concurrent;

namespace Tript.Obs.IntegrationTests;

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
        ObsTestEnvironment.RequireUsableRuntime();

        if (ObsRuntime.Current is not null || ObsRuntime.IsInitialized)
            throw new InvalidOperationException(
                "An OBS context was still running when this test began. A previous test did not dispose its session.");

        var session = new ObsSession();

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

    internal bool AnyMessageContains(string fragment) =>
        _messages.Any(entry => entry.Message.Contains(fragment, StringComparison.Ordinal));

    private void Enqueue(ObsLogLevel level, string message) => _messages.Enqueue((level, message));
}
