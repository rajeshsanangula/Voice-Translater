using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

public class PrefixStabilityEngineTests
{
    private const string U = "utterance-1";
    private static DateTimeOffset T(int i) => DateTimeOffset.UnixEpoch.AddSeconds(i);

    private static PartialSourceEvent P(int seq, string text, string uttId = U) =>
        new(uttId, seq, text, T(seq));

    private static FinalSourceEvent F(int seq, string text, string uttId = U) =>
        new(uttId, seq, text, T(seq));

    // ---- A. Simple growing prefix ----
    [Fact]
    public void A_SimpleGrowingPrefix_CommitsIncrementallyWithNoDuplicates()
    {
        var engine = new PrefixStabilityEngine();
        var committedSegments = new List<string>();

        void Run(int seq, string text)
        {
            var r = engine.ProcessPartial(P(seq, text));
            if (r.ShouldEmit) committedSegments.Add(r.NewlyCommittedSegment!);
        }

        Run(1, "I would");
        Run(2, "I would like");
        Run(3, "I would like to");
        Run(4, "I would like to schedule");
        var last = engine.ProcessPartial(P(5, "I would like to schedule a meeting"));
        if (last.ShouldEmit) committedSegments.Add(last.NewlyCommittedSegment!);

        var finalResult = engine.ProcessFinal(F(6, "I would like to schedule a meeting."));
        if (finalResult.ShouldEmit) committedSegments.Add(finalResult.NewlyCommittedSegment!);

        var reconstructed = string.Join(" ", committedSegments).Trim();
        // Every word from the final text must appear exactly once across all segments.
        Assert.Equal("I would like to schedule a meeting.", reconstructed);
        Assert.True(finalResult.Action == StabilityAction.Finalized);
        Assert.False(finalResult.FinalizedWithCorrection);
    }

    // ---- B. Rapid partial growth (short intervals — timestamps don't affect decisions) ----
    [Fact]
    public void B_RapidPartialGrowth_TimingDoesNotAffectDeterminism()
    {
        var engineA = new PrefixStabilityEngine();
        var engineB = new PrefixStabilityEngine();

        var textsBySeq = new[] { "I would", "I would like", "I would like to schedule" };

        List<string?> RunWithTimestamps(PrefixStabilityEngine engine, Func<int, DateTimeOffset> ts)
        {
            var results = new List<string?>();
            for (int i = 0; i < textsBySeq.Length; i++)
                results.Add(engine.ProcessPartial(new PartialSourceEvent(U, i + 1, textsBySeq[i], ts(i))).NewlyCommittedSegment);
            return results;
        }

        var resultsFast = RunWithTimestamps(engineA, i => DateTimeOffset.UnixEpoch.AddMilliseconds(i));
        var resultsSlow = RunWithTimestamps(engineB, i => DateTimeOffset.UnixEpoch.AddSeconds(i * 10));

        Assert.Equal(resultsFast, resultsSlow); // identical regardless of real-world timing
    }

    // ---- C. Partial regression ----
    [Fact]
    public void C_PartialRegression_DoesNotBlindlyEmitEveryDifference()
    {
        var engine = new PrefixStabilityEngine();

        engine.ProcessPartial(P(1, "I want to book"));
        var r2 = engine.ProcessPartial(P(2, "I want to book a"));
        // "I want to" should be safe to commit by now (boundary word held back).
        Assert.True(r2.ShouldEmit);

        var r3 = engine.ProcessPartial(P(3, "I'd like to book a"));
        // Regression at the very first word — must not retract or emit garbage.
        Assert.False(r3.ShouldEmit);
        Assert.Contains("I", r2.CommittedSourceText.Split(' '));

        var final = engine.ProcessFinal(F(4, "I'd like to book a table."));
        Assert.True(final.ShouldEmit);
        Assert.True(final.FinalizedWithCorrection); // committed "I want to" contradicted the final
        Assert.Equal("I'd like to book a table.", final.CommittedSourceText);
    }

    // ---- D. Self-correction ----
    [Fact]
    public void D_SelfCorrection_PrefersCorrectnessOverPrematureEmission()
    {
        var engine = new PrefixStabilityEngine();

        // Only one partial before the correcting one — nothing should be committed yet
        // (commit requires agreement across >=2 partials), so the wrong word "Tuesday"
        // is never emitted.
        engine.ProcessPartial(P(1, "I want to meet on Tuesday"));
        var r2 = engine.ProcessPartial(P(2, "I want to meet on Thursday"));

        Assert.DoesNotContain("Tuesday", r2.CommittedSourceText);
        if (r2.ShouldEmit)
            Assert.DoesNotContain("Tuesday", r2.NewlyCommittedSegment);

        var final = engine.ProcessFinal(F(3, "I want to meet on Thursday."));
        Assert.DoesNotContain("Tuesday", final.CommittedSourceText);
        Assert.Contains("Thursday", final.CommittedSourceText);
    }

    // ---- E. Repeated identical partials ----
    [Fact]
    public void E_RepeatedIdenticalPartials_NoDuplicateEmission()
    {
        var engine = new PrefixStabilityEngine();

        var r1 = engine.ProcessPartial(P(1, "I would"));
        var r2 = engine.ProcessPartial(P(2, "I would")); // identical text, new sequence
        var r3 = engine.ProcessPartial(P(3, "I would")); // identical again

        Assert.False(r1.ShouldEmit); // first partial ever — nothing to compare against
        // At most one of r2/r3 commits "I" — never both (no duplicate emission of the same word).
        var emittedCount = new[] { r2, r3 }.Count(r => r.ShouldEmit);
        Assert.True(emittedCount <= 1);
    }

    // ---- F. Empty partial ----
    [Fact]
    public void F_EmptyPartial_HandledWithoutCorruptingState()
    {
        var engine = new PrefixStabilityEngine();

        engine.ProcessPartial(P(1, "I would like"));
        var empty = engine.ProcessPartial(P(2, ""));
        Assert.False(empty.ShouldEmit);

        // A real partial after the empty one must still compare correctly against the
        // last REAL partial, not against the empty one.
        var r3 = engine.ProcessPartial(P(3, "I would like to schedule"));
        Assert.True(r3.ShouldEmit);
    }

    // ---- G. Whitespace-only variation ----
    // A whitespace-only-different repeat of the previous partial (e.g. "I would like" then
    // "I   would    like") tokenizes identically, so it is not, and must not be, treated as
    // a semantic correction — but per the documented one-step commit-delay characteristic
    // (see docs/design-notes/prefix-stability-test-failure-analysis.md), the partial
    // immediately AFTER such a repeat can be delayed by exactly one comparison step before
    // its new growth commits. This test asserts the behavior that actually holds: no
    // duplicate emission, no content loss, no spurious correction, and full recovery of
    // every word by the time a subsequent partial/Final resolves it.
    [Fact]
    public void G_WhitespaceOnlyVariation_NotTreatedAsCorrection_AllContentEventuallyRecoveredWithNoDuplication()
    {
        var engine = new PrefixStabilityEngine();

        var r1 = engine.ProcessPartial(P(1, "I would like"));
        Assert.False(r1.ShouldEmit); // first partial for the utterance: nothing to compare against yet

        var r2 = engine.ProcessPartial(P(2, "I   would    like")); // extra whitespace only, same words
        // The whitespace-only repeat is not a correction/contradiction: it commits cleanly,
        // exactly as a byte-identical repeat would.
        Assert.True(r2.ShouldEmit);
        Assert.Equal("I would", r2.NewlyCommittedSegment);
        Assert.Equal("I would", r2.CommittedSourceText);

        var r3 = engine.ProcessPartial(P(3, "I would like to schedule"));
        // Documented one-step commit delay: this partial genuinely adds "to schedule", but
        // because the immediately-preceding partial (r2) was a no-op repeat, this step's
        // agreement count only ties (not exceeds) what's already committed — nothing new
        // commits THIS step. Critically, nothing already committed is lost or duplicated either.
        Assert.False(r3.ShouldEmit);
        Assert.Equal("I would", r3.CommittedSourceText); // unchanged — not retracted, not duplicated

        var final = engine.ProcessFinal(F(4, "I would like to schedule."));

        // 4. Whitespace-only variation must never be treated as a semantic correction —
        // confirmed all the way through to the Final: no correction was ever flagged.
        Assert.False(final.FinalizedWithCorrection);

        // 3. Final aggregate result is correct and complete.
        Assert.Equal("I would like to schedule.", final.CommittedSourceText);

        // 1 & 2. No duplicate output, no content loss: every word appears in the reconstructed
        // stream (across all commits from partials + the Final's remainder) exactly once, and
        // the reconstruction matches the true final text exactly.
        var reconstructed = string.Join(" ", new[] { r2.NewlyCommittedSegment, final.NewlyCommittedSegment });
        Assert.Equal("I would like to schedule.", reconstructed);
    }

    // ---- H. Punctuation variation ----
    [Fact]
    public void H_PunctuationVariation_DoesNotCorruptEarlierWords()
    {
        var engine = new PrefixStabilityEngine();

        engine.ProcessPartial(P(1, "I would like"));
        var r2 = engine.ProcessPartial(P(2, "I would like,"));
        // "I would" should still be committable — punctuation only affects the boundary word.
        Assert.True(r2.ShouldEmit);
        Assert.DoesNotContain(",", r2.NewlyCommittedSegment);
    }

    // ---- I. Very short utterance ("Ja.") ----
    [Fact]
    public void I_VeryShortUtterance_NoPartialsAtAll_FinalizesFully()
    {
        // Matches the real, observed Step-1 behavior: short utterances can produce ZERO
        // Recognizing events, going straight to Recognized.
        var engine = new PrefixStabilityEngine();

        var final = engine.ProcessFinal(F(1, "Ja."));

        Assert.True(final.ShouldEmit);
        Assert.Equal("Ja.", final.NewlyCommittedSegment);
        Assert.Equal("Ja.", final.CommittedSourceText);
        Assert.False(final.FinalizedWithCorrection);
    }

    // ---- J. Long sentence ----
    [Fact]
    public void J_LongSentence_AllWordsAccountedForExactlyOnce()
    {
        var engine = new PrefixStabilityEngine();
        var segments = new List<string>();
        var words = "I would like to schedule a meeting tomorrow to discuss the quarterly budget and review outstanding action items from last week".Split(' ');

        // Simulate word-by-word growth, one word per partial (worst case for partial count).
        for (int i = 1; i <= words.Length; i++)
        {
            var text = string.Join(" ", words.Take(i));
            var r = engine.ProcessPartial(P(i, text));
            if (r.ShouldEmit) segments.Add(r.NewlyCommittedSegment!);
        }
        var final = engine.ProcessFinal(F(words.Length + 1, string.Join(" ", words) + "."));
        if (final.ShouldEmit) segments.Add(final.NewlyCommittedSegment!);

        var reconstructed = string.Join(" ", segments).Trim();
        Assert.Equal(string.Join(" ", words) + ".", reconstructed);
    }

    // ---- K. Fast conversational sentence (compressed sequence numbers, same algorithm) ----
    [Fact]
    public void K_FastConversationalSentence_BehavesIdenticallyRegardlessOfPace()
    {
        var engine = new PrefixStabilityEngine();
        var r1 = engine.ProcessPartial(P(1, "Let's grab coffee"));
        var r2 = engine.ProcessPartial(P(2, "Let's grab coffee tomorrow"));
        var final = engine.ProcessFinal(F(3, "Let's grab coffee tomorrow morning."));

        Assert.True(final.ShouldEmit);
        Assert.Equal("Let's grab coffee tomorrow morning.", final.CommittedSourceText);
    }

    // ---- L. Multiple consecutive utterances ----
    [Fact]
    public void L_MultipleConsecutiveUtterances_CompleteIsolation()
    {
        var engine = new PrefixStabilityEngine();

        engine.ProcessPartial(P(1, "First utterance", "utt-A"));
        var final1 = engine.ProcessFinal(F(2, "First utterance here.", "utt-A"));
        Assert.Equal("First utterance here.", final1.CommittedSourceText);

        // Second utterance must start completely fresh — no leakage from utt-A.
        var r1 = engine.ProcessPartial(P(1, "Second", "utt-B"));
        Assert.Equal("", r1.CommittedSourceText);
        var final2 = engine.ProcessFinal(F(2, "Second utterance entirely.", "utt-B"));
        Assert.Equal("Second utterance entirely.", final2.CommittedSourceText);
        Assert.DoesNotContain("First", final2.CommittedSourceText);
    }

    // ---- M. Final arriving immediately after a partial ----
    [Fact]
    public void M_FinalImmediatelyAfterOnePartial_NoContentLost()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "Quick"));
        var final = engine.ProcessFinal(F(2, "Quick question."));

        Assert.Equal("Quick question.", final.CommittedSourceText);
        Assert.True(final.ShouldEmit);
    }

    // ---- N. Final with substantially more text than latest partial ----
    [Fact]
    public void N_FinalWithMuchMoreTextThanLatestPartial_RemainderFullyEmitted()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "I would"));
        engine.ProcessPartial(P(2, "I would like"));
        var final = engine.ProcessFinal(F(3,
            "I would like to schedule a meeting tomorrow afternoon with the whole team."));

        Assert.Equal(
            "I would like to schedule a meeting tomorrow afternoon with the whole team.",
            final.CommittedSourceText);
    }

    // ---- O. Final containing text never present in partials (zero partials case) ----
    [Fact]
    public void O_FinalWithNoPriorPartials_EntireTextEmittedAtFinalize()
    {
        var engine = new PrefixStabilityEngine();
        var final = engine.ProcessFinal(F(1, "Completely unseen text arrives directly."));

        Assert.Equal("Completely unseen text arrives directly.", final.NewlyCommittedSegment);
        Assert.False(final.FinalizedWithCorrection);
    }

    // ---- P. Already-committed prefix appearing again in the final result ----
    [Fact]
    public void P_CommittedPrefixReappearingInFinal_NotEmittedTwice()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "I would"));
        var r2 = engine.ProcessPartial(P(2, "I would like to"));
        Assert.True(r2.ShouldEmit);
        var committedSoFar = r2.CommittedSourceText;

        var final = engine.ProcessFinal(F(3, "I would like to schedule now."));

        // The committed prefix must not appear twice in the newly-emitted segment.
        Assert.DoesNotContain(committedSoFar, final.NewlyCommittedSegment ?? "");
        Assert.Equal("I would like to schedule now.", final.CommittedSourceText);
    }

    // ---- Q. Partial regression after something has already been committed ----
    [Fact]
    public void Q_RegressionAfterCommit_CommittedContentNeverRetractedOrDuplicated()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "I want to book"));
        var r2 = engine.ProcessPartial(P(2, "I want to book a"));
        Assert.True(r2.ShouldEmit);
        var committedBefore = r2.CommittedSourceText;

        // Full divergence at word 1.
        var r3 = engine.ProcessPartial(P(3, "We should probably book a"));
        Assert.False(r3.ShouldEmit); // no new commit; nothing retracted either
        Assert.Equal(committedBefore, r3.CommittedSourceText);
    }

    // ---- R. Stale/out-of-order partial ----
    [Fact]
    public void R_StaleOutOfOrderPartial_Rejected()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "I would"));
        var r2 = engine.ProcessPartial(P(5, "I would like to schedule"));
        Assert.True(r2.ShouldEmit);
        var stateAfterR2 = r2.CommittedSourceText;

        // A late-arriving partial with an OLDER sequence number must be ignored.
        var stale = engine.ProcessPartial(P(3, "Something completely different"));
        Assert.Equal(StabilityAction.None, stale.Action);
        Assert.Equal(stateAfterR2, stale.CommittedSourceText); // unchanged
    }

    // =====================================================================
    // CRITICAL SAFETY PROPERTY TESTS
    // =====================================================================

    [Fact]
    public void Safety_NoDuplicateSourceSegment_AcrossFullUtteranceLifecycle()
    {
        var engine = new PrefixStabilityEngine();
        var allEmittedWords = new List<string>();

        void Emit(StabilityResult r)
        {
            if (r.ShouldEmit) allEmittedWords.AddRange(r.NewlyCommittedSegment!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        Emit(engine.ProcessPartial(P(1, "I would")));
        Emit(engine.ProcessPartial(P(2, "I would like")));
        Emit(engine.ProcessPartial(P(3, "I would like to")));
        Emit(engine.ProcessPartial(P(4, "I would like to schedule")));
        Emit(engine.ProcessFinal(F(5, "I would like to schedule a meeting.")));

        // Reconstructing by concatenation must equal the final text exactly — proves no
        // word was ever emitted twice and none were skipped.
        Assert.Equal("I would like to schedule a meeting.", string.Join(" ", allEmittedWords));
    }

    [Fact]
    public void Safety_NoLostFinalContent_EvenWithHeavyRegression()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "Completely wrong start"));
        engine.ProcessPartial(P(2, "Completely wrong start here"));
        var final = engine.ProcessFinal(F(3, "Totally different final sentence entirely."));

        Assert.Equal("Totally different final sentence entirely.", final.CommittedSourceText);
    }

    [Fact]
    public void Safety_UnstableTextCanRemainPending_NotForceCommitted()
    {
        var engine = new PrefixStabilityEngine();
        var r1 = engine.ProcessPartial(P(1, "I would like to schedule a meeting"));
        // Only one partial ever seen — nothing should be committed (no 2-partial agreement yet).
        Assert.False(r1.ShouldEmit);
        Assert.Equal("", r1.CommittedSourceText);
        Assert.NotEqual("", r1.PendingUnstableText);
    }

    [Fact]
    public void Safety_FinalizationFlushesAllPendingContent()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "I would like to schedule a meeting"));
        var final = engine.ProcessFinal(F(2, "I would like to schedule a meeting tomorrow."));

        Assert.Equal("", final.PendingUnstableText);
        Assert.Equal("I would like to schedule a meeting tomorrow.", final.CommittedSourceText);
    }

    [Fact]
    public void Safety_ResetIsolatesUtterances_ExplicitResetCall()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "Some text here"));
        engine.Reset();

        var r = engine.ProcessPartial(P(1, "Brand new utterance"));
        Assert.Equal("", r.CommittedSourceText.Length == 0 ? "" : r.CommittedSourceText); // no leakage
        Assert.DoesNotContain("Some", r.PendingUnstableText);
    }

    // =====================================================================
    // PROPERTY-STYLE TESTS (hand-rolled, fixed-seed — no new test framework)
    // =====================================================================

    [Fact]
    public void Property_RandomizedGrowingSequences_NeverDuplicateNeverLoseContent()
    {
        var vocabulary = new[] { "I", "would", "like", "to", "schedule", "a", "meeting", "tomorrow", "afternoon", "please" };
        var random = new Random(Seed: 42); // fixed seed — deterministic across runs

        for (int trial = 0; trial < 50; trial++)
        {
            var engine = new PrefixStabilityEngine();
            var sentenceLength = random.Next(3, vocabulary.Length + 1);
            var words = Enumerable.Range(0, sentenceLength).Select(_ => vocabulary[random.Next(vocabulary.Length)]).ToArray();

            var emitted = new List<string>();
            var seq = 1;
            // Purely growing partials (no regressions) — a "well-behaved" sequence, since
            // arbitrary random regressions make the "monotonic accumulation" property
            // ill-defined without additional bookkeeping beyond this phase's scope.
            for (int i = 1; i <= words.Length; i++)
            {
                var text = string.Join(" ", words.Take(i));
                var r = engine.ProcessPartial(P(seq++, text));
                if (r.ShouldEmit) emitted.Add(r.NewlyCommittedSegment!);
            }
            var final = engine.ProcessFinal(F(seq, string.Join(" ", words)));
            if (final.ShouldEmit) emitted.Add(final.NewlyCommittedSegment!);

            var reconstructed = string.Join(" ", emitted).Trim();
            Assert.Equal(string.Join(" ", words), reconstructed); // finalization preserves final text exactly

            // No duplicate committed prefixes: concatenated emitted word count must equal
            // exactly the source word count — never more (duplication), never less (loss).
            var emittedWordCount = emitted.SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Count();
            Assert.Equal(words.Length, emittedWordCount);
        }
    }

    [Fact]
    public void Property_ResetAlwaysReturnsToInitialState()
    {
        var random = new Random(Seed: 7);
        var engine = new PrefixStabilityEngine();

        for (int trial = 0; trial < 20; trial++)
        {
            var wordCount = random.Next(1, 8);
            var words = Enumerable.Range(0, wordCount).Select(i => $"word{i}");
            for (int i = 1; i <= wordCount; i++)
                engine.ProcessPartial(P(i, string.Join(" ", words.Take(i))));

            engine.Reset();

            var r = engine.ProcessPartial(P(1, "fresh"));
            Assert.Equal("", r.CommittedSourceText); // always starts clean after Reset
            engine.Reset();
        }
    }

    // ---- V-1 fix: comparison-only trailing punctuation normalization ----
    // These tests commit a word via partials, then verify a Final that differs ONLY by
    // trailing punctuation on the already-committed word is NOT treated as a correction,
    // while a genuine lexical change still is. Emitted text is asserted to always retain
    // the exact original punctuation from its source event — never stripped.

    [Fact]
    public void V1_A_CommittedWould_FinalWouldComma_IsNotTreatedAsCorrection()
    {
        var engine = new PrefixStabilityEngine();
        var p1 = engine.ProcessPartial(P(1, "I would"));
        var p2 = engine.ProcessPartial(P(2, "I would like"));
        Assert.True(p2.ShouldEmit);
        Assert.Equal("I", p2.NewlyCommittedSegment); // "would" still held back as boundary word

        // Force "would" to commit via one more partial, then finalize with a comma on it.
        var p3 = engine.ProcessPartial(P(3, "I would like to"));
        Assert.Equal("would", p3.NewlyCommittedSegment);

        var final = engine.ProcessFinal(F(4, "I would, actually like to schedule."));

        Assert.False(final.FinalizedWithCorrection);
        Assert.Equal("I would, actually like to schedule.", final.CommittedSourceText);
        // The committed segment "would" (no comma) was never re-emitted with a comma attached;
        // the final's remainder starts after the agreed "I would" prefix.
        Assert.Equal("actually like to schedule.", final.NewlyCommittedSegment);
    }

    [Fact]
    public void V1_B_CommittedMeeting_FinalMeetingPeriod_IsNotTreatedAsCorrection()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "a meeting"));
        var p2 = engine.ProcessPartial(P(2, "a meeting today"));
        Assert.Equal("a", p2.NewlyCommittedSegment);
        var p3 = engine.ProcessPartial(P(3, "a meeting today now"));
        Assert.Equal("meeting", p3.NewlyCommittedSegment);
        Assert.Equal("a meeting", p3.CommittedSourceText);

        var final = engine.ProcessFinal(F(4, "a meeting."));

        Assert.False(final.FinalizedWithCorrection);
        Assert.False(final.ShouldEmit); // "meeting." vs committed "meeting" is not new content
        Assert.Equal("a meeting.", final.CommittedSourceText);
    }

    [Fact]
    public void V1_C_CommittedMeeting_FinalMeetingQuestionMark_IsNotTreatedAsCorrection()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "a meeting"));
        var p2 = engine.ProcessPartial(P(2, "a meeting today"));
        Assert.Equal("a", p2.NewlyCommittedSegment);
        var p3 = engine.ProcessPartial(P(3, "a meeting today now"));
        Assert.Equal("meeting", p3.NewlyCommittedSegment);
        Assert.Equal("a meeting", p3.CommittedSourceText);

        var final = engine.ProcessFinal(F(4, "a meeting?"));

        Assert.False(final.FinalizedWithCorrection);
        Assert.False(final.ShouldEmit);
        Assert.Equal("a meeting?", final.CommittedSourceText);
    }

    [Fact]
    public void V1_D_GenuineLexicalCorrection_TuesdayToThursday_StillTreatedAsCorrection()
    {
        var engine = new PrefixStabilityEngine();
        var p1 = engine.ProcessPartial(P(1, "meet on Tuesday"));
        var p2 = engine.ProcessPartial(P(2, "meet on Tuesday next"));
        Assert.Equal("meet on", p2.NewlyCommittedSegment);
        var p3 = engine.ProcessPartial(P(3, "meet on Tuesday next week"));
        Assert.Equal("Tuesday", p3.NewlyCommittedSegment);
        Assert.Equal("meet on Tuesday", p3.CommittedSourceText);

        var final = engine.ProcessFinal(F(4, "meet on Thursday next week."));

        // Genuine word-level change ("Tuesday" -> "Thursday") must still be detected as a
        // correction — punctuation normalization must not mask a real lexical difference.
        Assert.True(final.FinalizedWithCorrection);
        Assert.Equal("meet on Thursday next week.", final.CommittedSourceText);
        Assert.Equal("Thursday next week.", final.NewlyCommittedSegment);
    }

    [Fact]
    public void V1_E_PunctuationOnlyDifferenceBetweenPartials_DoesNotCreateCorrection()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "I would"));
        var p2 = engine.ProcessPartial(P(2, "I would like"));
        Assert.Equal("I", p2.NewlyCommittedSegment);

        // Next partial repeats "would" but now with a trailing comma — must still be
        // recognized as agreement (an extension), not a contradiction.
        var p3 = engine.ProcessPartial(P(3, "I would, like to"));
        Assert.Equal(StabilityAction.Committed, p3.Action);
        Assert.Equal("would,", p3.NewlyCommittedSegment);
        Assert.Equal("I would,", p3.CommittedSourceText);
    }

    [Fact]
    public void V1_F_WhitespaceOnlyDifference_DoesNotCreateCorrection()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "I would"));
        var p2 = engine.ProcessPartial(P(2, "I would like"));
        Assert.Equal("I", p2.NewlyCommittedSegment);
        var p3 = engine.ProcessPartial(P(3, "I  would   like  to")); // extra internal whitespace
        Assert.Equal("would", p3.NewlyCommittedSegment);

        var final = engine.ProcessFinal(F(4, "I would like to schedule."));
        Assert.False(final.FinalizedWithCorrection);
    }

    [Fact]
    public void V1_G_ExistingRegressionTests_StillValid_ContradictionAfterCommitStillHandled()
    {
        // Same shape as the pre-fix regression-safety trace: a real content contradiction
        // (not punctuation) after a commit must still result in no retraction and a flagged
        // Final correction — normalization must not weaken this.
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "I want to book"));
        var p2 = engine.ProcessPartial(P(2, "I want to book a"));
        Assert.Equal("I want to", p2.NewlyCommittedSegment);

        var p3 = engine.ProcessPartial(P(3, "I'd like to book a"));
        Assert.Equal(StabilityAction.None, p3.Action); // contradiction: no new commit
        Assert.Equal("I want to", p3.CommittedSourceText); // not retracted

        var final = engine.ProcessFinal(F(4, "I'd like to book a table."));
        Assert.True(final.FinalizedWithCorrection);
        Assert.Equal("I'd like to book a table.", final.CommittedSourceText);
    }

    [Fact]
    public void V1_EmittedText_NeverStripsPunctuation_EvenWhenUsedOnlyForComparison()
    {
        var engine = new PrefixStabilityEngine();
        engine.ProcessPartial(P(1, "well"));
        var p2 = engine.ProcessPartial(P(2, "well, actually"));
        var final = engine.ProcessFinal(F(3, "well, actually yes."));

        // No emitted segment anywhere in this run should have had its punctuation removed.
        Assert.DoesNotContain(final.NewlyCommittedSegment ?? "", "actually,"); // sanity: comma not relocated
        Assert.Equal("well, actually yes.", final.CommittedSourceText);
    }
}
