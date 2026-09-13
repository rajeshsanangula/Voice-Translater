# Step 1 — Measurement Results: Azure Partial-Result Behavior

**Status: measurement only. No behavior, threshold, or architecture was changed to
produce this data — see the instrumentation added in
`AzureSpeechTranslationProvider.cs` (marked "Step 1 measurement instrumentation" in
code comments) and `PartialResultAnalyzer.cs`.**

---

## 1. Test environment

- Real Azure AI Speech resource (same account/region used throughout this project), real network conditions at time of test.
- Provider: `AzureSpeechTranslationProvider` (unmodified control flow — instrumentation is purely additive logging).
- Audio source: `TestFileAudioInputSource` feeding real Azure-TTS-synthesized WAV files (this project's established methodology — see `docs/test-methodology.md`; this is NOT a physical microphone/acoustic test).
- Voices: `en-US-JennyNeural`, `de-DE-KatjaNeural`.
- Harness: two new `VTTranslate.LiveTest` commands, `gen-measurement-audio` and `measurement-test`, added for this phase.
- Diagnostic logs: one file per test case under `test-results/logs/measurement/`, via the existing `FileDiagnosticLogger`.

## 2. Test methodology

Each of the 10 phrases below was synthesized once via Azure TTS (deterministic, reproducible), then streamed through the real, unmodified pipeline via `TestFileAudioInputSource` → `AzureSpeechTranslationProvider.PushAudio`, with the new instrumentation logging every `Recognizing`, `Recognized`, and first `Synthesizing` event. Speaking rate for "fast" and "short" cases was controlled via SSML `<prosody rate="...">` — a real, reproducible way to vary rate, though it does not perfectly emulate human disfluency (documented limitation, consistent with `docs/design-notes/streaming-conversational-engine-design.md §9`).

**Not tested this pass, honestly**: physical acoustic speech (no microphone), and Test I (reconnect during active speech) — this session has no administrator rights, confirmed by a failed `Disable-PnpDevice` attempt in an earlier phase, and no reconnect occurred naturally during any of these 12 runs (stable network throughout). Zero reconnect data was collected.

## 3. Raw/aggregated measurements

| Case | Direction | Partial count | Revisions (source regressed) | Final duration (Azure-reported) | T0→first partial | T0→final | T0→first TTS | Eligibility | Notes |
|---|---|---|---|---|---|---|---|---|---|
| A normal | EN→DE | 2 | 0 | 2010ms | **13534ms** | 13926ms | 14426ms | accepted | see §11 — anomalous first-partial delay |
| B normal | DE→EN | 4 | 0 | 2440ms | 1592ms | 3686ms | 4074ms | accepted | baseline, no anomaly |
| C fast | EN→DE | 5 | 0 | 3210ms | 1410ms | 4070ms | 4740ms | accepted | |
| D fast | DE→EN | 3 | 1 | 3200ms | 4091ms | 5216ms | 5851ms | accepted | one source regression |
| E short | EN→DE | **0** | 0 | 560ms | — (no partials fired) | 13891ms | 14277ms | accepted | Azure skipped partial events entirely |
| E short | DE→EN | **0** | 0 | **240ms** | — (no partials fired) | 1553ms | 1898ms | **REJECTED** | duration below 250ms floor |
| F long | EN→DE | 22 | 6 | 10280ms | 1376ms | 12762ms | 14017ms | accepted | most revisions of any case |
| F long | DE→EN | 26 | 2 | 13080ms | 1516ms | 16320ms | 17150ms | accepted | most partials of any case |
| G correction | EN→DE | 4 | 1 | 5000ms | **13459ms** | 14942ms | 15769ms | accepted | anomalous delay again |
| G correction | DE→EN | 9 | 1 | 5120ms | 1647ms | 7238ms | 7938ms | accepted | |
| H consecutive (×3) | EN→DE | 2, 2, 2 | 0, 0, 0 | 2010/2200/1120ms | see §11 (T0-reset artifact) | — | — | accepted ×3 | |
| H consecutive (×3) | DE→EN | 4, 5, 1 | 0, 0, 0 | 2440/3000/960ms | see §11 | — | — | accepted ×3 | |

No content loss or duplication was observed in any case (every case produced exactly one accepted or rejected Final per intended utterance, matching the number of utterances in the source audio).

## 4. Partial-result behavior

- **`translatedPartialMissingCount` was 0 in every single case** — translated text was available on every `Recognizing` partial, with no exceptions across 12 test runs. Direct answer to requirement #11: **yes, partial translated text is available for every utterance**, at least in this sample.
- **Source text (`isPrefixExtension`) is a reliable pure-append signal once a second partial arrives** — in the large majority of partials across all cases, the source text extends the previous partial as a clean prefix. The exceptions are concentrated in long utterances (F: 6 and 2 source regressions respectively) — Azure does revise already-recognized source text, not just translations, for longer utterances.
- **Translated text regresses far more often than source text** — in nearly every case, most partials show `translatedIsPrefixExtension=False, translatedRegressed=True` even while the corresponding source text shows `isPrefixExtension=True`. This is the single most important finding for the streaming design (see §9).

## 5. Fast-speech observations (C, D)

Both fast-speech cases (SSML rate +60%) behaved essentially like normal-speed cases in partial count and timing — 5 partials/3210ms (C) and 3 partials/5216ms total (D), no dramatically fewer or more partials than the normal-speed baseline (2 and 4 partials for A/B). D showed one source-text regression; C showed none. **No evidence in this sample that faster speech alone produces measurably more unstable partials** — but this is a small sample (2 cases) using SSML-synthesized "fast" speech, not genuine human disfluent fast speech, and should not be treated as a strong conclusion.

## 6. Short-utterance observations (E)

This is the most decisive result in the whole dataset. **`test_e_short_de.wav` ("Ja." at SSML rate +80%) measured at 240ms and was REJECTED by `UtteranceEligibilityGate`** — logged exactly as `"duration 240ms below minimum 250ms"`. This is **direct, real evidence** answering requirement #15: **yes, the 250ms floor can and does reject a real, legitimate short utterance** under realistic conditions (a single short word spoken quickly). The English counterpart ("No." at the same rate) measured 560ms and was accepted — so the floor's effect is real but narrow (a ~250-320ms window where a genuine short word can fall on either side of the line). Both short cases produced **zero partial events** — Azure went straight to a Final with no `Recognizing` events at all, a distinct behavior from all other cases.

## 7. Reconnect observations (I)

**No data.** No reconnect occurred during any of the 12 measurement runs (stable network), and this session has no administrator rights to force one (confirmed by an earlier failed `Disable-PnpDevice` attempt in a prior phase). Requirements #13 and #14 remain **untested**. The instrumentation added (`ReconnectDuringUtterance` log entry, wired into `OnCanceled`) is ready to capture this data the moment a real reconnect happens — either naturally during a longer live session, or if administrator rights become available.

## 8. Eligibility-gate observations

Every accepted case showed `confidence=null` — the detailed-format confidence score was never actually populated by Azure for `TranslationRecognizer` results in this entire dataset, confirming the honest limitation already documented in `AzureConfidenceParser`'s doc comment (confidence support is documented for plain `SpeechRecognizer`, not guaranteed for translation results). Every accept/reject decision in this dataset was therefore made via the duration/word-density fallback path, never the confidence path. This is a real, now-confirmed-with-evidence limitation worth noting for Step 2: **the eligibility gate's confidence-based branch is currently dead code in practice** for this provider/scenario — it never fires because Azure never supplies a confidence value here.

## 9. Evidence supporting or contradicting the proposed prefix-stability approach

**Supports** the core mechanism: source text is a reliable, mostly-monotonic prefix-extension signal (§4) — exactly the assumption `docs/design-notes/streaming-conversational-engine-design.md §4` depends on for deciding when a segment is "stable."

**Reveals an additional reason the design is necessary, not previously evidenced**: translated text is volatile far more often than source text. This means a naive design that translates *every* partial as it grows would produce constantly-changing, contradictory audio even while the source text itself is stably extending. This is direct evidence *for* the design's specific mechanism of translating only once a source-stability threshold is met (not on every partial) — the design's approach is validated, and this data adds a second, independent reason to require it (translation volatility, not just source volatility).

**Reveals a gap the design doc did not anticipate**: long utterances (F) show the source text *itself* regressing 2–6 times, not just extending — meaning even the "reliable" source-prefix signal is not perfectly monotonic for longer sentences. The design's recommended mitigation (require stability across ≥3 consecutive partials, never commit the trailing word) should hold up against this, but this is real evidence the corrective/self-revision case is not rare — it happened in every long-utterance case tested.

## 10. Recommended parameters/algorithm inputs for Step 2

Based strictly on this data (not tuned/guessed):
- A stability requirement of **at least 2–3 consecutive matching partials** before commit looks appropriate — F's regressions were never more than 2 consecutive occurrences before re-stabilizing.
- **Do not use Azure's confidence score as an input** for now — it was never populated in this dataset; the eligibility gate's fallback (duration/word-density) is the only signal that actually fires in practice for this provider.
- **Translation should be re-requested at commit-time, not reused from an earlier partial's translation** — since translated text was shown to regress independently of source stability, an early partial's translation cannot be trusted even if its source text later proves stable.
- The 250ms eligibility floor (§6) is a real, working boundary — no change recommended from this data alone (per instructions, this phase does not tune thresholds), but Step 2 should be aware a genuinely short accepted utterance can produce **zero partial events**, meaning the stabilizer must handle a "straight to Final, no partials at all" path as a first-class case, not an edge case.

## 11. Findings requiring architectural changes to the design document

1. **T0 measurement methodology is unreliable for closely-spaced consecutive utterances** (Test H). Within `test_h_consecutive_en_to_de.wav`, T0-to-first-partial values grew utterance over utterance (12684ms → 13803ms → 15126ms) in a pattern matching accumulated *stream* time, not per-utterance time — evidence that our T0 reset (triggered by the `Recognized`/Final event) can fire *after* audio belonging to the next utterance has already been pushed, because real-time audio pushing can run ahead of Azure's own recognition-event timing. **This means T0→T1 latency numbers for closely-spaced consecutive speech should be treated as unreliable/approximate**, not exact. A correct fix (not implemented this phase) would need to timestamp each pushed chunk individually and correlate T0 to the chunk that actually *contains* each utterance's start, rather than "whichever chunk happens to arrive right after the previous utterance's Final." Recommend adding this as an explicit caveat to `docs/design-notes/streaming-conversational-engine-design.md §2`.
2. **Isolated large first-partial latencies (A, G) of ~13 seconds for otherwise-normal single-utterance cases**, not explained by content or by the consecutive-utterance artifact above (both were standalone files). This is most plausibly Azure connection/network latency variability — real network/service latency exactly as the design doc's §2 already describes as "conflated into what we measure and not separable." This specific dataset makes that conflation concretely visible (a >10-second swing between otherwise-similar cases) rather than just a theoretical caveat.
3. **Confidence is confirmed never available in practice** for `TranslationRecognizer` (§8) — worth updating the design doc's Step-2 recommendations to explicitly deprioritize any confidence-dependent logic.

---

## Automated tests and build

- New pure unit tests: `PartialResultAnalyzerTests.cs` (8 tests covering first-partial/no-prior-text, exact repeat, pure append, shrink, full divergence, mid-word revision misclassification guard, casing-only-change documented behavior, empty strings).
- Full suite: **95/95 pass**. Build: **0 warnings, 0 errors**.
- Secret scan: confirmed no `_logger.Log` call references `.Text`/raw recognized content anywhere in the modified file — only `.Length` and the new boolean comparison flags are logged.

---

*No Azure settings, eligibility thresholds, reconnect behavior, audio routing, translation/TTS behavior, LLM, Adaptive Translation Memory, or naturalization layer were added or changed to produce this report — instrumentation only, as instructed.*
