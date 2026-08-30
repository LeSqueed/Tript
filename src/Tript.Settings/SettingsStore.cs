// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// The settings service: loads and saves the settings model as JSON at the platform config
// directory, and hands out page-scoped read/write handles so the settings UI edits one logical page
// at a time. Round-trip preservation of unknown keys is the forward-compatibility contract: a build
// that models only part of the settings surface must neither lose fields it does not model when it
// saves, nor choke on them when it loads.
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

    // The typed model. Loading is lazy and read-once: the first call reads the file (or the
    // defaults if it does not exist), and the model stays live in memory until Save is called.
    public Settings Load()
    {
        lock (_gate)
        {
            if (_settings is not null)
                return _settings;

            var json = _provider.ReadJson();
            _settings = json is null
                ? new Settings()
                : SettingsSerialization.Deserialize(json) ?? new Settings();
            return _settings;
        }
    }

    // A handle to one logical page of the model. Loading the handle materializes (but does not
    // persist) the model and returns the page's typed object for editing; Save persists the whole
    // model, so a page edit never destroys a field from a page this build does not model.
    public SettingsPageHandle<TPage> Page<TPage>(SettingsPage page)
        where TPage : class
    {
        return new SettingsPageHandle<TPage>(this, page, GetPage);
    }

    // Persists the model back to disk. Saving before the model was ever loaded writes the
    // defaults — a settings file is not a thing that should be absent just because the user
    // never edited a setting.
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
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
    };
}

// A read/write handle to one logical page of the settings model. The settings UI loads a page,
// edits its properties, and saves it; the store serializes the whole model on save.
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

    // The page's typed sub-object, materialized from the live model.
    public TPage Load() => (TPage)_getPage(_store.Load(), _page);

    public void Save() => _store.Save();
}
