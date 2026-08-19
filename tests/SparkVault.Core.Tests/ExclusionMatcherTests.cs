using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class ExclusionMatcherTests
{
    [Fact]
    public void MatchesSimpleWildcard()
    {
        Assert.True(ExclusionMatcher.IsExcluded("notes.tmp", new[] { "*.tmp" }));
    }

    [Fact]
    public void DoesNotMatchUnrelatedPattern()
    {
        Assert.False(ExclusionMatcher.IsExcluded("notes.txt", new[] { "*.tmp" }));
    }

    [Fact]
    public void MatchesInsideSubfolder()
    {
        Assert.True(ExclusionMatcher.IsExcluded(@"cache\file.bin", new[] { @"cache\*" }));
    }

    [Fact]
    public void NoPatternsMeansNothingExcluded()
    {
        Assert.False(ExclusionMatcher.IsExcluded("anything.txt", Array.Empty<string>()));
    }
}
