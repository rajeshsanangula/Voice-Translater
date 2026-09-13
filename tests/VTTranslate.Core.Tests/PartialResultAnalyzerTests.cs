using VTTranslate.Core.Providers;

namespace VTTranslate.Core.Tests;

public class PartialResultAnalyzerTests
{
    [Fact]
    public void Compare_FirstPartialOfUtterance_NoPriorText_AllFlagsFalse()
    {
        var result = PartialResultAnalyzer.Compare(previous: null, current: "Hello");

        Assert.False(result.IsPrefixExtension);
        Assert.False(result.Unchanged);
        Assert.False(result.Regressed);
    }

    [Fact]
    public void Compare_ExactRepeat_IsUnchanged()
    {
        var result = PartialResultAnalyzer.Compare("I would like", "I would like");

        Assert.True(result.Unchanged);
        Assert.False(result.IsPrefixExtension);
        Assert.False(result.Regressed);
    }

    [Fact]
    public void Compare_PureAppend_IsPrefixExtension()
    {
        var result = PartialResultAnalyzer.Compare("I would like", "I would like to schedule");

        Assert.True(result.IsPrefixExtension);
        Assert.False(result.Unchanged);
        Assert.False(result.Regressed);
    }

    [Fact]
    public void Compare_TextShrinks_IsRegressed()
    {
        // Azure revised the partial down to fewer words than before — a real
        // observed behavior this instrumentation exists to detect and count.
        var result = PartialResultAnalyzer.Compare("I would like to schedule", "I would like");

        Assert.False(result.IsPrefixExtension);
        Assert.False(result.Unchanged);
        Assert.True(result.Regressed);
    }

    [Fact]
    public void Compare_TextDivergesEntirely_IsRegressed()
    {
        var result = PartialResultAnalyzer.Compare("I would like", "We should probably");

        Assert.True(result.Regressed);
    }

    [Fact]
    public void Compare_MidWordRevision_NotJustAppend_IsRegressed()
    {
        // Same length category but the tail changed — e.g. "I was belated" vs
        // "I was related" — must NOT be misclassified as a prefix extension.
        var result = PartialResultAnalyzer.Compare("I was belated", "I was belated to follows");
        Assert.True(result.IsPrefixExtension);

        var revised = PartialResultAnalyzer.Compare("I was belated to follows", "I was related to follows and quickly");
        Assert.False(revised.IsPrefixExtension);
        Assert.True(revised.Regressed);
    }

    [Fact]
    public void Compare_CasingOnlyChange_IsRegressed_DocumentedBehavior()
    {
        // Documented, deliberate: this instrumentation uses exact ordinal comparison,
        // so a casing-only revision counts as Regressed, not Unchanged. This is an
        // honest measurement choice, not a bug — see the class doc comment.
        var result = PartialResultAnalyzer.Compare("hello there", "Hello there");

        Assert.False(result.Unchanged);
        Assert.True(result.Regressed);
    }

    [Fact]
    public void Compare_EmptyStrings_HandledWithoutThrowing()
    {
        var result = PartialResultAnalyzer.Compare("", "");
        Assert.True(result.Unchanged);

        var extendFromEmpty = PartialResultAnalyzer.Compare("", "Hello");
        Assert.True(extendFromEmpty.IsPrefixExtension);
    }
}
