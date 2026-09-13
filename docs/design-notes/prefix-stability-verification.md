# Prefix-Stability Engine — Static + Deterministic Verification Review

**Status: review only. No production behavior changed. `PrefixStabilityEngine.cs`,
`IStreamingStabilityEngine.cs`, and `StabilityModels.cs` are unmodified by this
document — this is analysis, not implementation.**

---

## 1. Verification scope

Static, manual (non-executed) trace-based review of:
- `src/VTTranslate.Core/Streaming/StabilityModels.cs`
- `src/VTTranslate.Core/Streaming/IStreamingStabilityEngine.cs`
- `src/VTTranslate.Core/Streaming/PrefixStabilityEngine.cs`
- `tests/VTTranslate.Core.Tests/PrefixStabilityEngineTests.cs`
- `docs/design-notes/prefix-stability-engine.md`
- `docs/design-notes/streaming-conversational-engine-design.md`

**Why static, not executed**: `dotnet test` on `VTTranslate.Core.Tests` is
blocked in this environment by Windows Defender Application Control (WDAC,
`UsermodeCodeIntegrityPolicyEnforcementStatus: 2`), which rejects the
freshly-built `VTTranslate.Core.dll` for the vstest test host with
`FileLoadException ... An Application Control policy has blocked this file.
(0x800711C7)`. Re-confirmed immediately before this review (see §12). No
bypass, weakening, or disabling of WDAC was attempted, per instruction. Every
trace below was carried out by hand against the actual algorithm code as
written on disk — not against intended behavior or the design doc's
description of it.

## 2. Current algorithm (as implemented, restated precisely)

`ProcessPartial(partial)`:
1. If `partial.UtteranceId` differs from the tracked utterance, reset, then adopt the new ID.
2. If `partial.SequenceNumber <= _lastProcessedSequence`, return `None` — no state touched.
3. Tokenize `partial.SourceText` on whitespace (punctuation stays attached to its word).
4. If zero tokens, return `None` — `_previousPartialTokens` is deliberately left untouched.
5. Set `_latestPartialTokens = currentTokens`.
6. If `_previousPartialTokens` is set: `agreedLength = LCP(_previousPartialTokens, currentTokens)`; `committableLength = max(0, agreedLength - 1)`.
7. If `committableLength > _committedTokens.Count`: take `currentTokens[0..committableLength)` as `candidateTokens`. If `candidateTokens` is a token-for-token extension of `_committedTokens` (`IsExtension`), commit it — `_committedTokens = candidateTokens`, emit the newly added tokens, `_commitVersion++`. Otherwise emit nothing (contradiction — see §5).
8. `_previousPartialTokens = currentTokens` unconditionally (when tokens were non-empty).

`ProcessFinal(final)`:
1. Utterance-ID reset logic identical to above.
2. Tokenize `final.SourceText`.
3. `agreedWithCommitted = LCP(_committedTokens, finalTokens)`. `correction = agreedWithCommitted < _committedTokens.Count`.
4. Emit `finalTokens[agreedWithCommitted..]` as the (possibly empty→null) final segment. `CommittedSourceText` in the result is `join(finalTokens)` — the true final text, always.
5. Reset unconditionally.

No timestamps, no `DateTime.Now`, no `Random`, and no wall-clock value of any
kind participates in any branch above — every input to every decision is
either `SequenceNumber`, tokenized text, or state built purely from prior
calls. This was verified by reading the full file; `Timestamp` fields on
`PartialSourceEvent`/`FinalSourceEvent` are stored on the records but never
read inside `PrefixStabilityEngine`.

## 3. State transitions

```
[fresh / just Reset()]
      │ UtteranceId seen for the first time on ProcessPartial or ProcessFinal
      ▼
[tracking utterance U, _committedTokens = [], _previousPartialTokens = null]
      │ ProcessPartial(seq advances, non-empty tokens)
      ├─ 1st partial for U: _previousPartialTokens set, no commit possible yet (nothing to compare against)
      ├─ 2nd+ partial, committableLength > committed.Count, IsExtension true  → commit, emit, version++
      ├─ 2nd+ partial, committableLength <= committed.Count                  → no-op (steady state / not enough new agreement)
      ├─ 2nd+ partial, committableLength > committed.Count, IsExtension false→ no-op (contradiction, §5)
      └─ seq <= _lastProcessedSequence                                       → no-op, fully rejected, no fields touched
      │ ProcessFinal(any time)
      ▼
[Finalized: emits finalTokens beyond agreedWithCommitted, sets FinalizedWithCorrection if agreedWithCommitted < committed.Count]
      │ ResetInternal() (unconditional)
      ▼
[fresh / ready for utterance U+1]
```

A change in `UtteranceId` on either method call forces the reset path before
any processing — there is no code path where two different utterance IDs can
share `_committedTokens`, `_previousPartialTokens`, `_lastProcessedSequence`,
or `_commitVersion`.

## 4. Case-by-case traces

Tokens shown as lists; `committed` = `_committedTokens` after the step;
`emit` = `NewlyCommittedSegment` for that step (`—` = none).

### Case 1 — clean growing prefix ending in a Final
Partials: "I would" → "I would like" → "I would like to" → "I would like to schedule" → "I would like to schedule a meeting"; Final = "I would like to schedule a meeting".

| step | tokens | agreedLen(prev,curr) | committable | commit? | emit | committed after |
|---|---|---|---|---|---|---|
| p1 | I,would | — (no prev) | — | no | — | [] |
| p2 | I,would,like | 2 | 1 | yes | "I" | [I] |
| p3 | I,would,like,to | 3 | 2 | yes | "would" | [I,would] |
| p4 | I,would,like,to,schedule | 4 | 3 | yes | "like" | [I,would,like] |
| p5 | I,would,like,to,schedule,a,meeting | 5 | 4 | yes | "to" | [I,would,like,to] |
| Final | (7 tokens) | agreedWithCommitted=4, correction=false | — | — | "schedule a meeting" | reset |

Reconstructed stream: `"I" + "would" + "like" + "to" + "schedule a meeting"` = exactly the final text, once each, in order. **Matches expected.**

### Case 2 — mid-utterance divergence at the very first word ("I want to book" → "... a" → "I'd like to book a")

| step | tokens | agreedLen | committable | commit? | emit | committed after |
|---|---|---|---|---|---|---|
| p1 | I,want,to,book | — | — | no | — | [] |
| p2 | I,want,to,book,a | 4 | 3 | yes | "I want to" | [I,want,to] |
| p3 | I'd,like,to,book,a | **0** (token 0 "I" ≠ "I'd") | 0 | no (0 ≤ 3) | — | [I,want,to] (unchanged) |

If a Final of `"I'd like to book a"` follows: `agreedWithCommitted = LCP([I,want,to], [I'd,like,to,book,a]) = 0` (first token differs) → `correction = true` → emits the entire final text `"I'd like to book a"`.

Reconstructed stream: `"I want to"` (from p2) + `"I'd like to book a"` (from Final) — **the incremental stream contains both the wrong early commit and the correct final text side by side.** `CommittedSourceText` on the Final result is the correct `"I'd like to book a"` (accurate), and `FinalizedWithCorrection = true` flags exactly this situation. See §6 finding V-1.

### Case 3 — trailing-word buffer absorbs a same-length self-correction ("Tuesday" → "Thursday")

| step | tokens | agreedLen | committable | commit? | emit | committed after |
|---|---|---|---|---|---|---|
| p1 | I,want,to,meet,on,Tuesday | — | — | no | — | [] |
| p2 | I,want,to,meet,on,Thursday | 5 (differs only at idx5) | 4 | yes | "I want to meet" | [I,want,to,meet] |

"on" (index 4, the boundary word) is correctly held back — never committed — so "Tuesday" is never emitted at all. If a subsequent partial or Final resolves to `"...on Thursday"`, the remaining `"on"` and `"Thursday"` are picked up cleanly with no contradiction, exactly reproducing the design doc's canonical example. **No defect — the buffer does its job here.**

### Case 4 — identical repeated partial, then growth ("I want to" → "I want to" → "I want to schedule")

| step | tokens | agreedLen | committable | commit? | emit | committed after |
|---|---|---|---|---|---|---|
| p1 | I,want,to | — | — | no | — | [] |
| p2 | I,want,to (identical to p1) | 3 | 2 | yes | "I want" | [I,want] |
| p3 | I,want,to,schedule | 3 | 2 | **no** (2 ≤ 2) | — | [I,want] (unchanged) |

`"to"` is not committed at p3 — `committableLength` (2) does not exceed `committed.Count` (2), so no commit fires even though the utterance clearly grew. This is not a duplicate-emission bug (invariant 7 holds — nothing is emitted twice, nothing is emitted for the identical-text repeat itself), but it is a real **commit-delay** effect: "to" only gets committed by the next partial (once agreement grows past 3) or by the Final. See §9.

### Case 5 — a partial with content sharing nothing with the previous partial

Suppose `_committedTokens = [I]` already, `_previousPartialTokens = [I, want, to]`, and the next partial is `[Guten, Tag]` (e.g. a transient misrecognition/language mix-up — token 0 "I" vs "Guten" differ immediately).

`agreedLength = LCP([I,want,to], [Guten,Tag]) = 0` → `committable = 0` → `0 > 1` is false → **no commit attempted, no exception, no state corruption.** `_previousPartialTokens` is still updated to `[Guten, Tag]` for the next comparison (this is correct — it always reflects the literal latest partial). Critically, `ProcessFinal` never reads `_previousPartialTokens`, only `_committedTokens` — so this wildly divergent partial cannot corrupt the eventual Final reconciliation. **No defect.**

### Case 6 — Final substantially longer than the last partial

`ProcessFinal` computes `finalTokens.Skip(agreedWithCommitted)` against `_committedTokens`, **not** against `_previousPartialTokens` or `_latestPartialTokens`. There is no length cap or truncation anywhere in this path. Regardless of how much additional content the Final carries beyond what any partial ever showed (e.g., the T0-for-consecutive-utterances artifact from Step 1, §11.1 of `streaming-measurement-results.md`, or simply a case where partials lagged far behind), the entire gap between `_committedTokens` and the Final text is emitted in one segment. **No defect — no content is ever capped or dropped.**

### Case 7 — Final shorter/different from the latest partial

Suppose `_committedTokens = [I,want,to,schedule]` (over-eagerly committed from a garden-path partial sequence), and Final = `"I want a meeting"` (`[I,want,a,meeting]`).

`agreedWithCommitted = LCP([I,want,to,schedule], [I,want,a,meeting]) = 2` (diverges at index 2: "to" vs "a") → `correction = true` (2 < 4) → emits `finalTokens.Skip(2) = "a meeting"`.

`CommittedSourceText` on the result = `"I want a meeting"` (correct, always derived from the Final's own text). Reconstructed **incremental stream**, however, if a consumer had already acted on the early commits: `"I"+"want"+"to"+"schedule"` (from partials) + `"a meeting"` (from Final) = `"I want to schedule a meeting"` — **does not match** the true final `"I want a meeting"`. Same class of issue as Case 2 — see V-1.

### Case 8 — correction after content has already been committed

Structurally identical to, and already covered by, Cases 2 and 7 above: once `_committedTokens` contains a prefix that a later partial or the Final contradicts, that prefix is never retracted; only `FinalizedWithCorrection` (Final-only) signals it happened. There is no separate code path for "correction after commit" versus "correction before commit" — the `IsExtension` check in `ProcessPartial` and the `agreedWithCommitted` check in `ProcessFinal` are the only two contradiction-detection points, and both behave as traced above regardless of how much was previously committed.

### Case 9 — very short utterance, zero partials ("Ja.")

No `ProcessPartial` call occurs. `ProcessFinal` is called directly:
- Utterance-ID check triggers `ResetInternal()` (harmless no-op if already fresh) and adopts the new ID.
- `finalTokens = ["Ja."]` (period stays attached — single token).
- `agreedWithCommitted = LCP([], ["Ja."]) = 0`. `correction = false` (0 < 0 is false).
- Emits `"Ja."` in full. Resets.

**No defect** — matches the real Step-1-observed "straight to Final, zero `Recognizing` events" behavior exactly, and the empty-`_committedTokens` path is handled without any special-casing needed.

### Case 10 — multiple consecutive utterances

`ProcessFinal` unconditionally calls `ResetInternal()` as its last action, which clears `_currentUtteranceId`, `_lastProcessedSequence` (→ -1), `_committedTokens` (→ new empty list), `_previousPartialTokens` (→ null), `_latestPartialTokens` (→ new empty list), and `_commitVersion` (→ 0). The next utterance's first call (partial or final) additionally re-triggers the `UtteranceId != _currentUtteranceId` reset path redundantly (harmless — resetting an already-reset engine is idempotent). No field is shared by reference across utterances (both `List<string>` fields are reassigned to `new List<string>()`, not `.Clear()`'d in place, so no aliasing risk either). **No defect — isolation is structurally guaranteed, not just usually true.**

## 5. Invariant analysis

1. **No duplicates** — **Holds.** `_committedTokens` only grows via `IsExtension`-verified appends (`candidateTokens.Skip(_committedTokens.Count)`); a token range, once in `NewlyCommittedSegment`, is never included in a later `NewlyCommittedSegment` from `ProcessPartial`. In `ProcessFinal`, the emitted segment is always `finalTokens.Skip(agreedWithCommitted)` — the already-agreed prefix is explicitly excluded. Traced concretely in Cases 1, 3, 9, 10.
2. **No loss on Final** — **Holds, for the aggregate/`CommittedSourceText` view.** `ProcessFinal`'s `CommittedSourceText` is always `join(finalTokens)` — the literal, complete final text, unconditionally. The remainder-since-`_committedTokens` is always emitted (Case 6). This invariant as stated ("final recognized content must be completely represented by committed segments + final emitted remainder") holds exactly for the token *ranges*, but see V-1 below for a caveat about what a downstream *consumer* would have already done with an incorrectly-committed range before the Final corrects it — content is not lost from the engine's own accounting, but a naive consumer's already-produced output could still be wrong.
3. **Monotonic commitment** — **Holds.** No code path ever removes elements from `_committedTokens` except the unconditional full reset between utterances (which is the defined utterance boundary, not a mid-utterance loss). Within one utterance, `_committedTokens` is append-only.
4. **Regression safety** — **Holds, in the narrow sense of "no duplicate/contradictory emission from the engine's own state."** A contradicting partial produces no emission at all (Case 2 step p3, Case 5) — it neither duplicates nor corrupts `_committedTokens`. However, see V-1: the *combination* of an earlier (now-known-wrong) commit and a later correct Final segment is a real content-quality concern for a consumer, even though the engine itself never contradicts its own committed state.
5. **Final authority** — **Holds.** `ProcessFinal` always computes its emission against the true final text and always overrides/reconciles regardless of what came before (Cases 2, 5, 6, 7 all confirm `_previousPartialTokens` and any mid-utterance divergence cannot prevent the Final from producing a correct `CommittedSourceText`).
6. **Reset isolation** — **Holds.** See Case 10 — every relevant field is either reset to a primitive default or reassigned to a fresh collection instance.
7. **Empty/repeated partial safety** — **Holds.** Empty/whitespace-only partials return `None` without touching `_previousPartialTokens` (verified by reading lines 57–64 of `PrefixStabilityEngine.cs`), preventing a transient empty partial from resetting the comparison baseline. Exact-duplicate-text partials with an advancing sequence number produce no new commit once steady state is reached (Case 4, p1→p2 boundary), and out-of-sequence/stale partials are rejected before any tokenizing happens at all (`SequenceNumber <= _lastProcessedSequence` check, first line of `ProcessPartial`'s substantive logic).
8. **Word-boundary safety** — **Holds, by construction.** All operations (`Take`, `Skip`, indexing, `IsExtension`) operate on `List<string>` token lists, never on raw string offsets/substrings. There is no code path capable of splitting inside a token.
9. **Commit-version correctness** — **Holds.** `_commitVersion++` occurs exactly once per non-null emission in both methods (`ProcessPartial`'s single increment site inside the `IsExtension` branch; `ProcessFinal`'s single `if (newSegment != null) _commitVersion++`), and is reset to 0 in `ResetInternal`. No path increments without emitting or emits without incrementing.
10. **Determinism** — **Holds.** Confirmed by full read of the algorithm (§2) — every decision is a pure function of `SequenceNumber`, tokenized `SourceText`, and prior calls' resulting state; no timestamp, random value, or external I/O is consulted.

## 6. Identified bugs, if any

**V-1 — RESOLVED.** A comparison-only trailing-punctuation normalization has
been implemented in `PrefixStabilityEngine.cs` (`TrimTrailingPunctuation` /
`TokensMatchForComparison`, used inside `WordLevelCommonPrefixLength` and
`IsExtension` only). Trailing `. , ! ? ; :` are stripped for equality checks
used in stability/contradiction decisions; nothing else changes — emitted
text (`NewlyCommittedSegment`, `CommittedSourceText`, `PendingUnstableText`)
is still always built from the original, untouched tokens. No lowercasing,
stemming, or fuzzy matching was introduced; a genuine lexical difference
(e.g. `"Tuesday"` vs `"Thursday"`) still compares unequal and is still a real
correction. Re-traced by hand against the original V-1 example
(`committed "would"`, `final "would,"`): `agreedWithCommitted` now includes
the `"would"`/`"would,"` position (previously it stopped one token early),
`FinalizedWithCorrection` no longer fires for this case, and no word is
re-emitted. See `prefix-stability-engine.md` §8a for the production doc
description, and the new `V1_*` tests in `PrefixStabilityEngineTests.cs` for
executable coverage (§12 below — these tests actually ran and passed in this
environment, unlike prior attempts).

Original finding, preserved for record:

**Punctuation/formatting mismatch between an already-committed token and the Final's corresponding token could trigger a false "correction."**

- **Affected input**: any utterance where a word gets committed via partials *without* trailing punctuation (typical — punctuation is usually only added once Azure's recognizer is confident, i.e. late), and where that exact word, mid-sentence, later receives punctuation or formatting (e.g. a comma) in the Final that no partial ever showed for it while it was the "boundary word." Concretely: partials build up `_committedTokens = [..., "would"]`; the Final is `"... would, actually, ..."` where a comma got attached to `"would"` in the final's tokenization but never appeared attached to `"would"` in the partial that got committed.
- **Expected**: this should be treated as the same word (a formatting-only difference), and reconciliation should not treat everything from that point on as a "correction."
- **Actual**: `WordLevelCommonPrefixLength` does an exact ordinal string compare per token. `"would"` ≠ `"would,"`. The LCP stops one token early, `correction = true` fires, and the Final's emitted remainder re-includes that word (now with its punctuation) even though it was already committed/emitted once without punctuation — a token that is *semantically* the same word appears twice in the reconstructed stream (once bare, once with trailing punctuation attached).
- **Why this wasn't caught by the existing test `H_PunctuationVariation_...`**: per the Step 2 summary, that test covers punctuation variation between consecutive *partials*, not a mismatch specifically between an already-committed token and the Final's token for the same word. This specific partial-then-Final punctuation-boundary case does not appear to have a dedicated test.
- **Severity**: real but narrow — it only affects the word that happens to sit exactly at the commit boundary when punctuation is added, and only produces a formatting-level duplicate/mismatch (not a semantic content error, and not data loss — `CommittedSourceText` is still fully correct). It is a stricter, more concrete instance of the general trade-off already documented in §9 of `prefix-stability-engine.md` ("punctuation timing is coarse-grained"), but that doc did not previously spell out that this specific case forces a full `FinalizedWithCorrection` and a duplicated-looking word in the incremental stream, as opposed to just "coarse timing."
- **Proposed smallest corrective change (not implemented)**: when computing `WordLevelCommonPrefixLength`/`IsExtension` comparisons only (not for the emitted text itself), compare tokens with trailing punctuation stripped (e.g. trim a fixed small set of trailing punctuation chars before the `Equals` check), while still emitting the original, punctuation-preserving token text. This would make the LCP comparison punctuation-insensitive without changing what text is actually produced.

No other defect — coding bug, off-by-one, or invariant violation — was found across the ten cases and ten invariants above.

## 7. Identified false-positive emission risks

- **V-1 above** is the primary one: a punctuation-only mismatch between a committed token and the Final's token for the same word can cause that word to be re-emitted (with different punctuation) as part of the Final's "correction" remainder, even though no real speech-recognition correction occurred.
- **Case 2 / Case 7 class**: a genuinely wrong early commit (real self-correction, not just punctuation) is, by design, never retracted — only the Final's aggregate `CommittedSourceText` is guaranteed correct. If a future Step-3 consumer treats every `NewlyCommittedSegment` as "safe to permanently display/speak," a self-correction that happens *after* the trailing-word buffer has already let a word through will produce visibly/audibly contradictory output (old wrong segment + new corrected segment from the Final). This is the accepted trade-off already documented in `prefix-stability-engine.md` §6/§7 — restated here because the case traces in §4 make its concrete shape (what exact text a user would see/hear) explicit for the first time.

## 8. Identified content-loss risks

**None found.** In every traced case (especially 2, 5, 6, 7, 8 — the adversarial ones), `ProcessFinal`'s `CommittedSourceText` and emitted remainder were verified to fully and exactly reconstruct the true final text, because the Final computation is always anchored to the Final's own tokens, never to `_previousPartialTokens` or `_latestPartialTokens`. The known risk (§7) is *false-positive/duplicate-looking* emission, not *lost* content — nothing in this engine can cause the true final text to be under-represented.

## 9. Identified latency/commit-delay risks

- **Case 4 pattern**: when a partial repeats the previous partial's text exactly (or grows by fewer than 2 words from the previous partial), the trailing-word buffer combined with the strict `committableLength > _committedTokens.Count` check means the delta doesn't commit until a *subsequent* partial (or the Final) shows further growth. This is intentional per this phase's "correctness over low latency, don't tune thresholds yet" instruction, but it does mean commit latency is not uniform — it depends on how partial growth happens to be chunked by Azure, not just on elapsed time. This matches the already-documented §9 limitation in `prefix-stability-engine.md` ("no latency tuning was attempted") and is not a new finding, just confirmed concretely by trace.
- No case traced above showed unbounded delay or a word that could never commit via partials — every word not committed incrementally is guaranteed to be swept up by the Final (§8), so the worst case is simply "committed later than theoretically possible," never "never committed."

## 10. Whether the current implementation is safe to connect to Step 3

**V-1 is resolved; a separate, newly-observed issue (§12) still needs a decision before Step 3.** With V-1 fixed, punctuation-only differences between a committed token and its Final counterpart no longer produce a false correction or a duplicated-looking word — the engine's core guarantees continue to hold under trace, and this is now confirmed by an actual (not just hand-traced) test run for the first time this session (§12). The remaining, still-accepted trade-off is genuine content-level self-correction (Cases 2/7/8, §6/§7) — a real speech correction, not a formatting artifact — which is unchanged by this fix and remains a Step-3 integration-policy decision, not an engine defect. Separately, this test run surfaced one **pre-existing, unrelated test failure** (`G_WhitespaceOnlyVariation_TreatedAsUnchanged`, part of the original Step 2 suite) that has never actually executed before now (every prior run in this project's session was WDAC-blocked) — this is a genuine algorithmic gap, not caused by the V-1 change, and is flagged for your decision rather than fixed, since this correction's scope was V-1 only.

## 11. Required changes before live integration, if any

1. ~~Address V-1~~ — **done** (§6, §8a of `prefix-stability-engine.md`).
2. **Decide and document, explicitly, what a Step-3 consumer does when `FinalizedWithCorrection == true`** for a genuine (non-punctuation) content correction — unchanged from the original recommendation, still open.
3. **New, out-of-scope-for-this-correction finding**: `G_WhitespaceOnlyVariation_TreatedAsUnchanged` fails on an actual run (see §12). Trace: partial 1 `"I would like"` (3 tokens, first partial, no commit possible yet) → partial 2 `"I   would    like"` (whitespace-collapses to the same 3 tokens) triggers `agreedLength=3` → `committable=2` → first commit fires, committing `"I would"` (2 tokens) → partial 3 `"I would like to schedule"` (5 tokens) → `agreedLength=LCP(prev=3,curr=5)=3` (only `"I would like"` still agrees) → `committable=3-1=2`, which is **not greater than** the already-committed count of 2, so **no further commit fires**, and `r3.ShouldEmit` is `false`, failing the test's `Assert.True(r3.ShouldEmit)`. This is the same "commit-delay" class of behavior already documented in §9 (Case 4) — not a duplication/loss/correctness bug (the word would still be swept up by the eventual Final), but the existing test's expectation that "progress continues normally" after a whitespace-only partial turns out not to hold in this specific 3-partial shape. This needs your decision: relax the test's expectation (document the delay as intentional, matching Case 4), or treat it as a real gap in the "should always make some progress" guarantee. **Not fixed here — flagged only, per this correction's V-1-only scope.**

## 12. Build and test execution

- **`dotnet build VTTranslate.sln`**: **0 Warning(s), 0 Error(s)** — re-run after the V-1 fix, succeeded.
- **`dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj --no-build`**: **ran to completion this time — WDAC did not block this run.** This is the first time this suite has actually executed in this project's session (every previous attempt, across multiple phases, was blocked with `0x800711C7`); no bypass was applied, this run simply succeeded where prior ones didn't. Result: **127 passed, 1 failed, 128 total.** The 1 failure is `G_WhitespaceOnlyVariation_TreatedAsUnchanged` — a pre-existing test (not one added for V-1), traced and explained in §11 point 3 above; it is unrelated to the punctuation-normalization change (no punctuation is involved in its failing path) and was not modified or fixed as part of this correction. All new `V1_A` through `V1_G` and `V1_EmittedText_...` tests **passed**. No test result is being overstated — the one failure is reported exactly as observed.
