// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public sealed class ModelRootConfigurationTests : IDisposable
{
    private readonly string _userRoot = Path.Combine(Path.GetTempPath(), "tript-model-roots-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _baseGames = [];

    public ModelRootConfigurationTests()
    {
        Directory.CreateDirectory(_userRoot);
        ModelService.ConfigureUserModelRoot(_userRoot);
    }

    [Fact]
    public void UserRootTakesPrecedenceWithCaseInsensitiveLookup()
    {
        var gameId = "RootTest" + Guid.NewGuid().ToString("N");
        var baseGame = CreateBundle(ModelService.BasePath, gameId, "base");
        _baseGames.Add(baseGame);
        var userGame = CreateBundle(_userRoot, gameId.ToUpperInvariant(), "user");

        Assert.Equal(userGame, ModelService.GetGamePath(gameId.ToLowerInvariant()));
        Assert.Equal(Path.Combine(userGame, "model.onnx"), ModelService.GetModelPath(gameId));
    }

    [Fact]
    public void IncompleteUserBundleDoesNotShadowCompleteBaseBundle()
    {
        var gameId = "PartialRootTest" + Guid.NewGuid().ToString("N");
        var baseGame = CreateBundle(ModelService.BasePath, gameId, "base");
        _baseGames.Add(baseGame);
        var userGame = Path.Combine(_userRoot, gameId.ToUpperInvariant());
        Directory.CreateDirectory(userGame);
        File.WriteAllText(Path.Combine(userGame, "events.json"), "[]");

        Assert.Equal(baseGame, ModelService.GetGamePath(gameId.ToLowerInvariant()));
        Assert.True(ModelService.HasModelForGame(gameId));
    }

    [Fact]
    public void RejectedUserBundleFallsBackToCompleteBaseBundle()
    {
        var gameId = "RejectedRootTest" + Guid.NewGuid().ToString("N");
        var baseGame = CreateBundle(ModelService.BasePath, gameId, "base");
        _baseGames.Add(baseGame);
        var userGame = CreateBundle(_userRoot, gameId, "user");
        Assert.Equal(userGame, ModelService.GetGamePath(gameId));

        Assert.True(ModelService.RejectCurrentBundle(gameId, out var rejected));

        Assert.Equal(userGame, rejected);
        Assert.Equal(baseGame, ModelService.GetGamePath(gameId));

        ModelService.InvalidateModel(gameId);
        Assert.Equal(userGame, ModelService.GetGamePath(gameId));
    }

    [Fact]
    public void ModelWithoutEventsIsNotAnAvailableBundle()
    {
        var gameId = "IncompleteOnly" + Guid.NewGuid().ToString("N");
        var gamePath = Path.Combine(_userRoot, gameId);
        Directory.CreateDirectory(gamePath);
        File.WriteAllBytes(Path.Combine(gamePath, "model.onnx"), [0]);

        Assert.False(ModelService.HasModelForGame(gameId));
    }

    [Fact]
    public void InvalidateModelClearsDefinitionsRegardlessOfGameIdCasing()
    {
        var gameId = "InvalidateTest" + Guid.NewGuid().ToString("N");
        var gamePath = CreateBundle(_userRoot, gameId, "first");

        Assert.Equal("first", Assert.Single(ModelService.LoadEventDefinitions(gameId)).Name);
        File.WriteAllText(Path.Combine(gamePath, "events.json"), EventsJson("second"));

        Assert.Equal("first", Assert.Single(ModelService.LoadEventDefinitions(gameId)).Name);
        ModelService.InvalidateModel(gameId.ToUpperInvariant());
        Assert.Equal("second", Assert.Single(ModelService.LoadEventDefinitions(gameId)).Name);
    }

    public void Dispose()
    {
        ModelService.ConfigureUserModelRoot(ModelService.BasePath);
        Directory.Delete(_userRoot, recursive: true);
        foreach (var game in _baseGames)
            Directory.Delete(game, recursive: true);
    }

    private static string CreateBundle(string root, string gameId, string eventName)
    {
        var gamePath = Path.Combine(root, gameId);
        Directory.CreateDirectory(gamePath);
        File.WriteAllBytes(Path.Combine(gamePath, "model.onnx"), [0]);
        File.WriteAllText(Path.Combine(gamePath, "events.json"), EventsJson(eventName));
        return gamePath;
    }

    private static string EventsJson(string name)
        => $"[{{\"id\":0,\"classId\":0,\"name\":\"{name}\",\"type\":0}}]";
}
