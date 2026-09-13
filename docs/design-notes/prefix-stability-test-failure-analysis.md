# Test Failure Analysis — `G_WhitespaceOnlyVariation_TreatedAsUnchanged`

**Status: RESOLVED via Option A (test-expectation correction only).** Per
explicit decision, the production `PrefixStabilityEngine` algorithm was
**not** changed — the one-step commit-delay behavior analyzed below is
accepted as correct. Only the test's assertions were rewritten (renamed to
`G_WhitespaceOnlyVariation_NotTreatedAsCorrection_AllContentEventuallyRecoveredWithNoDuplication`
in `PrefixStabilityEngineTests.cs`) to verify what the engine actually and
correctly guarantees: no duplicate emission, no content loss, no spurious
correction, and full recovery by the Final — rather than the stronger,
undocumented "every content-growing partial always commits immediately"
assumption the original assertion encoded. The investigation below (root
cause, invariant analysis, Option A vs. B) is preserved unchanged as the
record of that decision.

---

## Failing test (exact source, `PrefixStabilityEngineTests.cs`)

```csharp
[Fact]
public void G_WhitespaceOnlyVariation_TreatedAsUnchanged()
{
    var engine = new PrefixStabilityEngine();

    engine.ProcessPartial(P(1, "I would like"));
    var r2 = engine.ProcessPartial(P(2, "I   would    like")); // extra whitespace only
    var r3 = engine.ProcessPartial(P(3, "I would like to schedule"));

    Assert.True(r3.ShouldEmit); // progress continues normally despite whitespace noise
}
```

`P(seq, text)` constructs a `PartialSourceEvent(U, seq, text, T(seq))` — utterance ID `"utterance-1"`, sequence number as given.

## Exact input sequence

1. `ProcessPartial(seq=1, "I would like")`
2. `ProcessPartial(seq=2, "I   would    like")` (extra internal whitespace, same words)
3. `ProcessPartial(seq=3, "I would like to schedule")`

No Final is sent in this test — it only asserts on `r3`.

## Exact actual output

`r3.ShouldEmit == false` (i.e. `r3.NewlyCommittedSegment == null`). The assertion `Assert.True(r3.ShouldEmit)` fails because the actual value is `false`.

## Expected output (per the test)

The test's comment states the intent: "progress continues normally despite whitespace noise" — i.e., it expects partial 3 (which genuinely adds two new words, "to" and "schedule", beyond partial 1/2's "I would like") to produce a new commit.

## State trace (exact, field-by-field)

Tokenizer note: `Tokenize` splits on `text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)` — any run of whitespace (including multiple/irregular spaces) is treated as a single separator and empty entries are dropped. So `"I   would    like"` tokenizes to exactly `["I", "would", "like"]` — byte-for-byte identical to `"I would like"`'s tokenization. The whitespace difference is invisible to the engine from partial 2 onward; this is expected and correct (confirmed no bug here).

### Before any calls
```
_currentUtteranceId = null
_lastProcessedSequence = -1
_committedTokens = []
_previousPartialTokens = null
_latestPartialTokens = []
_commitVersion = 0
```

### Partial 1 — `ProcessPartial(1, "I would like")`
- Utterance ID adopted (`"utterance-1"`), reset no-op.
- `1 > -1` → sequence accepted, `_lastProcessedSequence = 1`.
- `currentTokens = ["I","would","like"]` (3 tokens, non-empty).
- `_latestPartialTokens = ["I","would","like"]`.
- `_previousPartialTokens == null` → the `if` block is skipped entirely — **no commit is even attempted on the first partial**, by design (there is nothing yet to compare against).
- `_previousPartialTokens = ["I","would","like"]`.
- **Result (`r1`, not captured by the test but computed)**: `Action=None`, `CommittedSourceText=""`, `NewlyCommittedSegment=null`, `PendingUnstableText="I would like"` (since `_latestPartialTokens.Count(3) > _committedTokens.Count(0)`), `ShouldEmit=false`.

**State after partial 1**: `committed=[]`, `previous=[I,would,like]`, `latest=[I,would,like]`, `lastSeq=1`.

### Partial 2 — `ProcessPartial(2, "I   would    like")`
- `2 > 1` → accepted, `_lastProcessedSequence = 2`.
- `currentTokens = ["I","would","like"]` (3 tokens — identical to partial 1's tokens; the extra whitespace has already been normalized away by `Tokenize`).
- `_latestPartialTokens = ["I","would","like"]`.
- `_previousPartialTokens != null` → enter comparison:
  - `agreedLength = WordLevelCommonPrefixLength(["I","would","like"], ["I","would","like"]) = 3` (all three tokens match, ordinal, trailing-punctuation-normalized — irrelevant here, no punctuation present).
  - `committableLength = max(0, 3 - 1) = 2` (hold back the boundary word — here, the boundary word happens to be `"like"`, the last of the three identical tokens).
  - `2 > _committedTokens.Count (0)` → **true** → candidate path taken.
  - `candidateTokens = currentTokens.Take(2) = ["I","would"]`.
  - `IsExtension([], ["I","would"]) = true` (empty is trivially a prefix of anything).
  - `newTokens = candidateTokens.Skip(0) = ["I","would"]`.
  - `_committedTokens = ["I","would"]`. `newSegment = "I would"`. `_commitVersion = 1`. `action = Committed`.
- `_previousPartialTokens = ["I","would","like"]` (updated to partial 2's tokens — same content as partial 1's, but this is still a fresh assignment).

**Result (`r2`)**: `Action=Committed`, `CommittedSourceText="I would"`, `NewlyCommittedSegment="I would"`, `PendingUnstableText="like"` (latest has 3 tokens, committed has 2, remainder is `"like"`), `CommitVersion=1`, `ShouldEmit=true`.

**State after partial 2**: `committed=[I,would]`, `previous=[I,would,like]`, `latest=[I,would,like]`, `lastSeq=2`, `commitVersion=1`.

**This is the crux of the bug/behavior in question**: the whitespace-only-different, textually-identical repeat of partial 1 is exactly what *causes* the first-ever commit to fire (2 tokens committed), because it is the *second* data point the LCP comparison needs — the very first partial can never commit anything by itself. This is not specific to whitespace noise; any second partial with ≥2 tokens of agreement against the first would do the same. The "extra whitespace" is a red herring as far as *this* step goes — it behaves exactly as if partial 2 had been byte-identical to partial 1, which it effectively is after tokenization.

### Partial 3 — `ProcessPartial(3, "I would like to schedule")`
- `3 > 2` → accepted, `_lastProcessedSequence = 3`.
- `currentTokens = ["I","would","like","to","schedule"]` (5 tokens).
- `_latestPartialTokens = ["I","would","like","to","schedule"]`.
- `_previousPartialTokens != null` (`= ["I","would","like"]`, 3 tokens, from partial 2) → enter comparison:
  - `agreedLength = WordLevelCommonPrefixLength(["I","would","like"], ["I","would","like","to","schedule"])`. `n = min(3,5) = 3`. Compare index 0 `"I"="I"` ✓, index 1 `"would"="would"` ✓, index 2 `"like"="like"` ✓. Loop ends because `i` reached `n=3` (not because of a mismatch) → `agreedLength = 3`.
  - `committableLength = max(0, 3 - 1) = 2`.
  - `2 > _committedTokens.Count (2)` → **false** — condition fails, the whole `if (committableLength > _committedTokens.Count)` block is skipped.
  - `action` stays `None`, `newSegment` stays `null`.
- `_previousPartialTokens = ["I","would","like","to","schedule"]`.

**Result (`r3`)**: `Action=None`, `CommittedSourceText="I would"` (unchanged), `NewlyCommittedSegment=null`, `PendingUnstableText="like to schedule"` (latest 5 tokens minus committed 2), `CommitVersion=1` (unchanged), **`ShouldEmit=false`** — this is exactly what fails `Assert.True(r3.ShouldEmit)`.

**State after partial 3**: `committed=[I,would]` (unchanged), `previous=[I,would,like,to,schedule]`, `latest=[I,would,like,to,schedule]`, `lastSeq=3`, `commitVersion=1` (unchanged).

No Final is sent in this test, so there is no final trace to add — the test ends here, already failed on the `r3` assertion.

## Root cause

The comparison at partial 3 is `WordLevelCommonPrefixLength(_previousPartialTokens, currentTokens)`, and `_previousPartialTokens` at that point is partial **2**'s tokens (`["I","would","like"]`), **not** partial **1**'s. Because partial 2 was textually identical to partial 1 (post-tokenization), its LCP against partial 3 is capped at its own length — 3 tokens — even though partial 3 genuinely extends the utterance by two more real words ("to", "schedule"). The engine only ever compares **consecutive** partials, so the fact that partial 2 contributed *zero new information* over partial 1 doesn't get "carried forward" — it costs a full comparison step. Partial 3's `committableLength` (2) exactly equals what partial 2 already caused to be committed (2), so nothing new clears the `>` threshold this round.

This is a direct, mechanical consequence of two design choices working together, both already documented and both deliberate:
1. **Comparison is always against the immediately preceding partial only** (§3 of `prefix-stability-engine.md`: "compute the word-level longest common prefix (LCP) between the current and immediately preceding partial's tokens") — there is no "skip past a no-op partial" logic.
2. **The trailing-word buffer holds back exactly one word per comparison step** — so each comparison step can advance the committed frontier by at most `(this partial's new agreement) - 1` words, and a repeated/no-op partial "resets" that budget for the next comparison to start counting agreement from scratch against the repeat, not against the original growth.

The whitespace itself contributes nothing to the failure — it is only the mechanism by which this test produced a *textually-unchanged* partial 2. Any unchanged-content partial 2 (whitespace-only, exact duplicate, or otherwise) between two partials that would otherwise have committed more would reproduce exactly this delay by exactly one step.

## Is production behavior correct?

**Yes, relative to every stated invariant and the algorithm as documented.** Specifically:

- **No duplicate output**: not violated — nothing was emitted twice; `r3` simply emitted nothing.
- **No lost final content**: not applicable/not violated within this test (no Final was sent), and structurally guaranteed regardless — a subsequent partial or the eventual Final would still sweep up "like to schedule" correctly (traced and confirmed in the verification report's Case 4 and Case 6).
- **Monotonic commitment**: not violated — `_committedTokens` never shrank; it just didn't grow this particular step.
- **Deterministic behavior**: not violated — this trace is 100% reproducible; the same three inputs always produce the same three outputs.
- **Final authority**: not applicable (no Final in this test).
- **Reset isolation**: not applicable (single utterance).
- **Reasonable prefix-stability semantics**: **arguably strained, but not broken.** The engine's own stated semantics are "compare only against the immediately preceding partial, hold back the boundary word every time" — under those exact semantics, this is the correct, intended output. The test's expectation ("progress continues normally") is really an *implicit additional* semantic — "an unchanged-content partial should not cost a full commit-delay step" — that the algorithm as documented does not promise anywhere. `prefix-stability-engine.md` §3/§5 describes commits as happening incrementally per-partial with a trailing-word buffer; it does not claim every partial that objectively grows the utterance will always produce a commit, nor does it special-case no-op partials.

So: this is **not** a coding bug (no invariant is violated, nothing is lost, corrupted, or duplicated) — it is a **real, reproducible latency/commit-delay characteristic** of the two-consecutive-partials-only comparison design, surfaced concretely for the first time by an executable run, in a shape (a whitespace-only "flat" partial) that is plausible for real Azure output.

## Is the test expectation correct?

**Partially — the test's premise (progress "should" continue) is a reasonable product expectation, but the specific assertion (`r3.ShouldEmit == true`) encodes an assumption the algorithm doesn't actually guarantee at the single-partial granularity.** The test was written (per the original Step 2 test-authoring pass) to assert "whitespace noise doesn't break things" — and in the sense that matters most (no corruption, no wrong commit, no lost content, no exception), it doesn't. But the test conflates "doesn't break things" with "doesn't delay things by one step," which are different guarantees. The test's own in-code comment ("progress continues normally") is the part that turns out to be imprecise, not necessarily the underlying intent that whitespace noise be harmless.

## Does this represent a real Step-3 latency concern?

**Yes, worth flagging, but bounded and already partially anticipated.** If Azure genuinely emits a partial that repeats the prior partial's text verbatim (a real, plausible Azure behavior — e.g. a partial re-sent with an updated internal timestamp/confidence but unchanged text) immediately before a partial that would otherwise have advanced the commit frontier by exactly the trailing-buffer's width, that specific next partial's growth is "absorbed" into catching up rather than committing further — a one-step delay, not an unbounded one. This is the same class of finding already recorded in `prefix-stability-verification.md` §9 ("Case 4 pattern... commit latency is not uniform — it depends on how partial growth happens to be chunked by Azure") and §11 point 3 (this exact failing test, flagged as unfixed pending your decision). This document adds the precise mechanical explanation (a repeated/no-op partial "spends" a comparison step) that the verification report's Case 4 trace did not fully spell out. It does not change the earlier report's conclusion that the worst case is "committed later than theoretically possible," never "never committed" — confirmed again here since no Final was needed to eventually recover "like to schedule" (a subsequent partial or Final would still capture it, per Case 6's structural guarantee).

## Recommended smallest change, if any (NOT implemented)

Two independent options exist, deliberately not applied here per instruction:

**Option A — treat the test's expectation as wrong, adjust the test only.** Change the assertion to reflect the actual, documented one-step commit-delay semantics (e.g. assert `r3.ShouldEmit == false` and `r3.CommittedSourceText == "I would"`, then add a fourth partial or Final to show the delayed content is still recovered) — zero production risk, purely documents existing behavior precisely. This treats the finding as case **A** from your prompt ("the test expectation is wrong").

**Option B — treat the behavior as unnecessarily conservative, adjust the algorithm.** Compare the current partial against the best (longest) prior partial seen so far in the utterance, not strictly the immediately-preceding one — e.g. track a `_bestPartialTokensSoFar` (initialized to `_previousPartialTokens` normally, but only replaced when a new partial's token count is `>=` the current best) and use that in the LCP comparison instead of the literal previous partial. This would make a no-op/regressive partial "invisible" to the comparison rather than resetting the comparison baseline, closing the one-step delay this test exposes. This is a real algorithm change with its own new surface for review — deliberately not implemented here, and would need its own case-by-case re-verification (in particular re-checking it doesn't weaken regression/contradiction handling, since "best seen so far" and "always compare against latest" have different behavior when the utterance genuinely regresses, not just repeats). This maps to case **C** from your prompt ("behavior is correct but the test is asserting an unnecessarily aggressive commit timing") — Option B would be the change if you decide the timing itself, not just the test, should be more aggressive.

No changes were made to either the algorithm or the test as part of this investigation.

## Build and test result (after this investigation — no code changed)

- **`dotnet build VTTranslate.sln`**: **0 Warning(s), 0 Error(s)**.
- **`dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj --no-build`**: re-run, unmodified — **same result as before this investigation: 127 passed, 1 failed, 128 total.** The single failure is `G_WhitespaceOnlyVariation_TreatedAsUnchanged`, exactly as analyzed above. WDAC did not block this run (consistent with the immediately preceding successful run). No test was changed, skipped, or worked around to obtain this result.
