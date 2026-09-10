// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

[Collection(ModelSessionCollection.Name)]
public class ModelSessionLifetimeTests
{
    private const string GameId = "57ZZVAZ0PJK8VQGPKB728QE57C";
    private const int ModelInput = 640;

    private static void AssertModelIsOnDisk()
    {
        var modelPath = ModelService.GetModelPath(GameId);
        Assert.True(File.Exists(modelPath),
            $"ONNX model not found at {modelPath}. This test guards native session lifetime and " +
            "cannot be verified without the real model. It must fail, not skip.");
    }

    private static void AssertStillUsable(InferenceSession session)
    {
        var buffer = new float[ModelInput * ModelInput * 3];
        var tensor = new DenseTensor<float>(buffer.AsMemory(), new[] { 1, 3, ModelInput, ModelInput });
        var container = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor)
        };
        using var runOptions = new RunOptions();
        using var results = session.Run(container, session.OutputMetadata.Keys.ToList(), runOptions);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void UnloadingOneUser_LeavesTheSessionUsableForTheOther()
    {
        AssertModelIsOnDisk();

        var baseline = ModelService.GetSessionRefCount(GameId);
        var first = ModelService.LoadModel(GameId);
        var second = ModelService.LoadModel(GameId);

        try
        {
            Assert.Same(first, second);
            Assert.Equal(baseline + 2, ModelService.GetSessionRefCount(GameId));

            ModelService.UnloadModel(GameId);
            Assert.Equal(baseline + 1, ModelService.GetSessionRefCount(GameId));
            AssertStillUsable(second);
        }
        finally
        {
            ModelService.UnloadModel(GameId);
        }

        Assert.Equal(baseline, ModelService.GetSessionRefCount(GameId));
    }

    [Fact]
    public void ConcurrentLoads_ShareOneSession_AndEachTakesAReference()
    {
        AssertModelIsOnDisk();

        const int callers = 8;
        var baseline = ModelService.GetSessionRefCount(GameId);
        var sessions = new InferenceSession?[callers];
        var failures = new Exception?[callers];
        var released = 0;

        using (var gate = new ManualResetEventSlim())
        {
            var threads = new Thread[callers];
            for (int i = 0; i < callers; i++)
            {
                var index = i;
                threads[index] = new Thread(() =>
                {
                    try
                    {
                        gate.Wait();
                        sessions[index] = ModelService.LoadModel(GameId);
                    }
                    catch (Exception ex)
                    {
                        failures[index] = ex;
                    }
                })
                { IsBackground = true, Name = $"ModelSessionLifetimeTests.loader{index}" };
                threads[index].Start();
            }

            gate.Set();

            foreach (var thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "a concurrent LoadModel never returned");
        }

        try
        {
            Assert.All(failures, Assert.Null);
            Assert.All(sessions, s => Assert.NotNull(s));
            Assert.All(sessions, s => Assert.Same(sessions[0], s));
            Assert.Equal(baseline + callers, ModelService.GetSessionRefCount(GameId));

            for (; released < callers - 1; released++)
            {
                ModelService.UnloadModel(GameId);
                Assert.Equal(baseline + callers - released - 1, ModelService.GetSessionRefCount(GameId));
            }

            AssertStillUsable(sessions[0]!);
        }
        finally
        {
            for (; released < callers; released++)
                ModelService.UnloadModel(GameId);
        }

        Assert.Equal(baseline, ModelService.GetSessionRefCount(GameId));
    }

    [Fact]
    public void MixedCaseLoads_ShareOneSession()
    {
        AssertModelIsOnDisk();

        var alternateSpelling = GameId.ToLowerInvariant();
        var baseline = ModelService.GetSessionRefCount(GameId);
        var first = ModelService.LoadModel(GameId);
        var second = ModelService.LoadModel(alternateSpelling);

        try
        {
            Assert.Same(first, second);
            Assert.Equal(baseline + 2, ModelService.GetSessionRefCount(alternateSpelling));
        }
        finally
        {
            ModelService.UnloadModel(alternateSpelling);
            ModelService.UnloadModel(GameId);
        }

        Assert.Equal(baseline, ModelService.GetSessionRefCount(GameId));
    }

    [Fact]
    public void FailedLoad_LeavesNoReferenceAndNoPoisonedEntry()
    {
        const string missing = "not-a-real-game-for-session-lifetime";

        var first = Assert.Throws<FileNotFoundException>(() => ModelService.LoadModel(missing));
        Assert.Equal(0, ModelService.GetSessionRefCount(missing));

        var second = Assert.Throws<FileNotFoundException>(() => ModelService.LoadModel(missing));
        Assert.NotSame(first, second);
        Assert.Equal(0, ModelService.GetSessionRefCount(missing));
    }

    [Fact]
    public void UnloadWithoutLoad_IsInert()
    {
        const string missing = "not-a-real-game-never-loaded";

        ModelService.UnloadModel(missing);
        ModelService.UnloadModel(missing);

        Assert.Equal(0, ModelService.GetSessionRefCount(missing));
    }
}
