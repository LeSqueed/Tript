// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class ModelApiV1CompatibilityTests
{
    private static readonly EventDefinition[] Definitions =
    [
        new() { Id = 0, ClassId = 0, Name = "event", Type = EventType.Trigger },
    ];

    [Fact]
    public void MatchingRuntimeContractIsCompatible()
    {
        Assert.Null(ModelApiV1Compatibility.FindMismatch(Definitions, Metadata()));
    }

    [Fact]
    public void EqualEventAndModelClassCountsAreCompatible()
    {
        var definitions = new[]
        {
            new EventDefinition { Id = 0, ClassId = 0, Name = "first", Type = EventType.Trigger },
            new EventDefinition { Id = 1, ClassId = 1, Name = "second", Type = EventType.Trigger },
        };

        Assert.Null(ModelApiV1Compatibility.FindMismatch(definitions,
            Metadata(outputDimensions: [1, 6, 8400], classNames: new Dictionary<int, string>
            {
                [0] = "first",
                [1] = "second",
            })));
    }

    [Fact]
    public void AdditionalEventClassesAreCompatibleWithAnOlderModel()
    {
        var definitions = new[]
        {
            new EventDefinition { Id = 0, ClassId = 0, Name = "existing", Type = EventType.Trigger },
            new EventDefinition { Id = 1, ClassId = 1, Name = "new event", Type = EventType.Trigger },
        };

        Assert.Null(ModelApiV1Compatibility.FindMismatch(definitions,
            Metadata(classNames: new Dictionary<int, string> { [0] = "existing" })));
    }

    [Fact]
    public void ModelWithMoreClassesThanEventsIsRejected()
    {
        var mismatch = ModelApiV1Compatibility.FindMismatch(Definitions,
            Metadata(outputDimensions: [1, 6, 8400], classNames: new Dictionary<int, string>
            {
                [0] = "event",
                [1] = "removed",
            }));

        Assert.Contains("model declares 2 classes", mismatch);
    }

    [Fact]
    public void AdditionalEventsDoNotHideAnOverlappingClassIdentityMismatch()
    {
        var definitions = new[]
        {
            new EventDefinition { Id = 0, ClassId = 0, Name = "renamed", Type = EventType.Trigger },
            new EventDefinition { Id = 1, ClassId = 1, Name = "new event", Type = EventType.Trigger },
        };

        var mismatch = ModelApiV1Compatibility.FindMismatch(definitions,
            Metadata(classNames: new Dictionary<int, string> { [0] = "original" }));

        Assert.Contains("renamed", mismatch);
        Assert.Contains("original", mismatch);
    }

    [Theory]
    [InlineData(1, 640, 640, 3)]
    [InlineData(1, 3, 320, 320)]
    [InlineData(-1, 3, 640, 640)]
    public void InputMustBeFixedFloatNchw(int d0, int d1, int d2, int d3)
    {
        var metadata = Metadata(inputDimensions: [d0, d1, d2, d3]);

        Assert.Contains("[1,3,640,640]", ModelApiV1Compatibility.FindMismatch(Definitions, metadata));
    }

    [Fact]
    public void InputAndOutputMustContainFloats()
    {
        var inputMismatch = ModelApiV1Compatibility.FindMismatch(Definitions,
            Metadata(inputElementType: typeof(double)));
        var outputMismatch = ModelApiV1Compatibility.FindMismatch(Definitions,
            Metadata(outputElementType: typeof(double)));

        Assert.Contains("input", inputMismatch);
        Assert.Contains("float", inputMismatch);
        Assert.Contains("output", outputMismatch);
        Assert.Contains("float", outputMismatch);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(1, 5, 8400, 1)]
    public void OutputMustUseRankThreeYoloLayout(params int[] dimensions)
    {
        var mismatch = ModelApiV1Compatibility.FindMismatch(Definitions,
            Metadata(outputDimensions: dimensions));

        Assert.Contains("YOLO shape", mismatch);
    }

    [Fact]
    public void StaticOutputBatchMustBeOne()
    {
        var mismatch = ModelApiV1Compatibility.FindMismatch(Definitions,
            Metadata(outputDimensions: [2, 5, 8400]));

        Assert.Contains("batch size 2", mismatch);
    }

    [Fact]
    public void EventMapMustMatchTheYoloClassRows()
    {
        var definitions = new[]
        {
            new EventDefinition { Id = 0, ClassId = 0, Name = "renamed", Type = EventType.Trigger },
        };

        var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, Metadata());

        Assert.Contains("renamed", mismatch);
        Assert.Contains("event", mismatch);
    }

    private static OnnxModelMetadata Metadata(
        Type? inputElementType = null,
        IReadOnlyList<int>? inputDimensions = null,
        Type? outputElementType = null,
        IReadOnlyList<int>? outputDimensions = null,
        IReadOnlyDictionary<int, string>? classNames = null)
        => new()
        {
            ModelPath = "model.onnx",
            InputName = "images",
            InputElementType = inputElementType ?? typeof(float),
            InputDimensions = inputDimensions ?? [1, 3, 640, 640],
            OutputName = "output0",
            OutputElementType = outputElementType ?? typeof(float),
            OutputDimensions = outputDimensions ?? [1, 5, 8400],
            ClassNames = classNames ?? new Dictionary<int, string> { [0] = "event" },
        };
}
