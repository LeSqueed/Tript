// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Detection.Tests;

[CollectionDefinition(Name)]
public class ModelSessionCollection
{
    public const string Name = "ONNX model session cache";
}
