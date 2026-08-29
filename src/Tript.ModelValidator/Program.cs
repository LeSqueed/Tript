// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Detection;

if (args.Length != 3 || !int.TryParse(args[2], out var modelApiVersion))
{
    Console.Error.WriteLine("Usage: Tript.ModelValidator <model.onnx> <events.json> <model-api-version>");
    return 2;
}

try
{
    if (modelApiVersion != ModelApiV1Compatibility.Version)
        throw new InvalidDataException($"This validator supports model API {ModelApiV1Compatibility.Version}.");
    if (!File.Exists(args[0]))
        throw new FileNotFoundException("The model file was not found.", args[0]);
    if (!File.Exists(args[1]))
        throw new FileNotFoundException("The event definition file was not found.", args[1]);

    var definitions = JsonSerializer.Deserialize<List<EventDefinition>>(File.ReadAllText(args[1]),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    var metadata = OnnxModelInspector.Inspect(args[0]);
    var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
    if (mismatch is not null)
        throw new InvalidDataException(mismatch);

    Console.WriteLine($"Model API {modelApiVersion} validation passed: {args[0]}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Model validation failed: {exception.Message}");
    return 1;
}
