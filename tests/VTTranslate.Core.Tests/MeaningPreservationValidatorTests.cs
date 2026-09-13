using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

public class MeaningPreservationValidatorTests
{
    [Fact]
    public void IdenticalText_AllChecksPass_SafeToUse()
    {
        var findings = MeaningPreservationValidator.Evaluate("The meeting is at three o'clock.", "The meeting is at three o'clock.", null);
        Assert.Equal(ValidationOutcome.SafeToUse, MeaningPreservationValidator.Classify(findings));
        Assert.Null(findings.FailureReason);
    }

    [Fact]
    public void PurelyStylisticRephrase_PreservesEverything_SafeToUse()
    {
        var findings = MeaningPreservationValidator.Evaluate(
            "I would like to schedule a meeting for tomorrow.",
            "I would like to arrange a meeting for tomorrow.", null);
        Assert.Equal(ValidationOutcome.SafeToUse, MeaningPreservationValidator.Classify(findings));
    }

    // ---- Negation preservation ----
    [Fact]
    public void NegationDropped_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate("I did not say that.", "I did say that.", null);
        Assert.False(findings.NegationPreserved);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
        Assert.Contains("negation", findings.FailureReason);
    }

    [Fact]
    public void NegationPreserved_German_SafeToUse()
    {
        var findings = MeaningPreservationValidator.Evaluate("Es gibt keinen Grund zur Sorge.", "Es besteht kein Grund zur Sorge.", null);
        Assert.True(findings.NegationPreserved);
    }

    // ---- Number preservation ----
    [Fact]
    public void NumberChanged_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate("We gained 42 new customers.", "We gained 43 new customers.", null);
        Assert.False(findings.NumbersPreserved);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void NumberDropped_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate("The total came to 1250 euros.", "The total came to a lot of euros.", null);
        Assert.False(findings.NumbersPreserved);
    }

    [Fact]
    public void NumberPreservedAcrossRephrase_SafeToUse()
    {
        var findings = MeaningPreservationValidator.Evaluate("We gained 42 new customers.", "42 new customers joined us.", null);
        Assert.True(findings.NumbersPreserved);
    }

    // ---- Date preservation ----
    [Fact]
    public void DateDropped_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate("The meeting is planned for March 14th.", "The meeting is planned soon.", null);
        Assert.False(findings.DatesPreserved);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void DatePreserved_DifferentFormat_StillDetectedViaMonthName()
    {
        var findings = MeaningPreservationValidator.Evaluate("The deadline is October 3rd.", "The deadline falls on October 3rd.", null);
        Assert.True(findings.DatesPreserved);
    }

    // ---- Name/entity preservation ----
    [Fact]
    public void NameDropped_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate(
            "Mr. Müller prepared the presentation for Ms. Schmidt.",
            "The presentation was prepared for Ms. Schmidt.", null);
        Assert.False(findings.NamesPreserved);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void NamesPreserved_SafeToUse()
    {
        var findings = MeaningPreservationValidator.Evaluate(
            "Sarah introduced Michael to the new team lead.",
            "Sarah introduced Michael to the team's new lead.", null);
        Assert.True(findings.NamesPreserved);
    }

    // ---- Terminology preservation ----
    [Fact]
    public void RequiredTerminologyMissing_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate(
            "We need to reduce the latency of the API.",
            "We need to make the service respond faster.", new[] { "API", "latency" });
        Assert.False(findings.TerminologyPreserved);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void RequiredTerminologyPreserved_SafeToUse()
    {
        var findings = MeaningPreservationValidator.Evaluate(
            "We need to reduce the latency of the API.",
            "We need to reduce the API's latency.", new[] { "API", "latency" });
        Assert.True(findings.TerminologyPreserved);
    }

    // ---- Question vs statement preservation ----
    [Fact]
    public void QuestionTurnedIntoStatement_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate("Do you know what time the meeting starts?", "You know what time the meeting starts.", null);
        Assert.False(findings.QuestionStatementPreserved);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void QuestionFormPreserved_SafeToUse()
    {
        var findings = MeaningPreservationValidator.Evaluate("Do you know what time the meeting starts?", "Do you happen to know the meeting's start time?", null);
        Assert.True(findings.QuestionStatementPreserved);
    }

    // ---- Modality preservation ----
    [Fact]
    public void ModalityDropped_MustToDefinite_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate("We must sign the contract by Friday.", "We sign the contract by Friday.", null);
        Assert.False(findings.ModalityPreserved);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void ModalityPreserved_DifferentModalWord_StillCountedAsPresent()
    {
        // "would" -> "could": both are in the modal vocabulary, so the PRESENCE count (1==1) still matches —
        // a documented limitation (degree/certainty shift within the modal category is not detected).
        var findings = MeaningPreservationValidator.Evaluate("I would like to schedule a meeting.", "I could schedule a meeting.", null);
        Assert.True(findings.ModalityPreserved);
    }

    // ---- Qualifier preservation ----
    [Fact]
    public void QualifierDropped_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate("That will probably work.", "That will work.", null);
        Assert.False(findings.QualifiersPreserved);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    // ---- Token coverage ----
    [Fact]
    public void CandidateUnrelatedToBaseline_LowTokenOverlap_Rejected()
    {
        var findings = MeaningPreservationValidator.Evaluate(
            "We need to sign the contract by Friday.",
            "The weather in Berlin is quite pleasant this time of year.", null);
        Assert.False(findings.TokenCoverageOk);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void EmptyBaselineAndCandidate_TreatedAsCoverageOk()
    {
        var findings = MeaningPreservationValidator.Evaluate("", "", null);
        Assert.True(findings.TokenCoverageOk);
    }

    [Fact]
    public void EmptyCandidateOnly_CoverageFails()
    {
        var findings = MeaningPreservationValidator.Evaluate("We need to sign the contract.", "", null);
        Assert.False(findings.TokenCoverageOk);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    // ==== Step 5.14B regression tests — fixes for the two false-positives discovered
    // live during Step 5.14A (docs/translation-naturalization-gemini-experiment.md §12b/§12c) ====

    // ---- Fix 1: terminology check is now direction-aware (baseline-presence-gated) ----
    [Fact]
    public void TerminologyTerm_ForTheOtherLanguageDirection_NoLongerCausesFalseRejection()
    {
        // Reproduces Step 5.14A case D1 exactly: a corpus entry supplies BOTH the German
        // and English spelling of one term for a de->en case. The baseline (and any
        // legitimate candidate) is English, so the German spelling can never appear —
        // before the fix, this made the case reject ANY candidate, including a perfect one.
        var findings = MeaningPreservationValidator.Evaluate(
            "The server throws a null pointer exception in the production environment.",
            "The server is throwing a null pointer exception in the production environment.",
            new[] { "Nullzeiger-Ausnahme", "null pointer exception" });

        Assert.True(findings.TerminologyPreserved);
        Assert.Equal(ValidationOutcome.SafeToUse, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void TerminologyTerm_ActuallyPresentInBaseline_StillEnforced_GenuineDropStillRejected()
    {
        // The fix must not become a loophole: a term that DOES appear in the baseline
        // (for this case's actual direction) must still be required in the candidate.
        var findings = MeaningPreservationValidator.Evaluate(
            "We need to reduce the latency of the API.",
            "We need to make the service respond faster.",
            new[] { "API", "latency", "Nullzeiger-Ausnahme" }); // last term irrelevant/absent from baseline — must not matter

        Assert.False(findings.TerminologyPreserved);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void TerminologyTerm_NeitherLanguageSpellingInBaseline_BothIgnored()
    {
        // If a supplied term doesn't appear in the baseline in EITHER spelling (e.g. a
        // corpus authoring mistake, or a term genuinely irrelevant to this segment), it
        // must not cause a rejection either way.
        var findings = MeaningPreservationValidator.Evaluate(
            "The meeting is at three.", "The meeting's at three.",
            new[] { "API", "Datenbank" });
        Assert.True(findings.TerminologyPreserved);
    }

    // ---- Fix 2: token coverage is now contraction-aware (comparison-only) ----
    [Fact]
    public void GermanColloquialContraction_ExactStep514ACase_NoLongerFalselyRejected()
    {
        // Reproduces Step 5.14A case A2 exactly: baseline "Mir geht es gut, danke der
        // Nachfrage." vs Gemini's candidate "Mir geht's gut, danke fürs Nachfragen." —
        // a genuinely natural, meaning-identical German contraction that previously fell
        // below the 40% token-overlap threshold (30% raw) purely due to contraction.
        var findings = MeaningPreservationValidator.Evaluate(
            "Mir geht es gut, danke der Nachfrage.",
            "Mir geht's gut, danke fürs Nachfragen.", null);

        Assert.True(findings.TokenCoverageOk);
    }

    [Fact]
    public void EnglishColloquialContraction_NoLongerFalselyRejected()
    {
        // "It's" expands to "it is" for comparison, directly matching the baseline's "It is".
        var findings = MeaningPreservationValidator.Evaluate(
            "It is going to rain today, I believe.",
            "It's going to rain today, I believe.", null);
        Assert.True(findings.TokenCoverageOk);
    }

    [Fact]
    public void ContractionExpansion_DoesNotWeakenDetectionOfAGenuinelyDifferentSentence()
    {
        // The fix must not become a general loophole: a candidate that is genuinely
        // unrelated to the baseline must still fail coverage, contraction or not.
        var findings = MeaningPreservationValidator.Evaluate(
            "It's going to rain today.",
            "The stock market closed higher on Friday.", null);
        Assert.False(findings.TokenCoverageOk);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    [Fact]
    public void ContractionExpansion_OnlyAffectsCoverageCheck_NotNegationOrOtherChecks()
    {
        // Negation, numbers, dates, names, modality, and qualifiers all compare RAW
        // tokens/regex matches — confirm this fix (scoped to TokenCoverageOk only) leaves
        // a genuine negation-count mismatch detected exactly as before, even when the
        // sentence also contains a contraction the coverage check now tolerates.
        var findings = MeaningPreservationValidator.Evaluate("It is not going to rain today.", "It's going to rain today.", null);
        Assert.False(findings.NegationPreserved); // "not" dropped — still caught, contraction-awareness didn't mask it
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
    }

    // ---- Multiple simultaneous failures still report ONE clear, prioritized reason ----
    [Fact]
    public void MultipleFailures_ReportsFirstDetectedReason_NeverNull()
    {
        var findings = MeaningPreservationValidator.Evaluate("I did not say that. There were 42 of them.", "Completely unrelated text about something else entirely different.", null);
        Assert.Equal(ValidationOutcome.Rejected, MeaningPreservationValidator.Classify(findings));
        Assert.NotNull(findings.FailureReason);
    }
}
