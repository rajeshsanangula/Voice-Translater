# Design Report — Next-Generation Real-Time Conversational Translation Engine

**Status: DESIGN ONLY. No production code was modified to produce this report.** All
citations below are to the actual code as it exists in this repository today.

---

## 1. Current pipeline analysis (exact code trace)

```
AudioCaptureSource.PumpLoop()                                    [Audio/AudioCaptureSource.cs:86-118]
  → PcmChunkCaptured event
    → DirectionPipeline capture lambda                           [Session/DirectionPipeline.cs:64-71]
        if (!_isMuted) { Latency.OnCaptureChunkForwarded(); _provider.PushAudio(chunk); }
      → AzureSpeechTranslationProvider.PushAudio()                [Providers/AzureSpeechTranslationProvider.cs:267-283]
        → _pushStream.Write(...)   [Azure SDK PushAudioInputStream — network-bound, opaque to us from here]

        ... Azure service-side processing (not observable) ...

      → recognizer.Recognizing fires (interim)                    [AzureSpeechTranslationProvider.cs:133-141]
        → PartialResult event → DirectionPipeline                 [DirectionPipeline.cs:76-81]
            Latency.OnPartialResult(); Transcript?.Invoke(...)
            *** translated text (e.Result.Translations) IS already present on this
                event today — but is used ONLY for transcript display. Nothing
                downstream acts on it for translation-commit or TTS. ***

      → recognizer.Recognized fires (final; ASR+MT bundled)       [AzureSpeechTranslationProvider.cs:143-164]
        → UtteranceEligibilityGate.Evaluate(...) runs synchronously, sets _lastUtteranceAccepted
        → FinalResult event → DirectionPipeline                   [DirectionPipeline.cs:82-95]
            Latency.OnFinalResult(); sequence++; mute-suppression check; Transcript?.Invoke(...)

      → recognizer.Synthesizing fires (TTS audio chunk(s))        [AzureSpeechTranslationProvider.cs:166-179]
        → DisarmTtsWatchdog(); gate check (_lastUtteranceAccepted)
        → AudioSynthesized event → DirectionPipeline               [DirectionPipeline.cs:96-110]
            Latency.OnAudioSynthesized() [T5]; suppressed-sequence check;
            _playback.EnqueueAudio(...) → Latency.OnPlaybackEnqueued() [T6]
              → IAudioOutputSink.EnqueueAudio()                    [Audio/AudioPlaybackSink.cs:32]
```

### Where the pipeline actually waits

| Wait point | Exact mechanism | Citation |
|---|---|---|
| Audio chunks | Not a wait — `PushAudio` is a fire-and-forget write; pacing comes from `RealTimeAudioPump` on the capture side, not from anything downstream. | `AudioCaptureSource.cs` |
| Partial recognition | Not acted upon at all for translation/TTS — only re-published for display. | `DirectionPipeline.cs:76-81` |
| **Final recognition — THE bottleneck** | Everything (translation, gate evaluation, TTS) is gated on ONE event: `recognizer.Recognized`. Azure holds the entire utterance server-side using its own end-of-speech silence timeout before firing this — a value **our code never configures** (no `SpeechServiceConnection_EndSilenceTimeoutMs` or `Speech_SegmentationSilenceTimeoutMs` property is set anywhere in `CreateAndStartRecognizerLockedAsync`, confirmed by inspection), so it runs on the Azure SDK's undocumented-to-us default. | `AzureSpeechTranslationProvider.cs:143-164` |
| Translation | No separate wait — bundled into the same `Recognized` result (`e.Result.Translations` already populated). | Same event as above |
| TTS | `Synthesizing` fires **only** in association with `Recognized` — never with `Recognizing`. Once it fires, forwarding to playback is immediate (no batching on our side). | `AzureSpeechTranslationProvider.cs:166-179` |
| Playback | `EnqueueAudio` is synchronous/non-blocking (buffered write); no wait. | `AudioPlaybackSink.cs:32` |

**Key finding**: the current architecture is not "slow because our code is slow" — our code does essentially zero waiting of its own. All perceived latency and all "waits for completed utterances before translating" comes from **relying on Azure's `TranslationRecognizer` in its default whole-utterance mode**, which bundles ASR+MT+TTS into one server-decided event. This is exactly what the user's complaints #1 and #3 are observing.

---

## 2. Latency analysis

### What's actually measured today (`LatencyBreakdown.cs`)

| Stage | Measured? | Trigger |
|---|---|---|
| T0 capture | ✅ Yes | `OnCaptureChunkForwarded()`, first forwarded chunk since last utterance boundary |
| T1 first partial | ✅ Yes | `OnPartialResult()`, on `recognizer.Recognizing` |
| **T2 stable partial** | ❌ **No — does not exist as a concept anywhere in the code.** Nothing compares successive partials for stability. | — |
| T3 final recognition | ✅ Yes | `OnFinalResult()`, on `recognizer.Recognized` |
| T4 translation completion | ⚠️ **Same instant as T3, by architecture** — Azure bundles ASR+MT server-side; there is no SDK signal that separates them. Already documented in `LatencyBreakdown.cs`'s class doc comment. | — |
| T5 first TTS audio | ✅ Yes | `OnAudioSynthesized()`, on `recognizer.Synthesizing` |
| T6 playback enqueue | ✅ Yes | `OnPlaybackEnqueued()`, right after `IAudioOutputSink.EnqueueAudio()` |
| T7 playback-start | ❌ **Not measured, explicitly documented as such.** Would require querying the audio device's hardware playback position — not implemented, never reported as a number. | — |

### Pipeline vs. network/service vs. human-perceived — clearly separated

- **Pipeline-internal latency** (what `LatencyBreakdown` reports): our own wall-clock time between SDK-delivered events. Real numbers from the last live-Azure test: Recognition+MT 479–1881ms, TTS 360–418ms.
- **Network/service latency**: fully **conflated** into the T1→T3/T4 interval we measure — we cannot separate "Azure's actual ASR+MT compute time" from "network round-trip" from "Azure's own end-silence-timeout wait." This is an honest limitation, not a gap we can currently close without Azure exposing more granular server-side timing than this SDK surface provides.
- **Human-perceived latency**: T7 (unmeasured) + acoustic path + human reaction — categorically **not** what any current number represents. The gaps the user actually felt on the live Meet call are almost certainly larger than the pipeline-internal numbers above, because they include the unmeasured pieces.

### Can the current architecture support true incremental/streaming translation without major redesign?

**No — with one important nuance.** The ASR+MT streaming data **already exists and is already flowing to our client** (Azure translates every `Recognizing` partial, not just the final) — that half is "free," just currently discarded after display. But **TTS synthesis is architecturally tied to `Recognized`, not `Recognizing`**, in the current use of `TranslationRecognizer`. There is no wiring anywhere that synthesizes audio for a partial. Getting streaming TTS requires **decoupling synthesis from `TranslationRecognizer`'s bundled behavior** — calling a separate `SpeechSynthesizer` ourselves per stabilized segment. That's a real, non-trivial addition (see §4, §8), not a "major redesign" of the whole app, but not a config toggle either.

---

## 3. Fast-speech analysis — exact/potential causes of missed or delayed content

1. **`UtteranceEligibilityGate`'s duration floor** (`MinimumDurationMs = 250`, and the `FallbackAppliesBelowDurationMs = 800` word-density check) could plausibly reject a genuine short, fast utterance (a quick "yes"/"ja"/interjection during rapid back-and-forth) if Azure recognizes it with low confidence. The gate was designed to reject noise, not legitimate short speech, but this is a real, citable interaction worth empirical checking (`UtteranceEvaluated` log entries would show `accepted=false` with a short-duration reason for such cases, if it's happening).
2. **Azure's own segmentation is entirely opaque and unconfigured.** In rapid, minimally-paused speech bursts, Azure's server-side decision about where one utterance ends and the next begins is invisible to us — could merge a fast burst into one long utterance (delaying everything until it fully finishes) or split unpredictably. We neither configure nor observe this boundary today.
3. **Generation-guard drops in-flight content during a reconnect.** `if (myGeneration != _generation) return;` (`AzureSpeechTranslationProvider.cs:135,145,168`) silently discards any partial/final from a superseded connection — correct for preventing duplicates, but if a reconnect happens to land mid-utterance during fast speech, that utterance's content is lost entirely, with only a log line as evidence and no user-facing indication.
4. **No barge-in/interruption handling exists at all.** Once TTS audio is enqueued via `IAudioOutputSink.EnqueueAudio`, it plays to completion — there is no code path to cancel in-flight or queued playback based on new incoming speech. A user talking over a still-playing translation gets overlapping/queued audio, not a clean interruption.
5. **Self-correction has no impact on translation today** (a positive finding) — since nothing acts on partials for translation/TTS, Azure's normal partial-revision behavior only affects the live transcript display, never causes duplicate translation/TTS. This will become a real design challenge once streaming translation is added (see §4's stabilization algorithm).

No segmentation thresholds were changed to produce this analysis.

---

## 4. Streaming translation design (proposal — not implemented)

### Stabilization algorithm (prefix-stability / "local agreement")

1. Track the last N (e.g. 2–3) `Recognizing` partials for the current in-flight utterance.
2. **Stable prefix** = the longest common word-level prefix shared across the last N consecutive partials. Only text within it is eligible for early commit; anything beyond is still volatile.
3. Track an **already-committed pointer** per utterance. On each stability check, only the *new* portion of the stable prefix (stable prefix minus already-committed) gets translated + synthesized as a new segment — never re-translate or re-synthesize already-committed text. This is what prevents duplicate TTS.
4. On the **final** `Recognized` event, translate + synthesize only the remaining tail (final text minus already-committed prefix) — guarantees full coverage exactly once.
5. **Correction policy** (a genuine, inherent trade-off of streaming translation, same one live captioning products accept): recommend (a) requiring stability across ≥3 consecutive partials before committing, and (b) never committing the last word of any stable prefix (buffer of ≥1 trailing word), to reduce — not eliminate — the chance of a late correction to already-spoken audio.

### Preventing the specific failure modes the user listed

| Failure mode | Mitigation |
|---|---|
| Duplicate translation | Already-committed pointer; only new prefix growth is translated |
| Translating unstable text too early | N-partial agreement requirement before commit |
| Repeated TTS | Same already-committed pointer applies to synthesis, not just translation |
| Sentence corruption | Word-boundary-only commits (never split mid-word); trailing-word buffer |
| Out-of-order audio | Synthesize segments **strictly sequentially** (never start segment N+1's synthesis before segment N's audio is retrieved) — simpler and safer than a reordering buffer, at a small latency cost |
| Stale translations | Each segment is translated with the full accumulated utterance-so-far as context, not in isolation, so grammar stays consistent as more text arrives |
| Context drift | Feeds directly into the Conversation Context Engine (§5/§10) for cross-utterance consistency; within one utterance, the accumulated-prefix-as-context rule above handles it |

### Step 3 implementation note (shadow-integrated, real Azure data, still not connected to translation/TTS)

`PrefixStabilityEngine` has now been run in a SHADOW/OBSERVATION path against real
Azure partials (see `docs/design-notes/streaming-shadow-integration.md`) — the
production translation/TTS pipeline is unchanged; the engine's proposed commits are
logged (metadata only) and discarded. Real-data highlights relevant to this section's
assumptions: commit intervals across normal/fast/long/self-correction cases clustered
around ~470-700ms once a commit stream started, and a real mid-utterance source
regression (not synthetic) was correctly withheld from commitment and resolved by a
later partial without needing the Final to override anything — direct, live
confirmation of the "≥1 trailing-word buffer" mitigation actually working as designed
against genuine Azure revision behavior, not just Step 1's SSML-synthesized cases. One
new caveat this live run surfaced (not previously visible from Step 1's per-case
summary numbers alone): EN→DE cases showed a consistent ~13-14 second first-partial
connection latency absent from every DE→EN case in the same run — see
`streaming-shadow-integration.md` §10.3. This is a plausible connection/voice-warmup
effect, not investigated further under Step 3's scope, but should be resolved before
any future latency claims are made for the EN→DE direction specifically.

### Step 4 implementation note (commit-policy shadow comparison, real Azure data)

An experimental alternative commit policy ("Policy B" — compare against the best-so-far
consistent partial rather than strictly the immediately-preceding one, see
`docs/design-notes/commit-policy-shadow-evaluation.md`) was run in shadow alongside the
production-tested policy ("Policy A", §4 above, unmodified) against 11 real Azure
utterances. Result: **the two policies produced byte-for-byte identical commit decisions
in every case** — the shorter-partial pattern Policy B targets did not occur naturally in
this sample, even though Step 1's own data already showed real *content* regressions
(different text, not shorter text) do occur. This means §4's "≥1 trailing-word buffer,
compare against the immediately preceding partial" recommendation is not yet shown to be
a real source of unnecessary latency by live evidence — the theoretical concern remains
worth tracking (Policy B is unit-tested and ready for a future, larger comparison sample)
but this section's algorithm should not be considered outdated by this finding alone. See
`commit-policy-shadow-evaluation.md` §14 for the full recommendation.

### Step 5 implementation note (incremental-translation shadow evaluation, real Azure data)

Three candidate incremental-translation strategies were evaluated in shadow (never
connected to TTS/playback) against real Azure output — see
`docs/design-notes/incremental-translation-shadow-evaluation.md`. The naive
"translate-independently-and-concatenate" approach this section's §4 already warned
against was concretely confirmed harmful: it produced severe duplicate-content rates
(e.g. 74 duplicate tokens against a 31-token final translation in one case) because
Azure's own per-partial translated text regresses far more often than source text
(§9's original finding — now directly measured as a real downstream cost, not just a
qualitative risk). A "translate the cumulative stable context each time" strategy
performed best of the three tested; a source-stability-gated incremental strategy
under-performed due to how often Azure's translated text itself contradicts its own
prior output, though the evaluation's reconciliation method may understate that
strategy's true recovery behavior (see the linked doc §14). This does not change §4's
recommended algorithm (which concerns SOURCE-side stability, already validated in
Steps 2-4) — it is new evidence specifically about the translation-dispatch strategy a
future Step 6+ would need to choose, not yet authorized.

### Step 5.5 implementation note (independent translation-provider feasibility, credential gap confirmed)

A real attempt was made to use a genuinely independent Azure translation API (Translator
Text v3.0 — a different, separately-provisioned service from the Speech Translation this
project already uses) to properly evaluate cumulative/contextual/boundary-aware
incremental translation strategies. This project's existing `AZURE_SPEECH_KEY` was
confirmed, via a real live call, **not authorized** for that API (HTTP 401, then HTTP 429
on retries) — see `docs/design-notes/incremental-translation-provider-feasibility.md`.
No genuine translation was obtained; no naturalness/correctness conclusions could be
drawn. This means Step 5's earlier findings (§ above) remain based on reinterpreting
Azure Speech's bundled translated output, not independent-provider evidence — that
limitation should be kept in mind before any future step treats Step 5's "Strategy B is
best-evidenced" conclusion as validated against a true independent translation source.

### Step 2 implementation note (offline engine, not yet connected)

`PrefixStabilityEngine` (see `docs/design-notes/prefix-stability-engine.md`)
implements this section's core mechanism with one deliberate simplification
versus the "≥3 consecutive partials" wording above: it compares only the
**two most recent** partials' word-level common prefix (not a 3-partial
window), and relies on the ≥1-trailing-word buffer as the primary safety
margin, per this phase's explicit "correctness over low latency, don't tune
thresholds yet" instruction — a 3-partial window is easy to add later if
measured mis-commit rates warrant it. Step 1's real data (§9 and §11.3 of
`streaming-measurement-results.md`) otherwise confirms this section's
assumptions: source-text prefix extension is a reliable signal, translated
partials are not, and confidence is never available in practice for this
provider — so the design's provider-abstraction and evaluation sections (§8,
§9) should treat any confidence-dependent branch as dead weight, not a
future dependency.

### Where this plugs in

A new component (tentatively `UtteranceStabilizer`) sitting between `AzureSpeechTranslationProvider`'s `PartialResult`/`FinalResult` events and a new decoupled TTS call path. This requires `AzureSpeechTranslationProvider` (or a new sibling class — see §8) to gain a second, independent `SpeechSynthesizer` instance for per-segment synthesis, since the existing bundled `Synthesizing` event only ever fires for whole finals.

---

## 5. Conversational naturalization architecture (proposal — not implemented)

A separate layer, fed by: literal translated text, source text (for grounding), recent conversation turns (bounded), applicable Adaptive Translation Memory entries, target language, domain tag.

```csharp
interface INaturalizationProvider
{
    Task<string> NaturalizeAsync(NaturalizationRequest request, CancellationToken ct);
}
```

**Safe default**: a `PassthroughNaturalizationProvider` (returns the literal translation unchanged) — this is the correct *first* implementation, establishing the extension point with zero behavior change.

**Preventing invented information** (explicit requirement): naturalization is fundamentally a behavior constraint on whatever engine eventually implements this interface — genuinely natural rewriting beyond simple phrase substitution likely requires an LLM-class capability, which is explicitly out of scope to add right now (per constraint #11) and is flagged honestly as a future decision point, not assumed. Independent of engine choice, a cheap **architectural safety net** is proposed: extract key entities (numbers, capitalized proper nouns) from the literal translation via simple regex/heuristic, and reject/fall back to literal translation if the naturalized version drops or adds any such entity. Not a guarantee, but a real, implementable check.

---

## 6. Adaptive Translation Memory architecture (proposal — not implemented)

### Three explicitly separated layers

1. **Temporary conversation context** — in-memory only, bounded (last N turns or M minutes), never persisted, cleared on session Stop. Owned at the session level (new `ConversationContext`, sitting alongside `DirectionPipeline`, not inside `AzureSpeechTranslationProvider` — keeps the provider stateless/reusable).
2. **User-level persistent preferences** (the actual "Adaptive Translation Memory") — scoped to **the operator of this installation** (one local Windows user), not to "whoever happened to be speaking" — this matters precisely because the existing `UtteranceEligibilityGate` documentation already establishes the app cannot distinguish speakers. Learned terms must never be auto-attributed to an unidentified voice. Stored locally (a JSON/SQLite file under `%AppData%\VTTranslate\memory\`, mirroring the existing `AppSettings` pattern) — this is a local settings-style file, not the kind of "database" the constraints excluded.
3. **Global model/provider behavior** — untouched. Learned memory is **never** sent to Azure's shared service; it's applied purely locally, before/after Azure's call. This is what guarantees one user's terminology can never silently leak into another user's session (there is no shared channel for it to leak through).

### Safe learning pipeline (exactly as required — nothing silent)

```
candidate observation → confidence/repetition evaluation → optional user confirmation → persistent memory
```

- **Candidate observation**: either (a) an explicit user correction (requires a new UI affordance — an editable transcript line, not yet built) — instant high-confidence candidate, or (b) a pattern repeated across multiple utterances in the same session (weak signal, needs more evidence before promotion).
- **Confidence/repetition evaluation**: a repetition-sourced candidate needs ≥K (e.g. 3) consistent observations before becoming eligible; an explicit correction is eligible immediately.
- **User confirmation**: eligible candidates are queued and surfaced ("You've translated 'X' as 'Y' three times — remember this?") — accept or dismiss, always explicit. Nothing is silently learned, satisfying the hard requirement.
- **Persistent memory**: only confirmed entries are written.

This entire subsystem is greenfield — nothing like it exists in the codebase today.

---

## 7. Privacy architecture (proposal — not implemented)

- The existing rule (diagnostic logs contain metadata only, never recognized/translated text — enforced today in `AzureSpeechTranslationProvider`'s `_logger.Log` calls, which pass only `.Length`, never `.Text`) **must extend unchanged** to every new component: `ConversationContext`, `UtteranceStabilizer`, and the Adaptive Translation Memory candidate pipeline all log counts/timestamps/confidence only, never content.
- **Conversation Context** is the one place actual utterance text needs to exist transiently (to feed translation/naturalization context) — RAM-only, never serialized to disk or log, explicitly bounded and cleared on session Stop.
- **Adaptive Translation Memory** is a deliberate, narrow, explicit exception to "never store speech content" — scoped only to short, user-confirmed terms/phrases (never full utterances), in a clearly-named user-visible location, with a required future UI to view and delete individual entries or clear everything.
- No component in this design silently retains anything beyond what's described above.

---

## 8. Provider abstraction requirements

**Current `ISpeechTranslationProvider`** (`Providers/ISpeechTranslationProvider.cs`) bundles ASR+MT+TTS behind one `AudioSynthesized` event tied to whole-utterance completion. This is **insufficient** for streaming, precisely because of §4's finding: no way to request incremental synthesis, no discrete "translate this partial against this context" operation.

**Recommendation: extend, do not replace.** `DirectionPipeline` already depends on the interface, not the concrete class — a new streaming-capable provider can be added without touching existing callers:

```csharp
// Purely additive — existing ISpeechTranslationProvider and all its consumers unchanged.
interface IStreamingTranslationProvider : ISpeechTranslationProvider
{
    event EventHandler<StableSegment>? StableSegmentAvailable; // stabilized sub-utterance text+translation, no audio yet
}

interface ITextToSpeechProvider  // decoupled, callable per-segment
{
    Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken ct);
}
```

For genuinely different future providers (a different cloud vendor, or a local/on-device model): the existing interface is already provider-agnostic at this level — a new implementation just needs the same 4 events + 3 methods. The gap is specifically streaming granularity, not general provider-swappability, which was already a design goal met by the original architecture.

---

## 9. Evaluation/benchmark design

Builds on the existing, already-proven `VTTranslate.LiveTest` harness (`pipeline-test`/`TestCase` pattern).

**Categories** (per the request): normal speech, fast speech, long sentences, short sentences, German idioms, colloquial language, business language, technical terminology, incomplete sentences, self-corrections, interruptions, consecutive turns — each needs curated representative audio (Azure-TTS-generated for determinism, as today's harness already does, ideally supplemented with real human recordings for authenticity, since synthesized "fast speech" may not represent real disfluency well).

**Metrics**:
| Metric | How |
|---|---|
| Missing content | Word-overlap ratio, extending the existing `TextSimilar` heuristic |
| Duplicated content | Repeated n-gram / repeated-audio-segment detection (new) |
| Translation correctness | Needs a curated reference translation per case + either human review or a reference-based automated score — flagged as needing human judgment for true correctness; automated scores are a proxy |
| Naturalness | Fundamentally a human judgment call without an LLM-judge (out of scope) — proposed as a manual 1–5 rating rubric for now |
| Latency | Existing `LatencyBreakdown`, reused directly |
| First translated-audio delay | T0→T5, already measurable |
| Stability of incremental translation | New metric, once streaming exists: count of committed-segment corrections (should be near zero by design; a regression indicator) |

No target percentages are proposed — per instruction, this defines what/how to measure; thresholds should be set only after a real baseline run.

---

## 10. Architecture proposal

```
Audio
  → Streaming ASR (existing Azure connection, reused)
  → Utterance/segment stabilizer                    [NEW — §4]
  → Conversation Context Engine                      [NEW — §6, layer 1]
        + Adaptive Translation Memory                [NEW — §6, layer 2]
        + User Preferences                           [NEW — §6, layer 2]
  → Translation Engine (existing Azure MT, reused, now fed segment-by-segment)
  → Naturalization Layer                              [NEW — §5, defaults to passthrough]
  → TTS (decoupled per-segment synthesis)             [NEW — §4, §8]
  → Playback (existing IAudioOutputSink, unchanged)
```

### Local vs. cloud/backend

| Component | Location | Why |
|---|---|---|
| Audio I/O, stabilizer, Conversation Context | **Local** | Real-time latency requirement; Conversation Context is also privacy-bounded — sending live conversation text to a backend for context management adds a new data-exposure surface with no clear benefit |
| User Preferences / Adaptive Translation Memory storage | **Local** (MVP) | Mirrors `AppSettings`'s existing local-file pattern; could later sync via a backend if multi-device becomes an explicit product requirement — not assumed here |
| Streaming ASR, Translation Engine | **Cloud** (unchanged — already Azure) | No new cloud dependency introduced by this design |
| Naturalization Layer | **Undecided, interface-isolated** | If it ends up needing an LLM-class model too large to run locally, this is the most likely future backend candidate — but the `INaturalizationProvider` interface keeps callers unaware of which, so the decision is deferred without blocking anything else |

---

## 11. Files/classes that would need modification (future phases — none touched now)

- **New**: `ConversationContext.cs`, `UtteranceStabilizer.cs`, `INaturalizationProvider.cs` + `PassthroughNaturalizationProvider.cs`, `IAdaptiveTranslationMemory.cs` + local file-backed implementation + candidate-evaluation pipeline, `ITextToSpeechProvider.cs` (+ an Azure-backed implementation using `SpeechSynthesizer` directly).
- **Eventually modified**: `AzureSpeechTranslationProvider.cs` (or a new sibling implementing the extended interface), `DirectionPipeline.cs` (consume stabilized segments, orchestrate per-segment TTS ordering), `MainViewModel.cs`/XAML (correction/confirmation UI, memory review screen).
- **New test infrastructure**: benchmark corpus + `VTTranslate.LiveTest` extensions.

---

## 12. Risks and trade-offs

- **Streaming correction trade-off is inherent**, not a bug — same limitation every live-captioning product accepts. Must be communicated as an accepted trade-off once shipped.
- **Decoupled per-segment TTS likely increases Azure API call volume and cost**, and adds real ordering/concurrency complexity — mitigated by the sequential-synthesis recommendation, at a latency cost.
- **Naturalization risk**: any rewriting layer risks altering meaning; the entity-preservation check (§5) is a partial mitigation, not a guarantee — human spot-checking remains necessary, especially early on.
- **Adaptive Translation Memory risk**: even with explicit confirmation, a term correct in one context may be wrong in another (domain drift); the "conversation-domain" scoping in §6 is the intended mitigation but needs its own follow-up design pass.
- **Privacy risk**: Adaptive Translation Memory is a genuinely new persistent-text-storage surface versus today's zero-text-storage baseline — even opt-in, this is a real decision to confirm explicitly before implementation, not just an engineering detail.
- **Scope/effort risk**: this is the largest single feature area in the project's history. Recommend the incremental sequence below over attempting §4–§6 together.

### Recommended smallest safe implementation sequence

1. **Instrumentation-only**: log partial-stability metrics using existing data (how much do partials churn before finalizing) to empirically validate stabilization assumptions — zero risk, pure measurement, no behavior change.
2. Build `ConversationContext` (in-memory, bounded) wired as an inert pass-through — establishes plumbing, zero behavior change.
3. Build `INaturalizationProvider` + `PassthroughNaturalizationProvider`, wire in as a no-op — establishes the extension point, fully reversible.
4. Prototype the stabilizer **offline against logged partial-sequences** (not live) using the §9 framework — validate before touching the live pipeline.
5. Only then, behind an explicit default-OFF feature flag, wire live incremental translation+TTS for **one direction**, tested extensively, before enabling by default.
6. Adaptive Translation Memory (confirmation UI + storage) **last**, since it depends on real correction/repetition signals from a working streaming pipeline.

---

*No Azure settings, segmentation thresholds, TTS configuration, audio routing, LLM, persistent memory, database, mobile, backend, voice cloning, or speaker identification were added or changed to produce this report.*
