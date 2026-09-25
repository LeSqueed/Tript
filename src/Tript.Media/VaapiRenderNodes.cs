// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;

namespace Tript.Media;

public static class VaapiRenderNodes
{
    public const string OverrideEnvironmentVariable = "TRIPT_VAAPI_DEVICE";

    private const string DriDirectory = "/dev/dri";
    private const string RenderNodePrefix = "renderD";

    public static IReadOnlyList<string> Discover() =>
        Discover(Environment.GetEnvironmentVariable(OverrideEnvironmentVariable), ListRenderNodes);

    internal static IReadOnlyList<string> Discover(string? overrideDevice, Func<IEnumerable<string>> listRenderNodes)
    {
        if (!string.IsNullOrWhiteSpace(overrideDevice))
            return [overrideDevice.Trim()];

        return listRenderNodes()
            .Select(path => (Path: path, Number: NodeNumber(path)))
            .Where(node => node.Number is not null)
            .OrderBy(node => node.Number)
            .Select(node => node.Path)
            .ToList();
    }

    private static int? NodeNumber(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(RenderNodePrefix, StringComparison.Ordinal)
               && int.TryParse(name.AsSpan(RenderNodePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                   out var number)
            ? number
            : null;
    }

    private static IEnumerable<string> ListRenderNodes()
    {
        try
        {
            return Directory.GetFiles(DriDirectory, RenderNodePrefix + "*");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
