# Step 2 — Offline Prefix-Stability Engine

**Status: implemented, unit-tested, and fully isolated. NOT connected to
production translation/TTS. `AzureSpeechTranslationProvider.cs` and
`DirectionPipeline.cs` were not modified to produce this deliverable.**

Files: `src/VTTranslate.Core/Streaming/StabilityModels.cs`,
`IStreamingStabilityEngine.cs`, `PrefixStabilityEngine.cs`.
Tests: `tests/VTTranslate.Core.Tests/PrefixStabilityEngineTests.cs`.

---

## 1. Problem

Azure's `TranslationRecognizer` emits a growing sequence of `Recognizing`
(partial) events before one authoritative `Recognized` (final) event per
utterance. A future streaming pipeline wants to translate/synthesize *before*
the Final arrives, to cut perceived latency. But partials are not safe to act
on as-is: source text can regress mid-utterance (confirmed in Step 1, §4/§9 of
`streaming-measurement-results.md`), and translated partial text regresses far
more often than source text. Acting on every partial would produce
constantly-changing, contradictory audio.

This engine answers one narrow question, offline and deterministically: given
a sequence of partial/final SOURCE-text events for one utterance, which
prefix of that text is stable enough to be committed (i.e., safe for a future
consumer to translate/synthesize incrementally)? It makes no translation,
audio, or network decisions itself.

## 2. Why source partials, not translated partials

Step 1's real Azure data (`streaming-measurement-results.md` §4) showed:
`translatedPartialMissingCount` was 0 (translated text is always present on
partials), but translated text regresses far more often than source text —
most partials showed `translatedIsPrefixExtension=False` even while the
paired source text was a clean prefix extension. Trusting translated-partial
agreement as a stability signal would therefore commit far less, far later,
or on unstable content. The engine reads `SourceText` only;
`TranslatedTextForDiagnosticsOnly` is carried on the event record for a
future consumer's own diagnostics but is never inspected for any decision.

## 3. Algorithm

Per `Recognizing` (partial) event, for the current utterance:

1. Tokenize the current and immediately-preceding partial's source text
   (whitespace-split; punctuation stays attached to its word token).
2. Compute the word-level longest common prefix (LCP) length between the two
   token lists (ordinal comparison).
3. Hold back the last agreed word — `committableLength = agreedLength - 1`.
   The boundary word is the one most likely to still be revised by the next
   partial, so it is deliberately never trusted yet.
4. If `committableLength` exceeds what's already committed, and the
   candidate prefix is a true extension of the already-committed tokens
   (not a contradiction), commit the new words and report them as
   `NewlyCommittedSegment`.
5. If the candidate contradicts already-committed tokens, commit nothing —
   see §6.

`Recognized` (Final) is always authoritative: whatever portion of the final
text isn't yet committed (by word-level LCP against the committed prefix) is
force-emitted as the last segment, and the engine resets for the next
utterance.

## 4. State machine

Per utterance ID (tracked in `_currentUtteranceId`):

```
(new utterance ID seen, on either ProcessPartial or ProcessFinal) -> Reset -> Idle
Idle / Committing --[ProcessPartial, sequence advances, prefix grows]--> Committing (emits segment)
Idle / Committing --[ProcessPartial, sequence advances, no committable growth]--> Committing (no emission)
Idle / Committing --[ProcessPartial, sequence <= last processed]--> unchanged (rejected, no state change)
Idle / Committing --[ProcessFinal]--> Finalized -> Reset -> Idle
```

A change in `UtteranceId` on *either* method is treated as an implicit reset
before processing — the caller does not need to call `Reset()` explicitly
between utterances (though it may, e.g. on session teardown).

## 5. Commit rules

- Commits are strictly additive within an utterance: `_committedTokens` only
  grows (via `IsExtension` check) or stays the same; it is never truncated
  by a partial.
- The trailing-word buffer (§3 step 3) means a single-word gain between two
  partials never commits anything new — at least two words of agreement
  growth are needed before the first new word can commit, and the true
  minimum is "agreement growth of 2" for the first ever commit (since one
  word must always stay held back).
- `CommitVersion` increments once per non-null `NewlyCommittedSegment`
  (including the Final's forced completion, if it commits new words), reset
  to 0 per utterance — a cheap monotonic marker for a future consumer to
  detect "something new to act on" without string-diffing.

## 6. Regression handling

If a later partial's committable candidate does not start with the exact
sequence of already-committed tokens (a contradiction — e.g. Step 1's
observed long-utterance self-corrections), the engine:

- Does **not** retract the already-committed tokens.
- Does **not** emit anything for that partial.
- Keeps waiting; a later partial may re-agree with the committed prefix (in
  which case normal committing resumes), or the utterance may end with a
  Final that overrides the contradiction (§7).

This is a deliberate correctness/latency trade-off, not a bug: it means a
downstream consumer that already spoke/displayed a committed segment could
turn out to have been wrong, exactly as `streaming-conversational-engine-design.md`
already documents as an accepted trade-off of streaming translation in
general. The engine does not hide this — `FinalizedWithCorrection` on the
Final result flags exactly this situation when it happens.

## 7. Finalization

`ProcessFinal` computes the word-level LCP between `_committedTokens` and the
final source text. Everything in the final text beyond that agreed prefix is
emitted as one last `NewlyCommittedSegment`, regardless of whether it agrees
with or contradicts previously committed content — the Final's text is always
authoritative and always fully accounted for in `CommittedSourceText`. If the
LCP is shorter than what was already committed (i.e. a previously committed
word doesn't appear in the final text at that position), `FinalizedWithCorrection`
is set to `true` on the result — a signal, not a rollback (already-emitted
audio/text cannot literally be un-emitted). The engine resets its internal
state immediately after building the Final result, ready for the next
utterance.

A Final with no preceding partials for its utterance ID (the real, Step-1-observed
"straight to Final, zero `Recognizing` events" case for short utterances) is
handled the same way: `_committedTokens` starts empty, so the entire final
text is emitted as the one committed segment.

## 8. Duplicate prevention

- Committed tokens only ever grow via `IsExtension`-checked appends within an
  utterance — the same word range can never be committed twice, and
  `NewlyCommittedSegment` always contains only the tokens added since the
  previous commit.
- Stale/out-of-order partials (`SequenceNumber <= _lastProcessedSequence`)
  are rejected outright — no state change, no emission — so a re-delivered
  or delayed partial can never re-trigger a commit or corrupt
  `_previousPartialTokens`.
- Empty/whitespace-only partials are ignored without touching
  `_previousPartialTokens`, so a transient empty partial can't reset the
  prefix-comparison baseline and cause a false "regression."
- A new `UtteranceId` always resets state before processing, so no token
  state can leak between utterances.

## 8a. Comparison-only punctuation normalization (V-1 fix)

Stability decisions (`WordLevelCommonPrefixLength`, `IsExtension`) compare
tokens with trailing punctuation (`. , ! ? ; :`) stripped, via a private
`TrimTrailingPunctuation`/`TokensMatchForComparison` helper — so a committed
token like `"would"` and a later `"would,"` for the same word compare equal
for the purpose of deciding stability/contradiction. **This normalization is
used only inside the comparison functions.** Every emitted value
(`NewlyCommittedSegment`, `CommittedSourceText`, `PendingUnstableText`) is
still built from the original tokens, verbatim — punctuation is never
stripped, added, or altered in anything the engine emits. It is not semantic
normalization: no lowercasing, stemming, or fuzzy matching is performed, so a
genuine lexical change (e.g. `"Tuesday"` vs `"Thursday"`) still compares
unequal exactly as before and is still treated as a real correction. See
`docs/design-notes/prefix-stability-verification.md` §"V-1 resolution" for
the case traces that motivated and validate this fix.

## 9. Known limitations

- **Word-level, not character-level.** A partial that revises a single
  character within an already-agreed word (e.g. a typo-like correction) is
  not specially detected; the whole-word comparison treats the word as
  either matching or not. This matches the design doc's original word-level
  approach and Step 1's data (which analyzed source stability at the word
  level).
- **No confidence signal used** — consistent with Step 1's confirmed finding
  that Azure never populates confidence for `TranslationRecognizer` in
  practice; the engine has no confidence-dependent branch at all.
- **No timing/latency optimization** — per this phase's explicit instruction
  (correctness over low latency), the trailing-word buffer and 2-consecutive-
  partial-equivalent gate are deliberately conservative. A future step could
  tune commit aggressiveness against measured mis-commit rates; this phase
  does not attempt that.
- **A committed-then-contradicted segment is never un-committed mid-utterance**
  (§6) — if a future consumer has already translated/spoken a segment that a
  later partial contradicts, that output cannot be retracted by this engine;
  only `FinalizedWithCorrection` flags that it happened, for the consumer to
  handle (e.g. a spoken correction) in a future step. This is the single
  largest known correctness caveat and mirrors the accepted trade-off already
  documented for streaming translation generally.
- **Tokenization is whitespace-only** — punctuation stays attached to its
  word (e.g. `"meeting."` vs `"meeting"` are different literal tokens, and
  emitted text always preserves whichever exact form the source event
  carried). As of the V-1 fix (§8a), a trailing-punctuation-only difference
  between an already-committed token and a later partial/Final's
  corresponding token no longer causes a false "contradiction" — comparisons
  are punctuation-insensitive, emission is not. What remains a simplification
  (not a defect) is *which* revision's exact punctuation ends up in the
  emitted text — that's still whichever form was present at the moment the
  word was actually committed or finalized, so downstream punctuation timing
  is still coarse-grained, just no longer capable of triggering a spurious
  correction.

## 10. Examples

**Simple growing prefix** (mirrors design doc's canonical example and test
`A_SimpleGrowingPrefix...`): partials `"I"` → `"I would"` → `"I would like"` →
`"I would like to schedule"` → `"I would like to schedule a"` → Final
`"I would like to schedule a meeting."` The engine commits incrementally,
always holding back the trailing word, and the Final force-completes the
remaining tail (`"a meeting."` or similar, depending on exact partial
timing) — the full reconstructed text matches the Final exactly, with no
duplication.

**Self-correction** (mirrors design doc's "Tuesday" → "Thursday" example):
partial `"Let's meet on Tuesday"` followed by `"Let's meet on Thursday"` —
`"Tuesday"`/`"Thursday"` is the last word of the first partial, so it was
already held back by the trailing-word buffer and never committed; the
correction is absorbed with no contradiction and no incorrect commit.

**Short utterance, no partials** (Step-1-observed, test `I`/`O`): a Final
arrives with zero prior partials. `_committedTokens` is empty;
`ProcessFinal` emits the entire final text as one segment.

## 11. Test coverage

`tests/VTTranslate.Core.Tests/PrefixStabilityEngineTests.cs` — approximately
25 `[Fact]` tests:

- Scenarios A–R (named per this phase's spec): growing prefix, rapid-timing
  independence, regression handling, self-correction, repeated identical
  partials, empty partials, whitespace-only variation, punctuation
  variation, very-short-utterance-straight-to-Final, long sentences, fast
  conversational pacing, multiple consecutive utterances (isolation), Final
  immediately after one partial, Final with much more text than the latest
  partial, Final with no prior partials, committed prefix reappearing in
  Final (no duplicate), regression after commit (no retraction/duplication),
  stale out-of-order partial rejection.
- Explicit "critical safety property" tests: no duplicate source segment
  across a full utterance lifecycle; no lost Final content even under heavy
  regression; unstable text can remain pending (not force-committed); Final
  always flushes all pending content; `Reset()` fully isolates utterances.
- Hand-rolled, fixed-seed randomized "property-style" tests (plain xUnit
  `[Fact]` + `System.Random` with fixed seeds 42 and 7 — no new test
  framework/dependency added): 50 trials asserting randomized growing
  partial sequences never duplicate or lose words versus the expected final
  text; 20 trials asserting `Reset()` always returns to a verifiably clean
  initial state.

**Test execution status**: this environment has Windows Defender Application
Control (WDAC) user-mode code integrity enforcement active
(`UsermodeCodeIntegrityPolicyEnforcementStatus: 2`, confirmed via
`Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard`),
which is currently blocking the `dotnet test` vstest host from loading the
freshly-built `VTTranslate.Core.dll` (`FileLoadException ... An Application
Control policy has blocked this file. (0x800711C7)`), even after a full clean
rebuild. This is an environmental/OS security policy, not a code defect —
the same class of block has occurred intermittently elsewhere in this
project's session history. Per this project's standing constraint, no bypass
of this Windows security control was attempted. The test file has been
manually traced against the algorithm (test A's exact scenario) and reasoned
through for every listed scenario; a genuine passing automated run could not
be obtained in this session and is not being claimed.

## 12. Future integration boundary

This engine is intentionally not wired into `AzureSpeechTranslationProvider`
or `DirectionPipeline`. A future Step 3 (not started, requires separate
approval) would need to: feed real `Recognizing`/`Recognized` events as
`PartialSourceEvent`/`FinalSourceEvent` (with a real per-utterance ID and
monotonic sequence number — not yet defined in production code), decide what
a future consumer does with `NewlyCommittedSegment` (translate + synthesize
incrementally), and decide how `FinalizedWithCorrection` should be
surfaced/handled downstream (§9). None of that is implemented or connected
here.
