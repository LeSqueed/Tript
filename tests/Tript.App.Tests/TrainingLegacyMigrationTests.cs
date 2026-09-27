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
        Directory.CreateDirectory(_root);
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

        Tript.App.Training.TrainingWorkspaceMigration.Migrate(
            new Dictionary<string, string> { ["Legacy"] = "canonical-id" }, trainingRoot, modelsRoot);

        Assert.Equal("canonical-events", File.ReadAllText(canonical.EventsPath));
        Assert.Equal("legacy-events", File.ReadAllText(legacy.EventsPath));
        Assert.Equal("sample", File.ReadAllText(Path.Combine(canonical.SamplesPath, "sample.json")));
        Assert.True(File.Exists(TrainingWorkspace.ForGame("canonical-id", modelsRoot).ModelPath));
    }

    [Fact]
    public void Migration_LetsTheNewerLegacyWorkspaceWinOverTheOlderOne()
    {
        var trainingRoot = Path.Combine(_root, "training");
        var modelsRoot = Path.Combine(_root, "models");
        Directory.CreateDirectory(_root);
        var oldest = TrainingWorkspace.ForGame("Overwatch", trainingRoot);
        var newer = TrainingWorkspace.ForGame("57ZZVAZ0PJK8VQGPKB728QE57C", trainingRoot);
        oldest.EnsureDirectories();
        newer.EnsureDirectories();
        File.WriteAllText(oldest.EventsPath, "stale-events");
        File.WriteAllText(newer.EventsPath, "current-events");
        var oldestModel = TrainingWorkspace.ForGame("Overwatch", modelsRoot);
        var newerModel = TrainingWorkspace.ForGame("57ZZVAZ0PJK8VQGPKB728QE57C", modelsRoot);
        Directory.CreateDirectory(oldestModel.RootPath);
        Directory.CreateDirectory(newerModel.RootPath);
        File.WriteAllText(oldestModel.ModelPath, "stale-model");
        File.WriteAllText(newerModel.ModelPath, "current-model");

        TrainingWorkspaceMigration.Migrate(Tript.App.Models.LegacyGameIds.NewestFirst, trainingRoot, modelsRoot);

        var resolved = TrainingWorkspace.ForGame(Tript.App.Models.LegacyGameIds.Overwatch, trainingRoot);
        Assert.Equal("current-events", File.ReadAllText(resolved.EventsPath));
        Assert.Equal("current-model",
            File.ReadAllText(TrainingWorkspace.ForGame(Tript.App.Models.LegacyGameIds.Overwatch, modelsRoot).ModelPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

#endif
