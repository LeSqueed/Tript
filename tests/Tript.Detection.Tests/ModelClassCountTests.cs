// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

// The class count used to come from events.json's entry count, while the YOLO parser strides the
// output tensor by 4 + numClasses. Adding or removing a single events.json entry without retraining
// therefore shifted every read and decoded every box to garbage — no exception, no log.
public class ModelClassCountTests
{
    private const string GameId = "Overwatch";

    // The literal Ultralytics writes into metadata_props["names"]: a Python dict, not JSON.
    private const string UltralyticsNames =
        "{0: 'Class zero', 1: 'Class one', 2: 'Class two', 3: 'Class three', 4: 'Class four', " +
        "5: 'Class five', 6: 'Class six'}";

    private static EventDefinition Def(int classId, string name) =>
        new() { Id = classId, ClassId = classId, Name = name, Type = EventType.Trigger };

    private static List<EventDefinition> Definitions() =>
    [
        Def(0, "Class zero"),
        Def(1, "Class one"),
        Def(2, "Class two"),
        Def(3, "Class three"),
        Def(4, "Class four"),
        Def(5, "Class five"),
        Def(6, "Class six"),
    ];

    // [batch, 4 + numClasses, numAnchors].
    [Theory]
    [InlineData(true, 7, new[] { 1, 11, 8400 })]
    [InlineData(true, 1, new[] { 1, 5, 8400 })]
    // A dynamic axis exports as -1, or 0 on older exporters. Deriving from either produces a
    // stride that looks plausible, so the shape has to be reported as saying nothing.
    [InlineData(false, 0, new[] { 1, -1, 8400 })]
    [InlineData(false, 0, new[] { 1, 0, 8400 })]
    // Box rows only, or fewer: no class rows to count.
    [InlineData(false, 0, new[] { 1, 4, 8400 })]
    // Not the layout the parser walks at all.
    [InlineData(false, 0, new[] { 1, 11 })]
    [InlineData(false, 0, new[] { 1, 11, 8400, 1 })]
    public void TryDeriveClassCount_ReadsTheClassRowsOrRefusesToGuess(
        bool expected, int expectedClasses, int[] dimensions)
    {
        Assert.Equal(expected, VisualEventDetector.TryDeriveClassCount(dimensions, out var numClasses));
        Assert.Equal(expectedClasses, numClasses);
    }

    [Fact]
    public void TryDeriveClassCount_WithoutDimensions_Refuses()
    {
        Assert.False(VisualEventDetector.TryDeriveClassCount(null, out var numClasses));
        Assert.Equal(0, numClasses);
    }

    [Fact]
    public void ParseClassNames_ReadsTheUltralyticsDictLiteral()
    {
        var names = VisualEventDetector.ParseClassNames(UltralyticsNames);

        Assert.NotNull(names);
        Assert.Equal(7, names!.Count);
        Assert.Equal("Class zero", names[0]);
        Assert.Equal("Class one", names[1]);
        Assert.Equal("Class six", names[6]);
    }

    // Another exporter may quote differently or omit the key entirely; a map that cannot be read
    // must disable the name check rather than invent entries for it to fail on.
    [Fact]
    public void ParseClassNames_AcceptsDoubleQuotes_AndYieldsNullWhenUnreadable()
    {
        var doubleQuoted = VisualEventDetector.ParseClassNames("{0: \"Class zero\", 1: \"Class one\"}");
        Assert.NotNull(doubleQuoted);
        Assert.Equal("Class zero", doubleQuoted![0]);
        Assert.Equal("Class one", doubleQuoted[1]);

        Assert.Null(VisualEventDetector.ParseClassNames(null));
        Assert.Null(VisualEventDetector.ParseClassNames("   "));
        Assert.Null(VisualEventDetector.ParseClassNames("detect"));
    }

    [Fact]
    public void ParseClassNames_AcceptsJsonObjectAndArrayMetadata()
    {
        var objectNames = VisualEventDetector.ParseClassNames("{\"0\":\"Class zero\",\"1\":\"Class one\"}");
        var arrayNames = VisualEventDetector.ParseClassNames("[\"Class zero\",\"Class one\"]");

        Assert.Equal("Class zero", objectNames![0]);
        Assert.Equal("Class one", objectNames[1]);
        Assert.Equal("Class zero", arrayNames![0]);
        Assert.Equal("Class one", arrayNames[1]);
    }

    [Fact]
    public void FindClassMapMismatch_AcceptsDefinitionsThatMatchTheModel()
    {
        var names = VisualEventDetector.ParseClassNames(UltralyticsNames);

        Assert.Null(VisualEventDetector.FindClassMapMismatch(Definitions(), 7, names));
    }

    // The exact edit the old code could not survive: one more entry than the model has classes.
    [Fact]
    public void FindClassMapMismatch_RejectsAClassIdBeyondTheModelsClasses()
    {
        var definitions = Definitions();
        definitions.Add(Def(7, "Class seven"));

        var mismatch = VisualEventDetector.FindClassMapMismatch(
            definitions, 7, VisualEventDetector.ParseClassNames(UltralyticsNames));

        Assert.NotNull(mismatch);
        Assert.Contains("7", mismatch);
        Assert.Contains("Class seven", mismatch);
    }

    // The shape check cannot see this one — the count still matches, only the meaning moved.
    [Fact]
    public void FindClassMapMismatch_RejectsAReorderedOrRenamedClass()
    {
        var definitions = Definitions();
        definitions[2].Name = "Renamed class";

        var mismatch = VisualEventDetector.FindClassMapMismatch(
            definitions, 7, VisualEventDetector.ParseClassNames(UltralyticsNames));

        Assert.NotNull(mismatch);
        Assert.Contains("Renamed class", mismatch);
        Assert.Contains("Class two", mismatch);
    }

    // Without a class map the names cannot be checked, but the count still can.
    [Fact]
    public void FindClassMapMismatch_WithoutAModelClassMap_StillBoundsChecksClassIds()
    {
        var definitions = Definitions();
        Assert.Null(VisualEventDetector.FindClassMapMismatch(definitions, 7, null));

        definitions.Add(Def(9, "Class nine"));
        Assert.NotNull(VisualEventDetector.FindClassMapMismatch(definitions, 7, null));
    }

    // Fail loudly rather than skip, matching InputTensorReuseTests: this is the assertion that ties
    // the pure helpers above to the model and events.json that actually ship, and a guard that
    // quietly disables itself where the model is absent is worse than no guard at all. Loads its
    // own session rather than ModelService's cached one: test classes run in parallel and
    // ModelService.UnloadModel disposes the shared session out from under whoever else holds it.
    [Fact]
    public void ShippedModel_DeclaresAStaticClassMap_AndAgreesWithEventsJson()
    {
        var modelPath = ModelService.GetModelPath(GameId);
        Assert.True(File.Exists(modelPath),
            $"ONNX model not found at {modelPath}. This test verifies events.json against the " +
            "real model and cannot be checked without it. It must fail, not skip.");

        using var session = new InferenceSession(modelPath);

        var outputName = session.OutputMetadata.Keys.First();
        var dimensions = session.OutputMetadata[outputName].Dimensions;

        Assert.True(VisualEventDetector.TryDeriveClassCount(dimensions, out var numClasses),
            $"Output {outputName} has shape [{string.Join(',', dimensions)}], which carries no " +
            "static class dimension — the detector would be falling back to events.json.");
        var names = VisualEventDetector.ParseClassNames(
            session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var raw) ? raw : null);
        Assert.NotNull(names);
        Assert.Equal(numClasses, names!.Count);

        var definitions = ModelService.LoadEventDefinitions(GameId);
        Assert.Equal(numClasses, definitions.Count);

        var mismatch = VisualEventDetector.FindClassMapMismatch(definitions, numClasses, names);
        Assert.True(mismatch == null,
            $"data/training/{GameId}/events.json disagrees with model.onnx: {mismatch}");

        var metadata = OnnxModelInspector.Inspect(modelPath);
        Assert.Null(ModelEventCompatibility.FindMismatch(definitions, metadata));
        Assert.Null(ModelApiV1Compatibility.FindMismatch(definitions, metadata));
    }
}
