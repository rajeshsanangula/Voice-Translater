using System.Text.RegularExpressions;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.14. A deterministic, fully explainable
/// (non-AI/non-LLM) set of surface-level checks comparing a naturalized candidate against
/// its baseline translation. This is EXPLICITLY NOT semantic understanding — it has no
/// parsing, no entailment model, no embedding similarity, nothing that "understands" the
/// sentence. It is a conservative, auditable set of heuristics that catch OBVIOUS
/// preservation failures (a number disappearing, a question mark disappearing, a negation
/// word count changing) and is deliberately biased toward REJECTED over SAFE_TO_USE for
/// anything it cannot confidently verify — the same "wait when ambiguous" philosophy
/// <see cref="SemanticCompletionHeuristic"/> (Step 5.8) uses, applied here to validation
/// instead of segmentation.
///
/// KNOWN, DOCUMENTED LIMITATIONS (see docs/design-notes/translation-naturalization-experiment.md
/// §Validation strategy for the full discussion):
/// <list type="bullet">
/// <item><description>Cannot detect a MEANING change that preserves the same surface
/// tokens/counts (e.g., swapping which of two named entities did what, or reversing a
/// comparison "faster than" → "slower than" without changing any number/negation/name
/// count).</description></item>
/// <item><description>Name/entity detection is a crude capitalization heuristic — it will
/// miss lowercase entities and can false-positive on any capitalized common noun.</description></item>
/// <item><description>Date/number extraction is regex-based and will miss dates or
/// numbers spelled out in words (e.g., "dreiundzwanzig" or "twenty-three").</description></item>
/// <item><description>Modality/qualifier checks compare PRESENCE of a canonical category,
/// not degree — "might" and "could" both count as the modal category and won't flag a
/// subtle certainty shift between them.</description></item>
/// </list>
/// This is documented, not hidden, per the explicit instruction: "Do not pretend
/// deterministic text comparison is equivalent to semantic understanding."
///
/// STEP 5.14B FIXES — two concrete, reproducible false-positives discovered during the
/// Step 5.14A live Gemini run (see docs/translation-naturalization-gemini-experiment.md
/// §12b/§12c) are fixed here, narrowly, with regression tests
/// (<c>MeaningPreservationValidatorTests.cs</c>):
/// <list type="bullet">
/// <item><description><b>Terminology check now direction-aware.</b> A required term is
/// only enforced if it actually appears in the BASELINE text. Previously, a caller
/// supplying both a source- and target-language spelling of one term (as the corpus does)
/// would always fail the direction whose target language doesn't contain the OTHER
/// language's spelling — rejecting every candidate for that case regardless of quality.</description></item>
/// <item><description><b>Token coverage now contraction-aware, comparison-only.</b> A
/// small, explicit, non-exhaustive dictionary of common English/German colloquial
/// contractions (e.g. "geht's" ↔ "geht es", "it's" ↔ "it is") is expanded ONLY for the
/// token-overlap comparison — never affecting emitted/compared text elsewhere, and never
/// affecting any other check. This mirrors <c>PrefixStabilityEngine</c>'s existing
/// "punctuation stripped for comparison only, never for emitted text" pattern. It does NOT
/// lower the coverage threshold and does NOT weaken detection of a genuinely different
/// sentence — it only prevents a legitimate, meaning-identical contraction from being
/// miscounted as two unrelated tokens on each side.</description></item>
/// </list>
/// </summary>
public static class MeaningPreservationValidator
{
    /// <summary>Below this word-level Jaccard overlap ratio, the candidate is treated as too different from the baseline to trust — a coarse "did this turn into a different sentence" guard, not a fidelity score.</summary>
    public const double MinTokenCoverageRatio = 0.4;

    private static readonly HashSet<string> NegationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "n't", "never", "no", "none", "nobody", "nothing", "neither", "nor", "cannot",
        "nicht", "kein", "keine", "keinen", "keinem", "keiner", "keines", "niemals", "nie", "niemand", "nichts", "weder",
    };

    private static readonly HashSet<string> ModalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "would", "could", "should", "can", "will", "must", "might", "may", "shall",
        "würde", "würden", "könnte", "könnten", "kann", "können", "sollte", "sollten",
        "muss", "müsste", "müssen", "wird", "werden", "darf", "dürfen", "mag", "mögen",
    };

    private static readonly HashSet<string> QualifierWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "maybe", "perhaps", "possibly", "approximately", "roughly", "about", "likely", "probably",
        "vielleicht", "möglicherweise", "ungefähr", "etwa", "wahrscheinlich", "eventuell",
    };

    // Common capitalized sentence-start/function words excluded from the name/entity
    // heuristic so they don't count as false-positive "entities" merely for starting a
    // sentence or being a common capitalized function word in English/German.
    private static readonly HashSet<string> CapitalizedNonEntityWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "i", "der", "die", "das", "ich", "sie", "wir", "ihr", "es",
        "this", "that", "these", "those", "dieser", "diese", "dieses",
    };

    // Common colloquial contractions (English + German), expanded ONLY for the token-
    // coverage comparison in TokenCoverageOk — never for emitted text, and never for any
    // other check (negation/numbers/dates/names/etc. still compare raw tokens). Deliberately
    // small and explicit, not exhaustive — new false-positives found later should be added
    // here individually with a regression test, not addressed by loosening the threshold.
    private static readonly Dictionary<string, string> ContractionExpansions = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        ["i'm"] = "i am", ["it's"] = "it is", ["that's"] = "that is", ["let's"] = "let us",
        ["don't"] = "do not", ["doesn't"] = "does not", ["didn't"] = "did not",
        ["can't"] = "cannot", ["won't"] = "will not", ["isn't"] = "is not", ["aren't"] = "are not",
        ["wasn't"] = "was not", ["weren't"] = "were not", ["couldn't"] = "could not",
        ["wouldn't"] = "would not", ["shouldn't"] = "should not", ["you're"] = "you are",
        ["we're"] = "we are", ["they're"] = "they are", ["i've"] = "i have", ["we've"] = "we have",
        ["they've"] = "they have", ["i'll"] = "i will", ["we'll"] = "we will", ["he's"] = "he is",
        ["she's"] = "she is", ["what's"] = "what is", ["there's"] = "there is",
        // German
        ["geht's"] = "geht es", ["gibt's"] = "gibt es", ["hab's"] = "habe es", ["war's"] = "war es",
        ["fürs"] = "für das", ["ins"] = "in das", ["ans"] = "an das", ["aufs"] = "auf das",
        ["übers"] = "über das", ["durchs"] = "durch das", ["unters"] = "unter das", ["vors"] = "vor das",
        ["hinters"] = "hinter das", ["zum"] = "zu dem", ["zur"] = "zu der",
    };

    private static readonly Regex NumberRegex = new(@"\d+([.,]\d+)*", RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(
        @"\b\d{1,2}[./]\d{1,2}([./]\d{2,4})?\b|\b(January|February|March|April|May|June|July|August|September|October|November|December|" +
        @"Januar|Februar|März|April|Mai|Juni|Juli|August|September|Oktober|November|Dezember)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ValidationFindings Evaluate(string baselineText, string candidateText, IReadOnlyList<string>? terminologyTerms)
    {
        var baselineTokens = Tokenize(baselineText);
        var candidateTokens = Tokenize(candidateText);

        var tokenCoverageOk = TokenCoverageOk(baselineTokens, candidateTokens);
        var negationOk = CountMatches(baselineTokens, candidateTokens, NegationWords);
        var numbersOk = SetsMatch(ExtractAll(baselineText, NumberRegex), ExtractAll(candidateText, NumberRegex));
        var datesOk = SetsMatch(ExtractAll(baselineText, DateRegex, normalizeCase: true), ExtractAll(candidateText, DateRegex, normalizeCase: true));
        var namesOk = SetsMatch(ExtractCapitalizedEntities(baselineText), ExtractCapitalizedEntities(candidateText));
        var terminologyOk = TerminologyPreserved(baselineText, candidateText, terminologyTerms);
        var questionOk = baselineText.TrimEnd().EndsWith('?') == candidateText.TrimEnd().EndsWith('?');
        var modalityOk = CountMatches(baselineTokens, candidateTokens, ModalWords);
        var qualifiersOk = CountMatches(baselineTokens, candidateTokens, QualifierWords);

        string? reason = null;
        if (!tokenCoverageOk) reason = $"token overlap below {MinTokenCoverageRatio:P0} — candidate too different from baseline";
        else if (!negationOk) reason = "negation word count changed between baseline and candidate";
        else if (!numbersOk) reason = "numeric content changed between baseline and candidate";
        else if (!datesOk) reason = "date content changed between baseline and candidate";
        else if (!namesOk) reason = "capitalized name/entity set changed between baseline and candidate";
        else if (!terminologyOk) reason = "a required terminology term present in baseline is missing from candidate";
        else if (!questionOk) reason = "question/statement form changed (trailing '?' presence differs)";
        else if (!modalityOk) reason = "modal-verb category presence changed between baseline and candidate";
        else if (!qualifiersOk) reason = "hedging/qualifier word presence changed between baseline and candidate";

        return new ValidationFindings(
            tokenCoverageOk, negationOk, numbersOk, datesOk, namesOk, terminologyOk, questionOk, modalityOk, qualifiersOk, reason);
    }

    public static ValidationOutcome Classify(ValidationFindings findings) =>
        findings.FailureReason == null ? Streaming.ValidationOutcome.SafeToUse : Streaming.ValidationOutcome.Rejected;

    private static bool TokenCoverageOk(IReadOnlyList<string> baseline, IReadOnlyList<string> candidate)
    {
        if (baseline.Count == 0 && candidate.Count == 0) return true;
        if (baseline.Count == 0 || candidate.Count == 0) return false;

        var baselineSet = new HashSet<string>(ExpandContractionsForComparison(baseline), StringComparer.OrdinalIgnoreCase);
        var candidateSet = new HashSet<string>(ExpandContractionsForComparison(candidate), StringComparer.OrdinalIgnoreCase);
        var intersection = baselineSet.Intersect(candidateSet, StringComparer.OrdinalIgnoreCase).Count();
        var union = baselineSet.Union(candidateSet, StringComparer.OrdinalIgnoreCase).Count();
        var ratio = union == 0 ? 1.0 : (double)intersection / union;
        return ratio >= MinTokenCoverageRatio;
    }

    // Comparison-only expansion (see class-level "STEP 5.14B FIXES" doc comment) — the
    // returned tokens are used ONLY to build the Jaccard sets in TokenCoverageOk, never
    // emitted, never used by any other check.
    private static IEnumerable<string> ExpandContractionsForComparison(IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            var trimmed = TrimPunctuation(token);
            if (ContractionExpansions.TryGetValue(trimmed, out var expansion))
            {
                foreach (var word in expansion.Split(' ')) yield return word;
            }
            else
            {
                yield return token;
            }
        }
    }

    private static bool CountMatches(IReadOnlyList<string> baseline, IReadOnlyList<string> candidate, HashSet<string> vocabulary)
    {
        var baselineCount = baseline.Count(t => vocabulary.Contains(TrimPunctuation(t)));
        var candidateCount = candidate.Count(t => vocabulary.Contains(TrimPunctuation(t)));
        return baselineCount == candidateCount;
    }

    // FIXED (Step 5.14B, see class-level doc comment): a term is only enforced if it
    // actually appears in the BASELINE — a term supplied only for the other translation
    // direction (e.g., the source-language spelling when this case is source->target) is
    // correctly never expected in the candidate and must not cause a spurious rejection.
    private static bool TerminologyPreserved(string baselineText, string candidateText, IReadOnlyList<string>? terms)
    {
        if (terms == null || terms.Count == 0) return true;
        var applicableTerms = terms.Where(term => baselineText.Contains(term, StringComparison.OrdinalIgnoreCase));
        return applicableTerms.All(term => candidateText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SetsMatch(HashSet<string> a, HashSet<string> b) => a.SetEquals(b);

    private static HashSet<string> ExtractAll(string text, Regex regex, bool normalizeCase = false)
    {
        var matches = regex.Matches(text).Select(m => normalizeCase ? m.Value.ToLowerInvariant() : m.Value);
        return new HashSet<string>(matches, StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractCapitalizedEntities(string text)
    {
        var tokens = Tokenize(text);
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var trimmed = TrimPunctuation(tokens[i]);
            if (trimmed.Length == 0 || !char.IsUpper(trimmed[0])) continue;
            if (i == 0) continue; // sentence-start capitalization is not an entity signal on its own
            if (CapitalizedNonEntityWords.Contains(trimmed)) continue;
            result.Add(trimmed);
        }
        return result;
    }

    private static string TrimPunctuation(string token) => token.TrimEnd('.', ',', '!', '?', ';', ':');

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new List<string>() : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
}
