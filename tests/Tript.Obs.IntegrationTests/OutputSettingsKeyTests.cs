// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class OutputSettingsKeyTests
{
    [SkippableFact]
    public void TheFileMuxer_ExposesExactlyThePathKeyTriptWrites()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var live = ObsOutput.EnumerateTypeProperties("ffmpeg_muxer").Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(["path"], live);
    }
}
