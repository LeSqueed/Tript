// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Models;
using Xunit;

namespace Tript.App.Tests;

public sealed class GameModelInstallerRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-installer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string StageModel(string name)
    {
        var staged = Path.Combine(_root, ".install-" + name);
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "model.onnx"), "weights");
        File.WriteAllText(Path.Combine(staged, "events.json"), "[]");
        return staged;
    }

    [Fact]
    public void AScannerHoldingTheFreshModelOpen_DelaysTheInstallInsteadOfFailingIt()
    {
        var modelsRoot = Path.Combine(_root, "models");
        var staged = StageModel("overwatch");
        using var scanned = new FileStream(Path.Combine(staged, "model.onnx"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var releaser = new Thread(() =>
        {
            Thread.Sleep(600);
            scanned.Dispose();
        }) { IsBackground = true };
        releaser.Start();

        GameModelInstaller.InstallValidatedDirectory("overwatch", staged, modelsRoot);
        releaser.Join();

        Assert.True(File.Exists(Path.Combine(modelsRoot, "overwatch", "model.onnx")));
    }

    [Fact]
    public void ASecondInstall_ReplacesThePreviousCopy()
    {
        var modelsRoot = Path.Combine(_root, "models");
        GameModelInstaller.InstallValidatedDirectory("overwatch", StageModel("first"), modelsRoot);

        GameModelInstaller.InstallValidatedDirectory("overwatch", StageModel("second"), modelsRoot);

        Assert.True(File.Exists(Path.Combine(modelsRoot, "overwatch", "events.json")));
        Assert.Empty(Directory.GetDirectories(modelsRoot, "*.backup-*"));
    }
}
