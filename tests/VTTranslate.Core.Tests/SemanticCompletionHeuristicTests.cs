using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

public class SemanticCompletionHeuristicTests
{
    [Fact]
    public void EmptyPrefix_NotComplete()
    {
        var r = SemanticCompletionHeuristic.Evaluate("");
        Assert.False(r.IsSemanticallyComplete);
    }

    // ---- Sentence-ending punctuation: strongest signal ----
    [Fact]
    public void TrailingPeriod_IsComplete_EvenIfShort()
    {
        var r = SemanticCompletionHeuristic.Evaluate("Ja.");
        Assert.True(r.IsSemanticallyComplete);
    }

    [Fact]
    public void TrailingQuestionMark_IsComplete()
    {
        var r = SemanticCompletionHeuristic.Evaluate("Can you send it?");
        Assert.True(r.IsSemanticallyComplete);
    }

    // ---- Modal verbs: WAIT ----
    [Theory]
    [InlineData("I would")]
    [InlineData("I could")]
    [InlineData("Ich möchte")]
    [InlineData("Ich kann")]
    public void TrailingModalVerb_IsNotComplete(string text)
    {
        var r = SemanticCompletionHeuristic.Evaluate(text);
        Assert.False(r.IsSemanticallyComplete);
        Assert.Contains("continuation-trigger", r.Reason);
    }

    // ---- Conjunctions: WAIT ----
    [Theory]
    [InlineData("I would like to schedule a meeting and")]
    [InlineData("Ich möchte ein Treffen vereinbaren und")]
    [InlineData("This works well but")]
    public void TrailingConjunction_IsNotComplete(string text)
    {
        var r = SemanticCompletionHeuristic.Evaluate(text);
        Assert.False(r.IsSemanticallyComplete);
    }

    // ---- Subordinate-clause introducers (German "dass"/"weil") ----
    [Theory]
    [InlineData("Ich denke, dass")]
    [InlineData("Er kommt nicht, weil")]
    public void TrailingSubordinateConjunction_IsNotComplete(string text)
    {
        var r = SemanticCompletionHeuristic.Evaluate(text);
        Assert.False(r.IsSemanticallyComplete);
    }

    // ---- Prepositions/articles: WAIT ----
    [Theory]
    [InlineData("I would like to schedule a")]
    [InlineData("Please send it to")]
    [InlineData("Ich möchte einen")]
    public void TrailingPrepositionOrArticle_IsNotComplete(string text)
    {
        var r = SemanticCompletionHeuristic.Evaluate(text);
        Assert.False(r.IsSemanticallyComplete);
    }

    // ---- German separable verbs: pending particle ----
    [Fact]
    public void SeparableVerb_PendingParticle_IsNotComplete()
    {
        // "rufe...an" ("to call up") — particle "an" never appears.
        var r = SemanticCompletionHeuristic.Evaluate("Ich rufe dich morgen früh am Vormittag");
        Assert.False(r.IsSemanticallyComplete);
        Assert.Contains("separable verb", r.Reason);
    }

    [Fact]
    public void SeparableVerb_ParticlePresent_NotBlockedByThatRule()
    {
        // Particle "an" IS present — the separable-verb rule should not block completion
        // (other rules, e.g. minimum length/trailing word, still apply independently).
        var r = SemanticCompletionHeuristic.Evaluate("Ich rufe dich morgen früh an");
        Assert.DoesNotContain("separable verb", r.Reason);
    }

    // ---- Minimum token count fallback (no punctuation, no trigger word) ----
    [Fact]
    public void ShortPunctuationlessPrefix_BelowMinimumLength_IsNotComplete()
    {
        var r = SemanticCompletionHeuristic.Evaluate("Please review");
        Assert.False(r.IsSemanticallyComplete);
        Assert.Contains("minimum token count", r.Reason);
    }

    [Fact]
    public void LongPunctuationlessPrefix_ContentWordEnding_IsComplete()
    {
        // >= MinTokenCountForPunctuationlessCompletion, ends on a content word (not a
        // trigger word), no pending separable-verb particle — conservative fallback path.
        var r = SemanticCompletionHeuristic.Evaluate("Please review the quarterly budget report");
        Assert.True(r.IsSemanticallyComplete);
    }

    // ---- Conservative: uncertain cases must WAIT, never guess ----
    [Fact]
    public void IncompleteThought_TrailingPreposition_LongButStillWaits()
    {
        // Even though this is long, the trailing preposition means more is grammatically
        // expected — length alone must never override a genuine continuation signal.
        var r = SemanticCompletionHeuristic.Evaluate("Please forward the quarterly report to");
        Assert.False(r.IsSemanticallyComplete);
    }
}
