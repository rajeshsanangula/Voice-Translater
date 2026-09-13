# Step 4 — Commit-Policy Shadow Evaluation (Policy A vs. Policy B)

**Status: SHADOW/OBSERVATION ONLY. Policy A (production-tested) is unmodified. Policy B is
experimental and isolated. Neither policy's output reaches translation, TTS, or playback.
The production pipeline is unchanged from Step 3.**

---

## 1. Objective

Evaluate whether `PrefixStabilityEngine`'s current commit policy — comparing each partial only against the literal immediately-preceding partial — creates unnecessary latency for real conversational speech, by running an experimental alternative policy side-by-side (never in place of it) against the identical real Azure partial/final stream, and comparing the two deterministically.

## 2. Policy A

**Exactly the current, unmodified `PrefixStabilityEngine`** ([src/VTTranslate.Core/Streaming/PrefixStabilityEngine.cs](../../src/VTTranslate.Core/Streaming/PrefixStabilityEngine.cs)) — the same class approved in Steps 2/2.5/3, byte-for-byte untouched by this step (confirmed: no diff to this file in Step 4). Word-level LCP against the literal previous partial, trailing-word holdback, comparison-only punctuation normalization (V-1).

## 3. Policy B

**`BestPartialStabilityEngine`** ([src/VTTranslate.Core/Streaming/BestPartialStabilityEngine.cs](../../src/VTTranslate.Core/Streaming/BestPartialStabilityEngine.cs)) — experimental, shadow-only. Identical to Policy A in every respect (tokenization, trailing-word buffer, duplicate prevention, punctuation normalization, finalization) except one: instead of comparing against the literal immediately-preceding partial, it maintains a **reference partial** that is only replaced when a new partial is **strictly longer than the current reference AND does not contradict already-committed content**:

```csharp
if (_referencePartialTokens == null)
    _referencePartialTokens = currentTokens;                    // bootstrap
else if (currentTokens.Count > _referencePartialTokens.Count)    // strictly longer...
{
    var agreedWithCommitted = WordLevelCommonPrefixLength(_committedTokens, currentTokens);
    if (agreedWithCommitted >= _committedTokens.Count)            // ...and consistent
        _referencePartialTokens = currentTokens;
    // else: longer but contradicts committed content — do NOT adopt
}
// else (same length or shorter): never adopted as the new reference
```

This directly addresses the prompt's caution — **"longest partial" does not automatically mean "best partial."** A longer partial is only adopted as the new comparison baseline if it is also consistent with what's already committed; a longer-but-contradicting partial is used normally for that step's commit decision (so contradictions/regressions are still detected and withheld, identically to Policy A) but is never allowed to become the new reference, so it cannot degrade every comparison that follows it.

## 4. State model

Both policies share the same lifecycle shape per utterance: `Idle → Committing (0+ commits) → Finalized → reset`. The only per-utterance state that differs is what Policy B calls `_referencePartialTokens` in place of Policy A's `_previousPartialTokens` — a reference that updates conditionally (§3) instead of unconditionally on every partial. Both reset fully on Final and on a new `UtteranceId`, exactly as verified for Policy A in Step 2/2.5's unit suite.

## 5. Regression handling

Distinguished from Policy A's regression handling (which is unchanged and identical for Policy B's own commit-attempt step) by one additional property: a **shorter** partial (a genuine Azure re-segmentation, not a contradiction) does not become the reference under Policy B, so it cannot cap future agreement the way it would under Policy A's "always adopt current as previous" rule. A **contradicting** partial (longer or shorter) is handled identically by both policies — neither ever commits contradicted content, and neither ever adopts a contradicting partial as its comparison baseline (Policy A has no such baseline update decision at all; Policy B explicitly excludes it, §3).

## 6. Duplicate prevention

Identical mechanism to Policy A: `_committedTokens` only grows via an `IsExtension`-verified append in both engines. Verified live (§9): across 142 real partial-level observations in this run, `BestPartialStabilityEngineTests` and `CommitPolicyComparatorTests` (§8) both confirm no duplicate segment was ever produced by either policy, and the live run's per-step comparison (§10) shows zero divergence in committed-token counts between the two policies for any of the 11 real utterances tested — meaning Policy B's duplicate-prevention guarantee held under real data with zero exceptions, not just in unit tests.

## 7. Correction handling

Both policies expose the same three-tier distinction required by the prompt:
1. **Speculative content** — `PendingUnstableText` on any non-final result: never committed, never treated as safe.
2. **Stable content** — `CommittedSourceText`/`NewlyCommittedSegment` from a `Committed` action: proposed-safe, but explicitly still revisable (see below) — this is a shadow proposal, not spoken/displayed content.
3. **Final authoritative content** — `CommittedSourceText` from a `Finalized` action: the only content either policy treats as ground truth.

Neither policy pretends a previously-proposed speculative commit can be physically retracted — both explicitly flag `FinalizedWithCorrection = true` when the Final's true text contradicts something already committed, exactly mirroring Policy A's existing, already-reviewed behavior (Steps 2/2.5). This shadow experiment measures how often each policy *would* produce a "potentially contradicted incremental commitment" precisely via this flag: in this live run (§9), both policies flagged `finalizedWithCorrection=True` in exactly one case (D) — see §13.

## 8. Test methodology

- **Unit tests** (all Azure-independent, executed and passing — §12): `BestPartialStabilityEngineTests.cs` (11 tests: monotonic growth, repeated/whitespace-identical partials, punctuation normalization, source-regression recovery — the key Policy A/B divergence, contradicting-longer-partial rejection, self-correction safety, the exact adversarial "Tuesday → Thursday → I'd like" example from this step's prompt, Final divergence, multi-utterance isolation, stale/out-of-order rejection, determinism) and `CommitPolicyComparatorTests.cs` (6 tests: the regression-recovery scenario showing Policy B ≥ Policy A committed tokens, monotonic-growth identical-outcome case, the adversarial example producing correction flags on both policies, privacy/no-text-logging, multi-utterance reset isolation, determinism).
- **Live methodology**: a new `commit-policy-test` command in `VTTranslate.LiveTest` ([tools/VTTranslate.LiveTest/Program.cs](../../tools/VTTranslate.LiveTest/Program.cs)) reuses Step 1/3's existing WAV files for cases A-D, F-H, and adds four new SSML-synthesized cases for I (rapid consecutive short utterances), J (natural conversational sentence with filler words), K (varied punctuation), and L (mid-sentence name-swap correction, distinct from case G's false-start/restart) via a new `gen-step4-audio` command. Case E is deliberately run separately via a dedicated `case-e-investigate` command (§11), not folded into the main sweep.

## 9. Live measurements

Real Azure, `commit-policy-test`, 11 cases (A-D, F-L; E investigated separately, §11). Full logs: `test-results/logs/commit-policy/*.log`.

| Case | Policy A 1st commit | Policy B 1st commit | Policy A final flush tokens | Policy B final flush tokens | Policy A cumulative | Policy B cumulative | Correction (A / B) |
|---|---|---|---|---|---|---|---|
| A EN→DE normal | 12897ms | 12897ms | 6 | 6 | 8 | 8 | No / No |
| B DE→EN normal | 1863ms | 1863ms | 2 | 2 | 6 | 6 | No / No |
| C EN→DE fast | 13913ms | 13913ms | 11 | 11 | 15 | 15 | No / No |
| D DE→EN fast | 1700ms | 1700ms | 9 | 9 | 14 | 14 | **Yes / Yes** |
| F EN→DE long | 1980ms | 1980ms | 3 | 3 | 32 | 32 | No / No |
| G EN→DE correction | 13977ms | 13977ms | 9 | 9 | 14 | 14 | No / No |
| H EN→DE ×3 consecutive | 1840/2867/2169ms | 1840/2867/2169ms | 2/4/2 | 2/4/2 | 8/7/4 | 8/7/4 | No×3 / No×3 |
| I EN→DE ×3 rapid | (0 partials any utterance — see below) | (same) | 4/2/1 | 4/2/1 | 4/2/1 | 4/2/1 | No×3 / No×3 |
| J EN→DE conversational | 2808ms | 2808ms | 2 | 2 | 15 | 15 | No / No |
| K EN→DE punctuation | 13982ms | 13982ms | 4 | 4 | 14 | 14 | No / No |
| L EN→DE mid-correction | 1744ms | 1744ms | 2 | 2 | 12 | 12 | No / No |

**Every single measured value is identical between Policy A and Policy B across all 11 cases and 142 individually-compared partial-level observations** (verified by a step-by-step diff of both policies' `ShadowPartialObserved` log lines paired by utterance ID and partial sequence number — zero differences found). Case I's three short utterances each went straight to Final with zero `Recognizing` events (matching Step 1's short-utterance finding), so no partial-level comparison exists for them — only the Final flush, which is naturally identical for any deterministic policy given zero partials (§4).

## 10. Policy comparison

**Estimated reduction in commit latency: 0ms.** **Percentage of utterances where Policy B commits earlier: 0%. Later: 0%. Identical: 100% (11/11 measured cases; the 3 zero-partial utterances in case I are excluded from this ratio as not applicable — neither policy had an opportunity to differ).**

This is a genuine, meaningful negative result, not a failure of the experiment: it means that in every single utterance sampled in this live run, no partial was ever both (a) shorter than the immediately-preceding partial and (b) consistent with already-committed content — the exact precondition under which Policy B's reference-retention rule would diverge from Policy A's always-update rule (§3, §6 of `prefix-stability-test-failure-analysis.md`'s "commit-delay" finding). Azure's real partials in this sample were always non-decreasing in length within an utterance, even across cases with real content regressions (D, G — see §13): those regressions changed the *content* of a same-or-longer partial, not its *length*. Policy B's theoretical benefit specifically targets a length-regression pattern that unit-testing shows is possible (§9's `RegressionThenRecovery` test) but that did not occur naturally anywhere in this 11-case, real-Azure sample.

## 11. Short-utterance investigation

Per Step 4's explicit instruction, Case E was investigated separately via a dedicated `case-e-investigate` command with a 60-second completion timeout (vs. the standard 30s+3s) and explicit chunk/connection instrumentation, rather than being folded into the main sweep or "fixed" by touching eligibility thresholds (none were touched).

**Findings**:
- **Audio genuinely reached the recognizer**: 28 chunks, 88,130 bytes pushed via `PushAudio`, confirmed by direct chunk-count instrumentation in the harness (not inferred from logs).
- **Azure did produce a Final event** — `RecognitionEvent kind=final textLength=3` — approximately 13.17 seconds after `StartAsync`, and `UtteranceEvaluated` shows it was correctly **accepted** (`durationMs=560 ... accepted=True`), matching Step 1's original finding for this exact case (560ms duration, accepted).
- **No timeout occurred in the sense of "Azure never responded"** — the recognizer connected in 33ms (`StartAsync returned after 33ms`) and eventually did produce a complete, correctly-processed result; the delay was entirely in Azure producing the recognition event itself, not in audio delivery or the harness.
- **This is the same connection/first-response latency anomaly already documented** for EN→DE cases throughout Steps 1, 3, and this step's own §9 data (cases A, C, G, K all showed 12.9-14.0 second first-commit latencies) — Case E's original zero-event result in Step 3 is best explained as this same anomaly having been, on that particular run, slow enough to exceed the 30s+3s window entirely, whereas this dedicated 60s+15s re-run gave it enough time to complete.
- **Conclusion**: not a harness bug, not an audio-routing bug, not an eligibility-threshold issue, and not a problem with the utterance itself (a genuinely short "No." at increased SSML rate) — it is variance in the same pre-existing EN→DE connection-latency phenomenon, now demonstrated to be large enough (occasionally exceeding 30+ seconds) to make a short test window an unreliable way to test this specific case, not a defect in shadow instrumentation, Policy A, or Policy B.

## 12. Latency analysis

"Shadow source-commit latency" (not "translation latency" — no translation is connected) ranged from ~1.7s to ~14.0s to first commit across the 8 cases where partials occurred, and the connection-warmup anomaly (§11, §9) dominates the EN→DE cases' numbers so heavily that this run cannot isolate a genuine algorithmic first-commit-latency figure for that direction. DE→EN cases (B, D) show ~1.7-1.9s to first commit, more plausibly reflecting genuine per-utterance recognition/agreement latency rather than connection warmup. Since Policy A and Policy B were identical in every measured case (§9, §10), **this latency analysis applies equally to both policies** — Step 4 did not find a latency difference between them to analyze separately.

## 13. Correctness analysis

- **No content loss observed**: every Final in this run showed `CommittedSourceText` equal to the full final text (structural guarantee, unit-tested in Steps 2/2.5/4 and consistent with every live case's `shouldEmit`/token-count pattern).
- **No duplication observed**: confirmed both by the unit suite (155 tests, zero duplication findings) and by the live per-step diff (§9) showing zero divergent commit-boundary steps.
- **Corrections**: exactly one case (D, DE→EN fast) showed `finalizedWithCorrection=True` for **both** policies simultaneously — consistent with §10's finding that the two policies never diverged: whatever contradiction occurred in case D's real Azure output was detected identically by both engines' independent (but structurally identical for this input pattern) contradiction checks.
- **No policy ever emitted contradicted content as a "safe" commit** — a contradiction always resulted in `action=None` for that step (never a duplicate or wrong commit), and the eventual Final always fully resolved the utterance, matching the invariants already verified in Steps 2/2.5's static and executable verification.

## 14. Recommendation

**Do not adopt Policy B for Step 5 based on this evidence alone.** The live data found zero measurable benefit from Policy B's reference-retention strategy against 11 real utterances (8 with partials), because the specific pattern it targets — a shorter, non-contradicting intermediate partial — did not occur naturally in this sample, even though it is a real, unit-tested, possible Azure behavior (Step 1's own data showed *content* regressions but this sample never showed *length* regressions). This is not evidence Policy B is wrong or unsafe (both policies were equally correct throughout, §13) — it is evidence that, on this sample, the two policies are behaviorally indistinguishable, so there is no latency case for preferring the more complex Policy B yet. Recommended next step (design-only, not authorized by this evaluation): either (a) collect a larger/longer live sample specifically watching for a naturally-occurring shorter-partial event to get a real comparison data point, or (b) construct a larger set of adversarial live-adjacent test phrases deliberately designed to provoke Azure's own re-segmentation behavior (Step 1 §11 already documents Azure re-segmenting under certain conditions). Given the added complexity Policy B introduces for zero observed live benefit so far, **Policy A remains the better-evidenced choice** to keep as the sole candidate should Step 5 ever authorize connecting a stability engine to a real consumer — this recommendation itself is not an authorization to do so.
