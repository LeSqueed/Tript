// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Core;

namespace Tript.Settings;

public sealed class SettingsStore
{
    private readonly object _gate = new();

    private Settings? _settings;

    public string FilePath { get; }

    private readonly SettingsFileProvider _provider;

    public SettingsStore()
        : this(new SettingsFileProvider(SettingsFilePaths.SettingsPath))
    {
    }

    public SettingsStore(SettingsFileProvider provider)
    {
        _provider = provider;
        FilePath = provider.FilePath;
    }

    public Settings Load()
    {
        lock (_gate)
        {
            if (_settings is not null)
                return _settings;

            var json = _provider.ReadJson();
            _settings = json is null ? new Settings() : DeserializeOrRecover(json);
            return _settings;
        }
    }

    // Where the unreadable settings file was moved, when Load had to fall back to defaults.
    public string? RecoveredFrom { get; private set; }

    // A settings.json that fails to parse used to throw straight out of Load, which runs during
    // startup, so Tript refused to start at all. Falling back to defaults keeps it usable, and moving
    // the bad file aside first means the next Save cannot overwrite the user's only copy.
    private Settings DeserializeOrRecover(string json)
    {
        try
        {
            return SettingsSerialization.Deserialize(json) ?? new Settings();
        }
        catch (JsonException exception)
        {
            RecoveredFrom = PreserveUnreadableFile();
            Diagnostics.Report(DiagnosticLevel.Error,
                $"{Path.GetFileName(FilePath)} could not be read and settings were reset to defaults; "
                + $"the original was kept at {RecoveredFrom ?? "its original path (it could not be moved)"}",
                exception);
            return new Settings();
        }
    }

    private string? PreserveUnreadableFile()
    {
        try
        {
            var backup = $"{FilePath}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(FilePath, backup);
            return backup;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public SettingsPageHandle<TPage> Page<TPage>(SettingsPage page)
        where TPage : class
    {
        return new SettingsPageHandle<TPage>(this, page, GetPage);
    }

    public void Save()
    {
        lock (_gate)
        {
            _provider.WriteRaw(SettingsSerialization.Serialize(_settings ?? new Settings()));
        }
    }

    public bool TryUpdate(Func<Settings, string?> update, out Settings settings, out string? error)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            var current = Load();
            var candidate = SettingsSerialization.Deserialize(SettingsSerialization.Serialize(current))
                ?? new Settings();

            try
            {
                error = update(candidate);
                if (error is not null)
                {
                    settings = current;
                    return false;
                }

                _provider.WriteRaw(SettingsSerialization.Serialize(candidate));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                settings = current;
                error = exception.Message;
                return false;
            }

            _settings = candidate;
            settings = candidate;
            error = null;
            return true;
        }
    }

    private static object GetPage(Settings settings, SettingsPage page) => page switch
    {
        SettingsPage.Recording => settings.Recording,
        SettingsPage.Buffer => settings.Buffer,
        SettingsPage.Audio => settings.Audio,
        SettingsPage.Capture => settings.Capture,
        SettingsPage.Game => settings.Game,
        SettingsPage.General => settings.General,
        SettingsPage.Hotkeys => settings.Hotkeys,
        SettingsPage.Streaming => settings.Streaming,
        SettingsPage.Storage => settings.Storage,
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
    };
}

public sealed class SettingsPageHandle<TPage> where TPage : class
{
    private readonly SettingsStore _store;

    private readonly SettingsPage _page;

    private readonly Func<Settings, SettingsPage, object> _getPage;

    internal SettingsPageHandle(SettingsStore store, SettingsPage page,
        Func<Settings, SettingsPage, object> getPage)
    {
        _store = store;
        _page = page;
        _getPage = getPage;
    }

    public SettingsPage Page => _page;

    public TPage Load() => (TPage)_getPage(_store.Load(), _page);

    public void Save() => _store.Save();
}
