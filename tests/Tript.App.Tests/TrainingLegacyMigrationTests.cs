#if TRIPT_TRAINING

using Tript.App.Training;
using Xunit;

namespace Tript.App.Tests;

public sealed class TrainingLegacyMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-training-migration-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Migration_PreservesDestinationCollisionsAndMovesLegacySamplesAndModel()
    {
        var trainingRoot = Path.Combine(_root, "training");
        var modelsRoot = Path.Combine(_root, "models");
        var cataloguePath = Path.Combine(_root, "games.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(cataloguePath,
            """[{"gameId":"canonical-id","executable":"game.exe","legacyGameIds":["Legacy"]}]""");
        var legacy = TrainingWorkspace.ForGame("Legacy", trainingRoot);
        var canonical = TrainingWorkspace.ForGame("canonical-id", trainingRoot);
        legacy.EnsureDirectories();
        canonical.EnsureDirectories();
        File.WriteAllText(legacy.EventsPath, "legacy-events");
        File.WriteAllText(canonical.EventsPath, "canonical-events");
        File.WriteAllText(Path.Combine(legacy.SamplesPath, "sample.json"), "sample");
        var legacyModel = TrainingWorkspace.ForGame("Legacy", modelsRoot);
        Directory.CreateDirectory(legacyModel.RootPath);
        File.WriteAllText(legacyModel.ModelPath, "model");

        AppHost.MigrateLegacyTrainingFolders(GameCatalog.Load(cataloguePath), trainingRoot, modelsRoot);

        Assert.Equal("canonical-events", File.ReadAllText(canonical.EventsPath));
        Assert.Equal("legacy-events", File.ReadAllText(legacy.EventsPath));
        Assert.Equal("sample", File.ReadAllText(Path.Combine(canonical.SamplesPath, "sample.json")));
        Assert.True(File.Exists(TrainingWorkspace.ForGame("canonical-id", modelsRoot).ModelPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

#endif
