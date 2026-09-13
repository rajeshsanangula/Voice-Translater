# Architecture — VT Software (German ↔ English Real-Time Voice Translation)

## Scope of this document

This describes the architecture actually being implemented: a **Windows desktop MVP**
that performs real-time, bidirectional English ↔ German speech translation using
microphone capture, cloud streaming ASR, translation, and neural TTS.

Android, iOS, cellular-call interception, voice cloning, and a multi-tenant cloud
backend are **out of scope for this build**. They are noted below as future phases
with their real technical constraints, so the architecture doesn't paint itself into
a corner, but no code for them exists yet.

## Why Windows-first, and why not everything at once

Real-time speech translation across native Windows audio, Android, iOS, and cellular
call interception is a distinct engineering discipline per platform (WASAPI vs.
AVAudioSession/CallKit vs. Android Telecom/AudioRecord), each gated by different OS
security models. Building all of them in one pass produces four broken prototypes
instead of one working product. The Windows desktop case is chosen first because:

- It has the most permissive audio APIs (loopback capture, virtual audio devices).
- It's the only platform where a solo/small build can reach a genuinely working
  end-to-end demo without an OS vendor's app-store approval process or hardware.
- Meeting-app translation (Teams/Zoom/Meet) is deliverable here without carrier or
  telephony involvement.

## MVP architecture

```
 ┌────────────────────┐         ┌────────────────────┐
 │ Microphone (WASAPI) │         │ System/Loopback     │
 │  English speech in  │         │ audio (remote German│
 └─────────┬───────────┘         │ participant, from   │
           │                     │ meeting app or       │
           │                     │ headphones-out loop) │
           │                     └─────────┬────────────┘
           v                               v
   ┌───────────────┐               ┌───────────────┐
   │ AudioCapture   │               │ AudioCapture   │
   │ (NAudio/WASAPI)│               │ (NAudio/WASAPI)│
   └───────┬────────┘               └───────┬────────┘
           v                               v
   ┌────────────────────────────────────────────────┐
   │      LanguageAwarePipeline (per direction)      │
   │  VAD → Streaming ASR → Translation → TTS        │
   └───────┬────────────────────────────┬────────────┘
           v                            v
   ┌───────────────┐            ┌───────────────┐
   │ TTS Output     │            │ TTS Output     │
   │ (German audio) │            │ (English audio)│
   │ → speaker or   │            │ → speaker      │
   │ virtual mic*   │            │                │
   └───────────────┘            └───────────────┘
```

\* Virtual microphone routing into Teams/Zoom/Meet requires a third-party virtual
audio driver (e.g., VB-Audio Virtual Cable) or a signed custom audio driver. The MVP
integrates with an **existing installed virtual audio device** by letting the user
select it as an output; it does not ship a custom kernel-mode audio driver (that is
a separate, much larger undertaking — see "Explicitly deferred" below).

## Provider abstraction

All AI capability is behind interfaces so the backing service can be swapped without
touching the pipeline or UI:

```csharp
ISpeechRecognitionProvider   // streaming ASR, partial + final results
ITranslationProvider          // text or speech translation
ISpeechSynthesisProvider      // streaming TTS
ILanguageDetectionProvider    // language ID with confidence
```

MVP implementation: `AzureSpeechProvider` implements all four using the Azure AI
Speech SDK, which natively supports streaming speech-to-speech translation
(recognize → translate → synthesize) in one connection for low latency. A second
`NullProvider` (no-op/mock) exists only for unit tests — never used in the shipped
app.

## Session model

```csharp
class TranslationSession {
    Guid SessionId;
    string InputLanguage;   // "en-US" or "de-DE"
    string OutputLanguage;
    SessionDirection Direction; // MicToRemote | RemoteToMic
    SessionStatus Status;
    DateTimeOffset StartedAt;
}
```

Two sessions run concurrently (one per direction) inside one process, sharing device
selection and provider configuration but independent audio streams — this is what
prevents the German TTS output from being picked up and re-translated (feedback
loop): each direction owns a distinct audio source and the app never feeds an
output device's live signal back into a capture stream that's also active.

## Feedback-loop prevention (MVP-level)

Full echo-cancellation-grade feedback prevention is complex; the MVP uses a simpler
guarantee that is still correct: microphone capture and system-loopback capture are
different, independent audio devices/streams, and generated TTS audio is written
only to output devices, never back into a capture buffer. Windows' own loopback API
captures what a specific render device is playing — if the user's TTS output device
and the loopback-captured device are the same, generated audio would be re-captured.
The app enforces (and the UI documents) that loopback capture must target a
*different* device than the German TTS output, or that TTS output routes to the
virtual microphone instead of a monitored speaker.

## Explicitly deferred (not built, and why)

| Feature | Why deferred |
|---|---|
| Android app | Distinct native audio stack (AudioRecord/Oboe), different lifecycle/background rules, needs a physical device or emulator to validate — separate build effort. |
| iOS app | Requires Apple Developer account, Xcode/macOS toolchain (not available on this Windows machine), CallKit/AVAudioSession have hard OS restrictions on call audio interception. |
| Cellular call interception | Neither Android nor iOS public APIs allow arbitrary interception/injection of cellular call audio — any real implementation needs a VoIP/SIP bridge architecture, which is its own project. |
| Custom virtual audio driver | Kernel-mode audio drivers need WHQL signing and a driver-development toolchain; MVP relies on an existing user-installed virtual cable instead. |
| Voice cloning / voice preservation | Requires enrollment, consent workflow, secure biometric storage, and a cloning-capable TTS provider — a distinct compliance-sensitive subsystem layered on top of the provider abstraction later. |
| Multi-tenant cloud backend, billing, auth | No product users yet; premature before the core translation loop is proven to work well. |
| Non-Azure providers | Provider interfaces support swapping later; only one implementation is built now to avoid spreading effort thin. |

## Technology choice: C# / .NET 8 (WPF) + NAudio + Azure Speech SDK

- **.NET 8 WPF**: mature Windows desktop UI framework, first-class WASAPI access via
  NAudio, and the Azure Speech SDK ships an official .NET binding.
- **NAudio**: de facto standard .NET audio library — device enumeration, WASAPI
  capture/render, loopback capture.
- **Azure AI Speech SDK**: one vendor, one connection type
  (`TranslationRecognizer`) covers streaming ASR + translation + synthesis for the
  en↔de pair with built-in low-latency streaming, avoiding hand-rolled
  orchestration across three separate REST APIs for the MVP.

This is a decision, not a permanent commitment — the provider interfaces exist
specifically so a future phase can add Google/local Whisper/local NLLB without
touching the pipeline.
