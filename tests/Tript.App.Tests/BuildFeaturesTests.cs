using Tript.Core;
using Xunit;

namespace Tript.App.Tests;

public sealed class BuildFeaturesTests
{
    [Fact]
    public void Training_is_disabled_by_default_or_enabled_only_by_the_compile_symbol()
    {
#if TRIPT_TRAINING
        Assert.True(BuildFeatures.TrainingEnabled);
#else
        Assert.False(BuildFeatures.TrainingEnabled);
#endif
    }
}
