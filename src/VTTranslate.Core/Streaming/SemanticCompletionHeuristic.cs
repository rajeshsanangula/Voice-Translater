namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.8. A deterministic, fully explainable
/// (non-AI/non-LLM) heuristic estimating whether a committed SOURCE text prefix is
/// SEMANTICALLY complete enough to be worth translating — as opposed to merely STABLE
/// (Policy A's/PrefixStabilityEngine's notion, which only means "Azure is unlikely to
/// revise this text," not "this text is a complete thought"). SOURCE STABILITY !=
/// TRANSLATION READINESS is the entire premise of this class.
///
/// This is explicitly NOT a linguistic parser. It looks at a small, hand-picked set of
/// surface-level signals — trailing sentence-ending punctuation, a trailing
/// conjunction/preposition/article/modal-or-auxiliary-verb (all of which strongly imply
/// more content is still coming), a small dictionary of common German separable-verb
/// stems whose particle has not yet appeared, and a conservative minimum token count as a
/// last-resort fallback when none of the above apply. It has no part-of-speech tagging,
/// no dependency parsing, and no semantic understanding whatsoever — it is a fast,
/// auditable, conservative approximation, and is documented as such throughout
/// docs/design-notes/semantic-segment-translation-experiment.md.
///
/// Conservative by design: every ambiguous case returns "not complete" (WAIT). This
/// heuristic is deliberately biased toward false negatives (waiting when a human might
/// judge the text already complete) over false positives (declaring completion
/// prematurely) — per the explicit instruction that latency reduction is secondary to
/// correctness for this experiment.
/// </summary>
public static class SemanticCompletionHeuristic
{
    /// <summary>Minimum token count required to declare completion WITHOUT trailing punctuation — the conservative fallback path.</summary>
    public const int MinTokenCountForPunctuationlessCompletion = 6;

    // Conjunctions, prepositions, articles/determiners, and modal/auxiliary verbs — English
    // and German. A prefix ending in one of these words is read as "a continuation is
    // grammatically expected next," regardless of length or punctuation elsewhere.
    private static readonly HashSet<string> ContinuationTriggerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English conjunctions
        "and", "but", "because", "so", "although", "that", "which", "or", "um", "yet", "while",
        // German conjunctions
        "und", "aber", "weil", "dass", "daß", "sondern", "denn", "obwohl", "dennoch", "als", "wenn", "ob",
        // English prepositions
        "to", "in", "on", "at", "for", "with", "of", "from", "about", "by", "into", "onto", "over", "under",
        // German prepositions
        "zu", "an", "auf", "für", "mit", "von", "aus", "bei", "nach", "über", "unter", "um", "durch", "gegen",
        // English articles/determiners/possessives
        "a", "an", "the", "this", "that", "these", "those", "my", "your", "his", "her", "its", "our", "their",
        // German articles/determiners/possessives
        "der", "die", "das", "ein", "eine", "einen", "einem", "einer", "eines", "dieser", "diese", "dieses",
        "mein", "meine", "meinen", "dein", "deine", "sein", "seine", "ihr", "ihre", "unser", "unsere",
        // English modal/auxiliary verbs
        "would", "could", "should", "can", "will", "must", "might", "may", "have", "has", "had",
        "is", "are", "was", "were", "do", "does", "did", "am", "be", "been",
        // German modal/auxiliary verbs
        "möchte", "möchten", "möchtest", "kann", "kannst", "könnte", "können", "könnten",
        "soll", "sollte", "sollen", "muss", "müsste", "müssen", "will", "willst", "würde", "würden",
        "habe", "hast", "hat", "haben", "hatte", "ist", "sind", "bin", "seid", "war", "waren",
    };

    // A small, explicitly non-exhaustive dictionary of common German separable-verb
    // (trennbares Verb) conjugated stems mapped to their required trailing particle. A
    // prefix containing the stem but not yet the particle is read as grammatically
    // incomplete — the particle carries essential meaning (e.g. "rufe...an" = "call up",
    // not just "call") and may appear much later in the clause.
    private static readonly Dictionary<string, string> GermanSeparableVerbStems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rufe"] = "an", ["rufst"] = "an", ["ruft"] = "an", ["rief"] = "an",
        ["stehe"] = "auf", ["stehst"] = "auf", ["steht"] = "auf", ["stand"] = "auf",
        ["lade"] = "ein", ["lädst"] = "ein", ["lädt"] = "ein", ["lud"] = "ein",
        ["komme"] = "zurück", ["kommst"] = "zurück", ["kommt"] = "zurück", ["kam"] = "zurück",
        ["fange"] = "an", ["fängst"] = "an", ["fängt"] = "an", ["fing"] = "an",
        ["mache"] = "mit", ["machst"] = "mit", ["macht"] = "mit",
        ["schlage"] = "vor", ["schlägst"] = "vor", ["schlägt"] = "vor", ["schlug"] = "vor",
    };

    /// <param name="committedSourcePrefix">The full committed (not necessarily final) SOURCE text to evaluate.</param>
    public static SemanticCompletionResult Evaluate(string? committedSourcePrefix)
    {
        var tokens = Tokenize(committedSourcePrefix);
        if (tokens.Count == 0)
            return new SemanticCompletionResult(false, "empty prefix");

        var last = tokens[^1];

        // 1. Trailing sentence-ending punctuation — the single strongest, most
        // conservative signal available; always sufficient on its own.
        if (last.EndsWith('.') || last.EndsWith('!') || last.EndsWith('?'))
            return new SemanticCompletionResult(true, "ends with sentence-ending punctuation");

        var lastTrimmed = TrimPunctuation(last);

        // 2. Trailing continuation-trigger word (conjunction/preposition/article/modal-
        // or-auxiliary-verb) — strongly implies more content is grammatically expected. WAIT.
        if (ContinuationTriggerWords.Contains(lastTrimmed))
            return new SemanticCompletionResult(false, $"trailing continuation-trigger word '{lastTrimmed}'");

        // 3. German separable-verb pending-particle check — a conjugated stem whose
        // particle has not yet appeared anywhere in the prefix means the clause's core
        // meaning is not yet complete, regardless of how long or punctuation-free the
        // rest of the prefix looks.
        foreach (var tok in tokens)
        {
            var stem = TrimPunctuation(tok);
            if (GermanSeparableVerbStems.TryGetValue(stem, out var particle) &&
                !tokens.Any(t => string.Equals(TrimPunctuation(t), particle, StringComparison.OrdinalIgnoreCase)))
            {
                return new SemanticCompletionResult(false, $"separable verb '{stem}' pending particle '{particle}'");
            }
        }

        // 4. Conservative fallback: without punctuation and without a trailing trigger or
        // pending particle, only declare completion once a minimum length is reached —
        // short punctuation-free prefixes are too likely to still be mid-thought.
        if (tokens.Count < MinTokenCountForPunctuationlessCompletion)
            return new SemanticCompletionResult(false, $"below minimum token count ({tokens.Count} < {MinTokenCountForPunctuationlessCompletion}) with no punctuation");

        return new SemanticCompletionResult(true, "sufficient length, content-word ending, no pending trigger/particle detected");
    }

    private static string TrimPunctuation(string token) => token.TrimEnd('.', ',', '!', '?', ';', ':');

    private static List<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new List<string>() : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
}

/// <summary>Result of <see cref="SemanticCompletionHeuristic.Evaluate"/> — the boolean decision plus a human-readable, non-secret reason (never contains the evaluated text itself).</summary>
public sealed record SemanticCompletionResult(bool IsSemanticallyComplete, string Reason);
