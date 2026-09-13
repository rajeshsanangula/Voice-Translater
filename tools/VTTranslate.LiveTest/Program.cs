using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using VTTranslate.Core.Audio;
using VTTranslate.Core.Config;
using VTTranslate.Core.Providers;
using VTTranslate.Core.Session;

// VT Translate — live pipeline test harness.
//
// This is NOT a substitute for physically speaking into a microphone. It exercises the
// real production classes (AzureSpeechTranslationProvider, DirectionPipeline,
// LatencyBreakdown) against real Azure Speech APIs, using a test-only IAudioInputSource
// (TestFileAudioInputSource) that feeds a real WAV file in place of live mic acoustics.
// See docs/test-methodology.md for exactly what this does and does not prove.

var command = args.Length > 0 ? args[0] : "help";

switch (command)
{
    case "devices":
        PrintDevices();
        break;
    case "gen-test-audio":
        await GenerateTestAudioAsync();
        break;
    case "pipeline-test":
        await RunPipelineTestAsync();
        break;
    case "route-test":
        RunRouteTest(args.Length > 1 ? args[1] : "CABLE Input", args.Length > 2 ? int.Parse(args[2]) : 8);
        break;
    case "concurrent-test":
        await RunConcurrentTestAsync();
        break;
    case "cancel-test":
        await RunCancelTestAsync();
        break;
    case "restart-loop-test":
        await RunRestartLoopTestAsync(args.Length > 1 ? int.Parse(args[1]) : 5);
        break;
    case "mic-capture-test":
        RunMicCaptureTest(args.Length > 1 ? args[1] : "Headset (Noise Airwave Max 5)", args.Length > 2 ? int.Parse(args[2]) : 5);
        break;
    case "cable-loopback-test":
        RunCableLoopbackTest();
        break;
    case "output-endpoint-test":
        RunRouteTest(args.Length > 1 ? args[1] : "Headphones (Noise Airwave Max 5)", args.Length > 2 ? int.Parse(args[2]) : 5);
        break;
    case "speaker-loopback-test":
        await RunSpeakerLoopbackTestAsync();
        break;
    case "session-inspect":
        RunSessionInspect(args.Length > 1 ? args[1] : "Speakers (3- Cirrus Logic");
        break;
    case "capture-and-save":
        RunCaptureAndSave(args.Length > 1 ? args[1] : "Speakers (3- Cirrus Logic", args.Length > 2 ? int.Parse(args[2]) : 15);
        break;
    case "feed-wav-to-azure":
        await RunFeedWavToAzureAsync(args[1], args.Length > 2 ? args[2] : "de-DE", args.Length > 3 ? args[3] : "en-US");
        break;
    case "silent-mic-azure-test":
        await RunSilentMicAzureTestAsync(args.Length > 1 ? args[1] : "Headset (Noise Airwave Max 5)", args.Length > 2 ? int.Parse(args[2]) : 25);
        break;
    case "mic-isolation-test":
        RunMicIsolationTest(args.Length > 1 ? args[1] : "Headset (Noise Airwave Max 5)", args.Length > 2 ? args[2] : "Headphones (Noise Airwave Max 5)");
        break;
    case "outbound-feedback-test":
        RunOutboundFeedbackTest(args.Length > 1 ? args[1] : "Headset (Noise Airwave Max 5)");
        break;
    case "mic-baseline-distribution-test":
        RunMicBaselineDistributionTest(args.Length > 1 ? args[1] : "Headset (Noise Airwave Max 5)", args.Length > 2 ? int.Parse(args[2]) : 30);
        break;
    case "isolation-repeat-test":
        RunIsolationRepeatTest(args.Length > 1 ? args[1] : "CABLE Input", args.Length > 2 ? args[2] : "Headset (Noise Airwave Max 5)", args.Length > 3 ? int.Parse(args[3]) : 3);
        break;
    case "bt-profile-test":
        RunBtProfileTest();
        break;
    case "silent-mic-azure-inspect-test":
        await RunSilentMicAzureInspectTestAsync(args.Length > 1 ? args[1] : "Headset (Noise Airwave Max 5)", args.Length > 2 ? int.Parse(args[2]) : 25);
        break;
    case "gen-measurement-audio":
        await GenerateMeasurementAudioAsync();
        break;
    case "measurement-test":
        await RunMeasurementTestAsync();
        break;
    case "shadow-test":
        await RunShadowTestAsync();
        break;
    case "gen-step4-audio":
        await GenerateStep4AudioAsync();
        break;
    case "commit-policy-test":
        await RunCommitPolicyTestAsync();
        break;
    case "case-e-investigate":
        await InvestigateCaseEAsync();
        break;
    case "gen-step5-audio":
        await GenerateStep5AudioAsync();
        break;
    case "translation-shadow-test":
        await RunTranslationShadowTestAsync();
        break;
    case "translation-provider-feasibility-test":
        await RunTranslationProviderFeasibilityTestAsync();
        break;
    case "translator-credential-verify":
        await VerifyTranslatorCredentialsAsync();
        break;
    case "streaming-translation-decision-test":
        await RunStreamingTranslationDecisionTestAsync();
        break;
    case "semantic-segment-translation-test":
        await RunSemanticSegmentTranslationTestAsync();
        break;
    case "provisional-translation-architecture-test":
        await RunProvisionalTranslationArchitectureTestAsync();
        break;
    case "context-aware-translation-test":
        await RunContextAwareTranslationTestAsync();
        break;
    case "context-window-optimization-test":
        await RunContextWindowOptimizationTestAsync();
        break;
    case "streaming-tts-feasibility-test":
        await RunStreamingTtsFeasibilityTestAsync();
        break;
    case "conversational-streaming-pipeline-test":
        await RunConversationalStreamingPipelineTestAsync();
        break;
    case "translation-naturalization-test":
        await RunTranslationNaturalizationTestAsync();
        break;
    case "translation-naturalization-gemini-test":
        await RunTranslationNaturalizationGeminiTestAsync();
        break;
    default:
        Console.WriteLine("Usage: VTTranslate.LiveTest <devices|gen-test-audio|pipeline-test|route-test [deviceNameContains] [seconds]|concurrent-test|cancel-test|restart-loop-test [cycles]>");
        break;
}

static void RunRouteTest(string deviceNameContains, int seconds)
{
    // Does NOT require Azure — this tests only the OS-level audio path our app relies
    // on: AudioPlaybackSink (the exact production class) writing PCM to a virtual
    // output device (e.g. VB-CABLE's "CABLE Input"), which a browser can then pick up
    // as a microphone via "CABLE Output". Run this while a browser tab has a WebRTC
    // mic-test page open with the CABLE Output device selected, and watch its level
    // meter — that's the real routing test, not just "the device appears in a list".
    var outputDevices = AudioDeviceCatalog.GetOutputDevices();
    var target = outputDevices.FirstOrDefault(d => d.Name.Contains(deviceNameContains, StringComparison.OrdinalIgnoreCase));
    if (target == null)
    {
        Console.WriteLine($"No output device found matching '{deviceNameContains}'. Available:");
        foreach (var d in outputDevices) Console.WriteLine($"  {d.Name}");
        return;
    }

    Console.WriteLine($"Playing a {seconds}s 440Hz test tone into: {target.Name}");
    Console.WriteLine("(Switch to your browser now and check the mic level meter with CABLE Output selected as input.)");

    const int sampleRate = 16000;
    var sink = new VTTranslate.Core.Audio.AudioPlaybackSink(target.Id);
    sink.Start(sampleRate);

    var chunkMs = 100;
    var samplesPerChunk = sampleRate * chunkMs / 1000;
    var totalChunks = seconds * 1000 / chunkMs;
    double phase = 0;
    const double freq = 440.0;

    for (int c = 0; c < totalChunks; c++)
    {
        var buffer = new byte[samplesPerChunk * 2];
        for (int i = 0; i < samplesPerChunk; i++)
        {
            var sample = (short)(Math.Sin(phase) * short.MaxValue * 0.5);
            phase += 2 * Math.PI * freq / sampleRate;
            buffer[i * 2] = (byte)(sample & 0xFF);
            buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        sink.EnqueueAudio(buffer);
        Thread.Sleep(chunkMs);
        Console.Write(".");
    }
    Console.WriteLine();

    sink.Stop();
    sink.Dispose();
    Console.WriteLine("Route test tone finished.");
}

static void PrintDevices()
{
    Console.WriteLine("=== Input devices (microphones) ===");
    foreach (var d in AudioDeviceCatalog.GetInputDevices())
        Console.WriteLine($"  [{d.Kind}] {(d.IsDefault ? "*" : " ")} {d.Name}  id={d.Id}");

    Console.WriteLine();
    Console.WriteLine("=== Output/render devices (also loopback sources) ===");
    foreach (var d in AudioDeviceCatalog.GetOutputDevices())
        Console.WriteLine($"  [{d.Kind}] {(d.IsDefault ? "*" : " ")} {d.Name}  id={d.Id}");
}

static (string? key, string? region) RequireAzureConfig()
{
    var settings = AppSettings.Load();
    if (!settings.IsProviderConfigured)
    {
        Console.Error.WriteLine("AZURE_SPEECH_KEY / AZURE_SPEECH_REGION are not set. Set them (User scope) and restart the shell.");
        Environment.Exit(1);
    }
    return (settings.AzureSpeechKey, settings.AzureSpeechRegion);
}

static async Task GenerateTestAudioAsync()
{
    var (key, region) = RequireAzureConfig();
    var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results", "input-audio");
    Directory.CreateDirectory(outDir);

    var phrases = new[]
    {
        ("en-US-JennyNeural", "en-US", "I would like to schedule a meeting tomorrow.", "test1_en.wav"),
        ("de-DE-KatjaNeural", "de-DE", "Ich möchte morgen ein Treffen vereinbaren.", "test2_de.wav"),
        ("en-US-JennyNeural", "en-US", "Silence check.", "test3_en_short.wav"),
        ("en-US-JennyNeural", "en-US", "The quarterly revenue projection for twenty twenty six is four point seven million dollars, an increase of twelve percent.", "test4_en_numbers.wav"),
        ("de-DE-KatjaNeural", "de-DE", "Die Geschwindigkeitsbegrenzung und die Lebensversicherungsgesellschaft haben unterschiedliche Anforderungen.", "test5_de_compound.wav"),
        ("en-US-JennyNeural", "en-US", "Please configure the Kubernetes cluster and update the API gateway before the deployment pipeline runs.", "test6_en_technical.wav")
    };

    foreach (var (voice, lang, text, fileName) in phrases)
    {
        var config = SpeechConfig.FromSubscription(key, region);
        config.SpeechSynthesisVoiceName = voice;
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);

        var path = Path.Combine(outDir, fileName);
        using var audioConfig = AudioConfig.FromWavFileOutput(path);
        using var synth = new SpeechSynthesizer(config, audioConfig);
        var result = await synth.SpeakTextAsync(text);

        if (result.Reason == ResultReason.SynthesizingAudioCompleted)
            Console.WriteLine($"Generated {fileName}: \"{text}\" ({lang}, {voice})");
        else
            Console.WriteLine($"FAILED to generate {fileName}: {result.Reason} {result.Properties.GetProperty(PropertyId.CancellationDetails_ReasonDetailedText)}");
    }
}

static async Task RunPipelineTestAsync()
{
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var outputDir = Path.Combine(testResultsRoot, "output-audio");
    Directory.CreateDirectory(outputDir);

    var cases = new[]
    {
        new TestCase("test1_en_to_de", Path.Combine(inputDir, "test1_en.wav"), "en-US", "de-DE",
            "I would like to schedule a meeting tomorrow.", "de-DE-KatjaNeural"),
        new TestCase("test2_de_to_en", Path.Combine(inputDir, "test2_de.wav"), "de-DE", "en-US",
            "Ich möchte morgen ein Treffen vereinbaren.", "en-US-JennyNeural"),
        new TestCase("test4_en_numbers", Path.Combine(inputDir, "test4_en_numbers.wav"), "en-US", "de-DE",
            "The quarterly revenue projection for twenty twenty six is four point seven million dollars, an increase of twelve percent.", "de-DE-KatjaNeural"),
        new TestCase("test5_de_compound", Path.Combine(inputDir, "test5_de_compound.wav"), "de-DE", "en-US",
            "Die Geschwindigkeitsbegrenzung und die Lebensversicherungsgesellschaft haben unterschiedliche Anforderungen.", "en-US-JennyNeural"),
        new TestCase("test6_en_technical", Path.Combine(inputDir, "test6_en_technical.wav"), "en-US", "de-DE",
            "Please configure the Kubernetes cluster and update the API gateway before the deployment pipeline runs.", "de-DE-KatjaNeural"),
    };

    var results = new List<TestResult>();

    foreach (var tc in cases)
    {
        if (!File.Exists(tc.WavPath))
        {
            Console.WriteLine($"SKIP {tc.Name}: input file missing ({tc.WavPath}). Run 'gen-test-audio' first.");
            continue;
        }

        Console.WriteLine($"--- Running {tc.Name} ---");
        var result = await RunOneCaseAsync(tc, key!, region!, outputDir);
        results.Add(result);

        Console.WriteLine($"  Expected: {tc.ExpectedSourceText}");
        Console.WriteLine($"  Actual ASR: {result.ActualTranscript}");
        Console.WriteLine($"  Actual translation: {result.ActualTranslation}");
        Console.WriteLine($"  Recognition+MT latency: {result.RecognitionMs:F0} ms | TTS latency: {result.SynthesisMs:F0} ms | End-to-end: {result.EndToEndMs:F0} ms");
        Console.WriteLine($"  Result: {(result.Passed ? "PASS" : "FAIL")} — {result.Notes}");
        Console.WriteLine();
    }

    WriteReport(testResultsRoot, results);
}

static async Task<TestResult> RunOneCaseAsync(TestCase tc, string key, string region, string outputDir)
{
    var provider = new AzureSpeechTranslationProvider(key, region, tc.TargetVoice);
    var source = new TestFileAudioInputSource(tc.WavPath);

    string? lastTranscript = null;
    string? lastTranslation = null;
    var audioChunks = new List<byte[]>();
    var latency = new LatencyBreakdown();
    var errors = new List<string>();
    var completionSignal = new TaskCompletionSource();
    var finalResultCount = 0;

    provider.PartialResult += (_, r) => latency.OnPartialResult();
    provider.FinalResult += (_, r) =>
    {
        Interlocked.Increment(ref finalResultCount);
        latency.OnFinalResult();
        lastTranscript = r.SourceText;
        lastTranslation = r.TranslatedText;
    };
    provider.AudioSynthesized += (_, a) =>
    {
        latency.OnAudioSynthesized();
        lock (audioChunks) audioChunks.Add(a.Pcm16);
        latency.OnPlaybackEnqueued(); // this harness has no real playback sink — "enqueue" here just means "we received it," matching the new T5->T6 stage model
    };
    provider.Error += (_, e) => { lock (errors) errors.Add($"{(e.IsFatal ? "FATAL" : "warn")}: {e.Message}"); };
    source.PcmChunkCaptured += (_, chunk) => { latency.OnCaptureChunkForwarded(); provider.PushAudio(chunk); };
    source.PlaybackCompleted += (_, _) => completionSignal.TrySetResult();
    source.CaptureError += (_, ex) => { lock (errors) errors.Add($"FATAL: capture error: {ex.Message}"); };

    await provider.StartAsync(tc.SourceLang, tc.TargetLang, CancellationToken.None);
    source.Start();

    await Task.WhenAny(completionSignal.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    // Give the recognizer a moment after end-of-file to finalize the last segment and synthesize.
    await Task.Delay(3000);

    source.Stop();
    await provider.StopAsync();
    await provider.DisposeAsync();

    if (audioChunks.Count > 0)
    {
        var outPath = Path.Combine(outputDir, $"{tc.Name}_output.wav");
        WriteWav(outPath, audioChunks, 16000);
    }

    var snap = latency.EndToEnd.Snapshot();
    var recSnap = latency.RecognitionAndTranslation.Snapshot();
    var synSnap = latency.Synthesis.Snapshot();

    var transcriptOk = lastTranscript != null && TextSimilar(lastTranscript, tc.ExpectedSourceText);
    var audioProduced = audioChunks.Count > 0;
    var fatalError = errors.Any(e => e.StartsWith("FATAL"));
    var passed = transcriptOk && audioProduced && !fatalError;

    var notes = fatalError ? string.Join("; ", errors)
        : !audioProduced ? "No TTS audio was produced."
        : !transcriptOk ? "ASR transcript did not reasonably match the expected phrase."
        : "OK";

    return new TestResult(
        tc.Name, tc.SourceLang, tc.TargetLang, tc.ExpectedSourceText,
        lastTranscript ?? "(none)", lastTranslation ?? "(none)",
        recSnap.LastMs, synSnap.LastMs, snap.LastMs, passed, notes, errors, finalResultCount);
}

static async Task RunConcurrentTestAsync()
{
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var outputDir = Path.Combine(testResultsRoot, "output-audio");
    Directory.CreateDirectory(outputDir);

    var enToDe = new TestCase("concurrent_en_to_de", Path.Combine(inputDir, "test1_en.wav"), "en-US", "de-DE",
        "I would like to schedule a meeting tomorrow.", "de-DE-KatjaNeural");
    var deToEn = new TestCase("concurrent_de_to_en", Path.Combine(inputDir, "test2_de.wav"), "de-DE", "en-US",
        "Ich möchte morgen ein Treffen vereinbaren.", "en-US-JennyNeural");

    if (!File.Exists(enToDe.WavPath) || !File.Exists(deToEn.WavPath))
    {
        Console.WriteLine("Input WAVs missing. Run 'gen-test-audio' first.");
        return;
    }

    var proc = System.Diagnostics.Process.GetCurrentProcess();
    var cpuBefore = proc.TotalProcessorTime;
    var memBefore = GC.GetTotalMemory(forceFullCollection: false);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    Console.WriteLine("--- Running BOTH directions concurrently (Acceptance Test 3) ---");
    Exception? thrown = null;
    TestResult? enResult = null, deResult = null;
    try
    {
        var enTask = RunOneCaseAsync(enToDe, key!, region!, outputDir);
        var deTask = RunOneCaseAsync(deToEn, key!, region!, outputDir);
        await Task.WhenAll(enTask, deTask);
        enResult = enTask.Result;
        deResult = deTask.Result;
    }
    catch (Exception ex)
    {
        thrown = ex;
    }
    sw.Stop();

    proc.Refresh();
    var cpuAfter = proc.TotalProcessorTime;
    var memAfter = GC.GetTotalMemory(forceFullCollection: false);

    Console.WriteLine();
    Console.WriteLine($"Wall time for both concurrent directions: {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"Process CPU time consumed during test: {(cpuAfter - cpuBefore).TotalSeconds:F1}s");
    Console.WriteLine($"Managed memory delta: {(memAfter - memBefore) / 1024.0:F0} KB");
    Console.WriteLine($"No deadlock/crash: {(thrown == null ? "CONFIRMED (Task.WhenAll completed)" : $"FAILED — {thrown}")}");

    if (thrown == null && enResult != null && deResult != null)
    {
        var enOk = enResult.Passed;
        var deOk = deResult.Passed;
        // Cross-contamination check: the EN->DE transcript must not equal the German
        // phrase and vice versa (that would mean the two directions' audio/results
        // got swapped or mixed).
        var enTranscriptIsGerman = enResult.ActualTranscript.Contains("Treffen", StringComparison.OrdinalIgnoreCase);
        var deTranscriptIsEnglish = deResult.ActualTranscript.Contains("schedule", StringComparison.OrdinalIgnoreCase);
        var noCrossContamination = !enTranscriptIsGerman && !deTranscriptIsEnglish;
        var noDuplicateFinals = enResult.FinalResultCount <= 3 && deResult.FinalResultCount <= 3; // a short single sentence should finalize once, allow slack for segmentation

        Console.WriteLine();
        Console.WriteLine($"EN->DE result: {(enOk ? "PASS" : "FAIL")} — transcript: \"{enResult.ActualTranscript}\" -> \"{enResult.ActualTranslation}\" ({enResult.FinalResultCount} final result(s))");
        Console.WriteLine($"DE->EN result: {(deOk ? "PASS" : "FAIL")} — transcript: \"{deResult.ActualTranscript}\" -> \"{deResult.ActualTranslation}\" ({deResult.FinalResultCount} final result(s))");
        Console.WriteLine($"No cross-direction contamination: {(noCrossContamination ? "CONFIRMED" : "FAILED — one direction's transcript contains the other language's expected content")}");
        Console.WriteLine($"No excessive duplicate FinalResult events: {(noDuplicateFinals ? "CONFIRMED" : "FAILED — unexpectedly high final-result count, possible duplication")}");

        var overallPass = enOk && deOk && noCrossContamination && noDuplicateFinals;
        Console.WriteLine();
        Console.WriteLine($"ACCEPTANCE TEST 3 (concurrent bidirectional): {(overallPass ? "PASS" : "FAIL")}");

        var report = new
        {
            WallTimeSeconds = sw.Elapsed.TotalSeconds,
            CpuTimeSeconds = (cpuAfter - cpuBefore).TotalSeconds,
            ManagedMemoryDeltaKb = (memAfter - memBefore) / 1024.0,
            NoDeadlockOrCrash = thrown == null,
            EnToDe = enResult,
            DeToEn = deResult,
            NoCrossContamination = noCrossContamination,
            NoExcessiveDuplicateFinals = noDuplicateFinals,
            OverallPass = overallPass
        };
        File.WriteAllText(Path.Combine(testResultsRoot, "concurrent-test-report.json"),
            System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Report written to {Path.Combine(testResultsRoot, "concurrent-test-report.json")}");
    }
}

static bool TextSimilar(string actual, string expected)
{
    string Normalize(string s) => new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
    var a = Normalize(actual);
    var e = Normalize(expected);
    if (a == e) return true;
    // Loose containment check — real ASR output legitimately varies in punctuation/casing.
    var aWords = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var eWords = e.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var overlap = aWords.Intersect(eWords).Count();
    return eWords.Length > 0 && (double)overlap / eWords.Length >= 0.6;
}

static void WriteWav(string path, List<byte[]> chunks, int sampleRate)
{
    using var fs = new FileStream(path, FileMode.Create);
    using var writer = new BinaryWriter(fs);
    var dataLength = chunks.Sum(c => c.Length);

    writer.Write("RIFF".ToCharArray());
    writer.Write(36 + dataLength);
    writer.Write("WAVE".ToCharArray());
    writer.Write("fmt ".ToCharArray());
    writer.Write(16);
    writer.Write((short)1); // PCM
    writer.Write((short)1); // mono
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2); // byte rate
    writer.Write((short)2); // block align
    writer.Write((short)16); // bits per sample
    writer.Write("data".ToCharArray());
    writer.Write(dataLength);
    foreach (var chunk in chunks) writer.Write(chunk);
}

static void WriteReport(string testResultsRoot, List<TestResult> results)
{
    var jsonPath = Path.Combine(testResultsRoot, "pipeline-test-report.json");
    var mdPath = Path.Combine(testResultsRoot, "pipeline-test-report.md");

    var json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(jsonPath, json);

    var md = new System.Text.StringBuilder();
    md.AppendLine("# Pipeline Test Report (Azure file-injection)");
    md.AppendLine();
    md.AppendLine($"Generated: {DateTimeOffset.UtcNow:u}");
    md.AppendLine();
    md.AppendLine("| Case | Source→Target | Expected | Actual ASR | Actual Translation | Rec+MT ms | TTS ms | E2E ms | Result |");
    md.AppendLine("|---|---|---|---|---|---|---|---|---|");
    foreach (var r in results)
    {
        md.AppendLine($"| {r.Name} | {r.SourceLang}→{r.TargetLang} | {r.Expected} | {r.ActualTranscript} | {r.ActualTranslation} | {r.RecognitionMs:F0} | {r.SynthesisMs:F0} | {r.EndToEndMs:F0} | {(r.Passed ? "PASS" : "FAIL")} |");
    }
    md.AppendLine();
    md.AppendLine($"**{results.Count(r => r.Passed)}/{results.Count} passed.**");
    File.WriteAllText(mdPath, md.ToString());

    Console.WriteLine($"Report written to {mdPath} and {jsonPath}");
}

static async Task RunCancelTestAsync()
{
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var wavPath = Path.Combine(testResultsRoot, "input-audio", "test4_en_numbers.wav"); // a longer clip, so we can genuinely interrupt it mid-stream
    if (!File.Exists(wavPath))
    {
        Console.WriteLine("Input WAV missing. Run 'gen-test-audio' first.");
        return;
    }

    Console.WriteLine("--- Phase 1: start a real session, cancel it mid-stream ---");
    var provider1 = new AzureSpeechTranslationProvider(key!, region!, "de-DE-KatjaNeural");
    var source1 = new TestFileAudioInputSource(wavPath);
    // Two windows are tracked separately, because Azure's StopContinuousRecognitionAsync
    // is documented to gracefully flush any recognition already in flight before it
    // returns — events firing DURING that drain are expected, not a bug. The invariant
    // that actually matters is: nothing fires AFTER shutdown has fully completed.
    var eventsDuringDrain = 0;
    var eventsAfterFullShutdown = 0;
    var shutdownComplete = false;
    var stopRequested = false;
    var gotAnyPartial = new TaskCompletionSource();

    void CountEvent()
    {
        if (shutdownComplete) Interlocked.Increment(ref eventsAfterFullShutdown);
        else if (stopRequested) Interlocked.Increment(ref eventsDuringDrain);
    }

    provider1.PartialResult += (_, _) => { CountEvent(); gotAnyPartial.TrySetResult(); };
    provider1.FinalResult += (_, _) => CountEvent();
    provider1.AudioSynthesized += (_, _) => CountEvent();
    provider1.Error += (_, e) => Console.WriteLine($"  [provider1 event during/after stop] {(e.IsFatal ? "FATAL" : "warn")}: {e.Message}");

    source1.PcmChunkCaptured += (_, chunk) => provider1.PushAudio(chunk);

    await provider1.StartAsync("en-US", "de-DE", CancellationToken.None);
    source1.Start();

    // Wait for real recognition to actually begin (first partial), so the cancel is
    // genuinely mid-stream, not before anything happened.
    await Task.WhenAny(gotAnyPartial.Task, Task.Delay(TimeSpan.FromSeconds(10)));
    Console.WriteLine("  Recognition is underway. Issuing Stop() now (mid-utterance)...");

    stopRequested = true;
    var stopSw = System.Diagnostics.Stopwatch.StartNew();
    await provider1.StopAsync();
    await provider1.DisposeAsync();
    stopSw.Stop();
    shutdownComplete = true;
    source1.Stop();
    source1.Dispose();

    Console.WriteLine($"  StopAsync + DisposeAsync completed in {stopSw.ElapsedMilliseconds}ms.");
    Console.WriteLine($"  Events during graceful stop-drain (expected, not a bug): {eventsDuringDrain}");

    // Give any stray background callback a real window to misbehave before we check.
    await Task.Delay(TimeSpan.FromSeconds(5));
    var eventsAfterStop = eventsAfterFullShutdown;
    Console.WriteLine($"  Events observed AFTER shutdown fully completed: {eventsAfterStop} (must be 0 for a clean shutdown)");

    Console.WriteLine();
    Console.WriteLine("--- Phase 2: start a brand-new session afterward, confirm it works cleanly ---");
    var enToDe = new TestCase("cancel_test_restart", Path.Combine(testResultsRoot, "input-audio", "test1_en.wav"), "en-US", "de-DE",
        "I would like to schedule a meeting tomorrow.", "de-DE-KatjaNeural");
    Directory.CreateDirectory(Path.Combine(testResultsRoot, "output-audio"));
    var restartResult = await RunOneCaseAsync(enToDe, key!, region!, Path.Combine(testResultsRoot, "output-audio"));

    Console.WriteLine($"  Restart session result: {(restartResult.Passed ? "PASS" : "FAIL")} — \"{restartResult.ActualTranscript}\" -> \"{restartResult.ActualTranslation}\"");

    var overallPass = eventsAfterStop == 0 && restartResult.Passed;
    Console.WriteLine();
    Console.WriteLine($"CANCELLATION/SHUTDOWN TEST: {(overallPass ? "PASS" : "FAIL")}");

    File.WriteAllText(Path.Combine(testResultsRoot, "cancel-test-report.json"), System.Text.Json.JsonSerializer.Serialize(new
    {
        StopAndDisposeMs = stopSw.ElapsedMilliseconds,
        EventsAfterStop = eventsAfterStop,
        CleanShutdown = eventsAfterStop == 0,
        RestartSessionResult = restartResult,
        OverallPass = overallPass
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}

static async Task RunRestartLoopTestAsync(int cycles)
{
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var outputDir = Path.Combine(testResultsRoot, "output-audio");
    Directory.CreateDirectory(outputDir);

    var wavPath = Path.Combine(inputDir, "test1_en.wav");
    if (!File.Exists(wavPath))
    {
        Console.WriteLine("Input WAV missing. Run 'gen-test-audio' first.");
        return;
    }

    Console.WriteLine($"--- Running {cycles} consecutive start/stop/restart cycles (Item 8) ---");
    var proc = System.Diagnostics.Process.GetCurrentProcess();
    var results = new List<(int Cycle, bool Passed, long ManagedMemBytes, double DurationMs)>();

    for (int i = 1; i <= cycles; i++)
    {
        var swCycle = System.Diagnostics.Stopwatch.StartNew();
        var tc = new TestCase($"restart_cycle_{i}", wavPath, "en-US", "de-DE",
            "I would like to schedule a meeting tomorrow.", "de-DE-KatjaNeural");
        var result = await RunOneCaseAsync(tc, key!, region!, outputDir);
        swCycle.Stop();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var mem = GC.GetTotalMemory(forceFullCollection: true);
        proc.Refresh();

        results.Add((i, result.Passed, mem, swCycle.Elapsed.TotalMilliseconds));
        Console.WriteLine($"  Cycle {i}/{cycles}: {(result.Passed ? "PASS" : "FAIL")} — \"{result.ActualTranscript}\" -> \"{result.ActualTranslation}\" | {swCycle.ElapsedMilliseconds}ms | managed mem after GC: {mem / 1024.0:F0} KB | working set: {proc.WorkingSet64 / 1024.0 / 1024.0:F1} MB");
    }

    var allPassed = results.All(r => r.Passed);
    var firstMem = results.First().ManagedMemBytes;
    var lastMem = results.Last().ManagedMemBytes;
    var memGrowthKb = (lastMem - firstMem) / 1024.0;

    Console.WriteLine();
    Console.WriteLine($"All {cycles} cycles passed: {(allPassed ? "YES" : "NO")}");
    Console.WriteLine($"Managed memory growth from cycle 1 to cycle {cycles} (after forced GC each time): {memGrowthKb:F0} KB");
    Console.WriteLine($"RESTART-LOOP TEST: {(allPassed ? "PASS" : "FAIL")}");

    File.WriteAllText(Path.Combine(testResultsRoot, "restart-loop-test-report.json"), System.Text.Json.JsonSerializer.Serialize(new
    {
        Cycles = cycles,
        AllPassed = allPassed,
        ManagedMemoryGrowthKb = memGrowthKb,
        PerCycle = results.Select(r => new { r.Cycle, r.Passed, ManagedMemKb = r.ManagedMemBytes / 1024.0, r.DurationMs })
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}

static void RunMicCaptureTest(string deviceNameContains, int seconds)
{
    // Test 1 (partial): proves the Bluetooth mic's WASAPI capture path is alive and
    // streaming correctly-formatted PCM. It does NOT prove intelligible speech reaches
    // Azure — that requires a human speaking, which this harness cannot do. Run
    // pipeline-test separately (file-injection) to prove Azure EN->DE itself works.
    var inputDevices = AudioDeviceCatalog.GetInputDevices();
    var target = inputDevices.FirstOrDefault(d => d.Name.Contains(deviceNameContains, StringComparison.OrdinalIgnoreCase));
    if (target == null)
    {
        Console.WriteLine($"No INPUT device found matching '{deviceNameContains}'. Available input devices:");
        foreach (var d in inputDevices) Console.WriteLine($"  [{d.Kind}] {d.Name}");
        Console.WriteLine("MIC CAPTURE TEST: BLOCKED (device not found as a capture endpoint)");
        return;
    }

    Console.WriteLine($"Capturing from: {target.Name} [{target.Kind}] for {seconds}s (target format: 16000Hz, 16-bit, mono PCM after internal resampling)");

    var source = new AudioCaptureSource(target.Id, CaptureKind.Microphone);
    var chunks = new List<byte[]>();
    Exception? captureError = null;
    source.PcmChunkCaptured += (_, chunk) => chunks.Add(chunk);
    source.CaptureError += (_, ex) => captureError = ex;

    source.Start();
    Thread.Sleep(seconds * 1000);
    source.Stop();
    source.Dispose();

    if (captureError != null)
    {
        Console.WriteLine($"Capture error: {captureError.Message}");
        Console.WriteLine("MIC CAPTURE TEST: FAIL");
        return;
    }

    var totalBytes = chunks.Sum(c => c.Length);
    var totalSamples = totalBytes / 2;
    double sumSquares = 0;
    short peak = 0;
    foreach (var chunk in chunks)
    {
        for (int i = 0; i + 1 < chunk.Length; i += 2)
        {
            var sample = (short)(chunk[i] | (chunk[i + 1] << 8));
            sumSquares += (double)sample * sample;
            if (Math.Abs((int)sample) > peak) peak = (short)Math.Abs((int)sample);
        }
    }
    var rms = totalSamples > 0 ? Math.Sqrt(sumSquares / totalSamples) : 0;

    Console.WriteLine($"Chunks received: {chunks.Count} | Total bytes: {totalBytes} | Expected approx bytes for {seconds}s @16kHz/16-bit/mono: {seconds * 16000 * 2}");
    Console.WriteLine($"RMS amplitude: {rms:F1} / 32768 | Peak amplitude: {peak} / 32768");
    Console.WriteLine(rms < 5 ? "(Near-silence — expected if nothing was spoken/no ambient noise; this only proves the capture channel is open, not that speech reaches it.)" : "(Non-trivial signal detected — real audio, not just silence, is reaching the capture buffer.)");

    var gotExpectedDataVolume = totalBytes > 0 && totalBytes >= (seconds * 16000 * 2) * 0.8; // allow some slack
    Console.WriteLine($"MIC CAPTURE TEST: {(gotExpectedDataVolume ? "PASS (capture channel open, correctly formatted, continuous data flow)" : "FAIL (data volume far below expected — capture path broken or starved)")}");
}

static void RunCableLoopbackTest()
{
    // Test 2: independently measures whether audio played into CABLE Input via the
    // production AudioPlaybackSink actually arrives at CABLE Output, by directly
    // WASAPI-capturing CABLE Output while playback happens — not relying on app status.
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var outputDir = Path.Combine(testResultsRoot, "output-audio");
    Directory.CreateDirectory(outputDir);
    var wavPath = Path.Combine(inputDir, "test2_de.wav");
    if (!File.Exists(wavPath))
    {
        Console.WriteLine($"'{wavPath}' not found. Run 'gen-test-audio' first (it synthesizes this German phrase via Azure TTS). CABLE LOOPBACK TEST: BLOCKED");
        return;
    }

    var outputDevices = AudioDeviceCatalog.GetOutputDevices();
    var cableInput = outputDevices.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
    var inputDevices = AudioDeviceCatalog.GetInputDevices();
    var cableOutput = inputDevices.FirstOrDefault(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));

    if (cableInput == null || cableOutput == null)
    {
        Console.WriteLine($"CABLE Input found: {cableInput != null} | CABLE Output found: {cableOutput != null}");
        Console.WriteLine("CABLE LOOPBACK TEST: BLOCKED (VB-CABLE endpoints not found)");
        return;
    }
    Console.WriteLine($"Playback target: {cableInput.Name} (render/playback endpoint)");
    Console.WriteLine($"Capture source: {cableOutput.Name} (capture/recording endpoint)");

    // Start capturing CABLE Output BEFORE playback begins, so we don't miss the start.
    var capture = new AudioCaptureSource(cableOutput.Id, CaptureKind.Microphone);
    var capturedChunks = new List<byte[]>();
    capture.PcmChunkCaptured += (_, chunk) => capturedChunks.Add(chunk);
    Exception? captureError = null;
    capture.CaptureError += (_, ex) => captureError = ex;
    capture.Start();
    Thread.Sleep(300); // let the capture stream stabilize before playback starts

    const int sampleRate = 16000;
    using (var reader = new NAudio.Wave.AudioFileReader(wavPath))
    using (var resampler = new NAudio.Wave.MediaFoundationResampler(reader, new NAudio.Wave.WaveFormat(sampleRate, 16, 1)) { ResamplerQuality = 60 })
    {
        var sink = new AudioPlaybackSink(cableInput.Id);
        sink.Start(sampleRate);
        var buf = new byte[3200];
        int read;
        Console.WriteLine("Playing German TTS audio into CABLE Input...");
        while ((read = resampler.Read(buf, 0, buf.Length)) > 0)
        {
            var chunk = new byte[read];
            Array.Copy(buf, chunk, read);
            sink.EnqueueAudio(chunk);
            Thread.Sleep(100);
        }
        Thread.Sleep(1000); // drain
        sink.Stop();
        sink.Dispose();
    }

    Thread.Sleep(500);
    capture.Stop();
    capture.Dispose();

    if (captureError != null)
    {
        Console.WriteLine($"Capture error on CABLE Output: {captureError.Message}");
        Console.WriteLine("CABLE LOOPBACK TEST: FAIL");
        return;
    }

    var totalBytes = capturedChunks.Sum(c => c.Length);
    var totalSamples = totalBytes / 2;
    double sumSquares = 0;
    short peak = 0;
    foreach (var chunk in capturedChunks)
    {
        for (int i = 0; i + 1 < chunk.Length; i += 2)
        {
            var sample = (short)(chunk[i] | (chunk[i + 1] << 8));
            sumSquares += (double)sample * sample;
            if (Math.Abs((int)sample) > peak) peak = (short)Math.Abs((int)sample);
        }
    }
    var rms = totalSamples > 0 ? Math.Sqrt(sumSquares / totalSamples) : 0;

    if (capturedChunks.Count > 0)
    {
        var capturePath = Path.Combine(outputDir, "cable_loopback_capture.wav");
        WriteWav(capturePath, capturedChunks, sampleRate);
        Console.WriteLine($"Captured audio saved to: {capturePath}");
    }

    Console.WriteLine($"Total captured bytes from CABLE Output: {totalBytes} | RMS amplitude: {rms:F1}/32768 | Peak: {peak}/32768");
    var signalPresent = rms > 50; // well above silence-floor noise for a real spoken TTS phrase
    Console.WriteLine($"CABLE LOOPBACK TEST: {(signalPresent ? "PASS (real signal independently measured on CABLE Output)" : "FAIL (no meaningful signal captured on CABLE Output — audio is not passing through the cable)")}");
}

static async Task RunSpeakerLoopbackTestAsync()
{
    // Test 4: plays the known German WAV through real physical Speakers, captures it
    // back via the production WASAPI-loopback AudioCaptureSource (SystemLoopback), and
    // feeds that captured audio through the real Azure DE->EN pipeline — full leg test.
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var wavPath = Path.Combine(inputDir, "test2_de.wav");
    if (!File.Exists(wavPath))
    {
        Console.WriteLine($"'{wavPath}' not found. Run 'gen-test-audio' first. SPEAKER LOOPBACK TEST: BLOCKED");
        return;
    }

    var outputDevices = AudioDeviceCatalog.GetOutputDevices();
    var speakers = outputDevices.FirstOrDefault(d => d.Name.Contains("Speakers (3- Cirrus Logic", StringComparison.OrdinalIgnoreCase));
    if (speakers == null)
    {
        Console.WriteLine("Speakers (3- Cirrus Logic XU) not found. SPEAKER LOOPBACK TEST: BLOCKED");
        return;
    }
    Console.WriteLine($"Playback + loopback-capture target: {speakers.Name}");

    var provider = new AzureSpeechTranslationProvider(key!, region!, "en-US-JennyNeural");
    string? transcript = null, translation = null;
    var errors = new List<string>();
    provider.FinalResult += (_, r) => { transcript = r.SourceText; translation = r.TranslatedText; };
    provider.Error += (_, e) => errors.Add($"{(e.IsFatal ? "FATAL" : "warn")}: {e.Message}");

    await provider.StartAsync("de-DE", "en-US", CancellationToken.None);

    var loopback = new AudioCaptureSource(speakers.Id, CaptureKind.SystemLoopback);
    long capturedBytes = 0;
    loopback.PcmChunkCaptured += (_, chunk) => { capturedBytes += chunk.Length; provider.PushAudio(chunk); };
    Exception? captureError = null;
    loopback.CaptureError += (_, ex) => captureError = ex;
    loopback.Start();
    Thread.Sleep(300);

    const int sampleRate = 16000;
    using (var reader = new NAudio.Wave.AudioFileReader(wavPath))
    using (var resampler = new NAudio.Wave.MediaFoundationResampler(reader, new NAudio.Wave.WaveFormat(sampleRate, 16, 1)) { ResamplerQuality = 60 })
    {
        var sink = new AudioPlaybackSink(speakers.Id);
        sink.Start(sampleRate);
        var buf = new byte[3200];
        int read;
        Console.WriteLine("Playing German WAV through physical Speakers...");
        while ((read = resampler.Read(buf, 0, buf.Length)) > 0)
        {
            var chunk = new byte[read];
            Array.Copy(buf, chunk, read);
            sink.EnqueueAudio(chunk);
            Thread.Sleep(100);
        }
        Thread.Sleep(3000); // let recognizer finalize
        sink.Stop();
        sink.Dispose();
    }

    loopback.Stop();
    loopback.Dispose();
    await provider.StopAsync();
    await provider.DisposeAsync();

    if (captureError != null)
        Console.WriteLine($"Loopback capture error: {captureError.Message}");

    Console.WriteLine($"Bytes captured via WASAPI loopback on Speakers: {capturedBytes}");
    Console.WriteLine($"ASR transcript: {transcript ?? "(none)"}");
    Console.WriteLine($"Translation: {translation ?? "(none)"}");
    if (errors.Count > 0) Console.WriteLine("Errors: " + string.Join("; ", errors));

    var expected = "Ich möchte morgen ein Treffen vereinbaren.";
    var transcriptOk = transcript != null && TextSimilar(transcript, expected);
    var fatalError = errors.Any(e => e.StartsWith("FATAL"));
    var passed = capturedBytes > 0 && transcriptOk && !fatalError;
    Console.WriteLine($"SPEAKER LOOPBACK TEST: {(passed ? "PASS" : "FAIL")}");
}

static void RunSessionInspect(string deviceNameContains)
{
    // Answers Part 1, item 1: is anything actually rendering audio to this device right
    // now (or in the last few seconds)? Independent of our own capture — reads the
    // device's live master peak meter directly via WASAPI, plus lists which process(es)
    // hold an audio session on it (so we can confirm it's Chrome/Meet, not something else).
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var devices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active);
    var device = devices.FirstOrDefault(d => d.FriendlyName.Contains(deviceNameContains, StringComparison.OrdinalIgnoreCase));
    if (device == null)
    {
        Console.WriteLine($"No render device found matching '{deviceNameContains}'.");
        return;
    }
    Console.WriteLine($"Device: {device.FriendlyName}  (State: {device.State})");

    var sessions = device.AudioSessionManager.Sessions;
    Console.WriteLine($"Active audio sessions on this device: {sessions.Count}");
    for (int i = 0; i < sessions.Count; i++)
    {
        var s = sessions[i];
        string procName = "(unknown)";
        try
        {
            var pid = (int)s.GetProcessID;
            if (pid > 0) procName = System.Diagnostics.Process.GetProcessById(pid).ProcessName;
        }
        catch { /* process may have exited between enumeration and lookup */ }
        Console.WriteLine($"  Session {i}: process='{procName}' state={s.State} peak={s.AudioMeterInformation.MasterPeakValue:F4}");
    }

    Console.WriteLine("Polling device-level master peak for 5 seconds (have the remote participant speak now if testing live)...");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    float maxPeak = 0;
    while (sw.ElapsedMilliseconds < 5000)
    {
        var peak = device.AudioMeterInformation.MasterPeakValue;
        if (peak > maxPeak) maxPeak = peak;
        Console.Write($"\r  peak: {peak:F4}   max-so-far: {maxPeak:F4}   ");
        Thread.Sleep(150);
    }
    Console.WriteLine();
    Console.WriteLine(maxPeak > 0.01
        ? $"RESULT: Real audio activity detected on '{device.FriendlyName}' (max peak {maxPeak:F4}). Something IS actively rendering to this device."
        : $"RESULT: No meaningful audio activity detected on '{device.FriendlyName}' during this 5s window (max peak {maxPeak:F4}).");
}

static void RunCaptureAndSave(string deviceNameContains, int seconds)
{
    // Part 1, items 2-5: independently captures real WASAPI loopback PCM from the
    // named render device (using the same, now-fixed, production AudioCaptureSource
    // class the app uses) and saves it so its content can be inspected/replayed,
    // rather than trusting app status alone.
    var outputDevices = AudioDeviceCatalog.GetOutputDevices();
    var target = outputDevices.FirstOrDefault(d => d.Name.Contains(deviceNameContains, StringComparison.OrdinalIgnoreCase));
    if (target == null)
    {
        Console.WriteLine($"No render device found matching '{deviceNameContains}'.");
        return;
    }

    Console.WriteLine($"Loopback-capturing '{target.Name}' for {seconds}s. HAVE THE REMOTE PARTICIPANT SPEAK GERMAN NOW.");
    var source = new AudioCaptureSource(target.Id, CaptureKind.SystemLoopback);
    var chunks = new List<byte[]>();
    Exception? captureError = null;
    source.PcmChunkCaptured += (_, chunk) => chunks.Add(chunk);
    source.CaptureError += (_, ex) => captureError = ex;

    var sw = System.Diagnostics.Stopwatch.StartNew();
    source.Start();
    for (int i = 0; i < seconds; i++)
    {
        Thread.Sleep(1000);
        Console.WriteLine($"  ...{i + 1}/{seconds}s");
    }
    source.Stop();
    sw.Stop();
    source.Dispose();

    if (captureError != null)
    {
        Console.WriteLine($"Capture error: {captureError.Message}");
        Console.WriteLine("CAPTURE-AND-SAVE: FAIL");
        return;
    }

    var totalBytes = chunks.Sum(c => c.Length);
    const int sampleRate = 16000;
    var actualDurationSec = totalBytes / 2.0 / sampleRate;
    double sumSquares = 0;
    short peak = 0;
    foreach (var chunk in chunks)
    {
        for (int i = 0; i + 1 < chunk.Length; i += 2)
        {
            var sample = (short)(chunk[i] | (chunk[i + 1] << 8));
            sumSquares += (double)sample * sample;
            if (Math.Abs((int)sample) > peak) peak = (short)Math.Abs((int)sample);
        }
    }
    var totalSamples = totalBytes / 2;
    var rms = totalSamples > 0 ? Math.Sqrt(sumSquares / totalSamples) : 0;

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var outputDir = Path.Combine(testResultsRoot, "output-audio");
    Directory.CreateDirectory(outputDir);
    var savePath = Path.Combine(outputDir, "meet_loopback_capture.wav");
    if (chunks.Count > 0)
        WriteWav(savePath, chunks, sampleRate);

    Console.WriteLine();
    Console.WriteLine($"Wall-clock capture window: {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"Captured audio duration (from byte count @16kHz/16-bit/mono): {actualDurationSec:F1}s");
    Console.WriteLine($"Sample rate: 16000 Hz | Channels: 1 (mono) | Bit depth: 16-bit");
    Console.WriteLine($"RMS amplitude: {rms:F1}/32768 | Peak amplitude: {peak}/32768");
    Console.WriteLine($"Saved to: {savePath}");
    Console.WriteLine(rms > 50
        ? "RESULT: Non-trivial signal captured — real audio reached the loopback capture."
        : "RESULT: Near-silence — no meaningful signal reached the loopback capture in this window.");
}

static async Task RunFeedWavToAzureAsync(string wavPath, string sourceLang, string targetLang)
{
    // Part 2: independently tests the captured WAV through the real Azure pipeline,
    // fully decoupled from Google Meet / the live capture moment.
    var (key, region) = RequireAzureConfig();
    if (!File.Exists(wavPath))
    {
        Console.WriteLine($"File not found: {wavPath}");
        return;
    }

    using var reader = new NAudio.Wave.AudioFileReader(wavPath);
    Console.WriteLine($"Source file: {wavPath}");
    Console.WriteLine($"Source format: {reader.WaveFormat.SampleRate}Hz, {reader.WaveFormat.Channels}ch, {reader.WaveFormat.BitsPerSample}-bit | Duration: {reader.TotalTime.TotalSeconds:F1}s");

    var voice = targetLang.StartsWith("de") ? "de-DE-KatjaNeural" : "en-US-JennyNeural";
    var provider = new AzureSpeechTranslationProvider(key!, region!, voice);
    string? transcript = null, translation = null;
    var errors = new List<string>();
    byte[]? synthesized = null;
    provider.FinalResult += (_, r) => { transcript = r.SourceText; translation = r.TranslatedText; };
    provider.AudioSynthesized += (_, a) => synthesized ??= a.Pcm16;
    provider.Error += (_, e) => errors.Add($"{(e.IsFatal ? "FATAL" : "warn")}: {e.Message}");

    await provider.StartAsync(sourceLang, targetLang, CancellationToken.None);

    var source = new TestFileAudioInputSource(wavPath);
    var completionSignal = new TaskCompletionSource();
    source.PcmChunkCaptured += (_, chunk) => provider.PushAudio(chunk);
    source.PlaybackCompleted += (_, _) => completionSignal.TrySetResult();
    source.Start();
    await Task.WhenAny(completionSignal.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    await Task.Delay(3000);

    source.Stop();
    await provider.StopAsync();
    await provider.DisposeAsync();

    Console.WriteLine($"ASR transcript: {transcript ?? "(none)"}");
    Console.WriteLine($"Translation: {translation ?? "(none)"}");
    Console.WriteLine($"TTS produced audio: {(synthesized != null ? $"YES ({synthesized.Length} bytes)" : "NO")}");
    if (errors.Count > 0) Console.WriteLine("Errors: " + string.Join("; ", errors));
    Console.WriteLine($"FEED-WAV-TO-AZURE: {(transcript != null && synthesized != null ? "PASS" : "FAIL")}");
}

static async Task RunSilentMicAzureTestAsync(string deviceNameContains, int seconds)
{
    // Diagnostics 1 & 2: SAFETY — this never constructs an AudioPlaybackSink to any
    // device. Synthesized TTS bytes (if Azure produces any despite silence) are
    // counted only, never played anywhere, so nothing can reach CABLE Input or Meet.
    var (key, region) = RequireAzureConfig();
    var inputDevices = AudioDeviceCatalog.GetInputDevices();
    var target = inputDevices.FirstOrDefault(d => d.Name.Contains(deviceNameContains, StringComparison.OrdinalIgnoreCase));
    if (target == null)
    {
        Console.WriteLine($"No input device found matching '{deviceNameContains}'.");
        return;
    }

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, $"silent-mic-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    Console.WriteLine("SAFETY: TTS output (if any) is counted only, never played to any device.");
    Console.WriteLine($"Capturing from '{target.Name}' for {seconds}s. DO NOT SPEAK.");
    Console.WriteLine($"Diagnostic log: {logPath}");

    var provider = new AzureSpeechTranslationProvider(key!, region!, "de-DE-KatjaNeural", logger);
    var recognitionEvents = new List<(DateTimeOffset Time, string Kind, int TextLength)>();
    var synthesizedCount = 0;
    provider.PartialResult += (_, r) => recognitionEvents.Add((DateTimeOffset.UtcNow, "partial", r.SourceText.Length));
    provider.FinalResult += (_, r) => recognitionEvents.Add((DateTimeOffset.UtcNow, "final", r.SourceText.Length));
    provider.AudioSynthesized += (_, _) => Interlocked.Increment(ref synthesizedCount); // discarded — never played
    var errors = new List<string>();
    provider.Error += (_, e) => errors.Add($"{(e.IsFatal ? "FATAL" : "warn")}: {e.Message}");

    await provider.StartAsync("en-US", "de-DE", CancellationToken.None);

    var source = new AudioCaptureSource(target.Id, CaptureKind.Microphone);
    var chunks = new List<byte[]>();
    source.PcmChunkCaptured += (_, chunk) => { chunks.Add(chunk); provider.PushAudio(chunk); };
    Exception? captureError = null;
    source.CaptureError += (_, ex) => captureError = ex;

    var sw = System.Diagnostics.Stopwatch.StartNew();
    source.Start();
    for (int i = 0; i < seconds; i++)
    {
        Thread.Sleep(1000);
        Console.Write(".");
    }
    Console.WriteLine();
    source.Stop();
    source.Dispose();
    sw.Stop();

    await Task.Delay(2000); // let any in-flight recognition finalize before we stop the provider
    await provider.StopAsync();
    await provider.DisposeAsync();

    if (captureError != null)
        Console.WriteLine($"Capture error: {captureError.Message}");

    var totalBytes = chunks.Sum(c => c.Length);
    double sumSquares = 0;
    short peak = 0;
    foreach (var chunk in chunks)
    {
        for (int i = 0; i + 1 < chunk.Length; i += 2)
        {
            var sample = (short)(chunk[i] | (chunk[i + 1] << 8));
            sumSquares += (double)sample * sample;
            if (Math.Abs((int)sample) > peak) peak = (short)Math.Abs((int)sample);
        }
    }
    var totalSamples = totalBytes / 2;
    var rms = totalSamples > 0 ? Math.Sqrt(sumSquares / totalSamples) : 0;
    var chunkRate = chunks.Count / Math.Max(sw.Elapsed.TotalSeconds, 0.001);

    Console.WriteLine();
    Console.WriteLine("=== Diagnostic 1 & 2 results ===");
    Console.WriteLine($"Wall time: {sw.Elapsed.TotalSeconds:F1}s | Chunks: {chunks.Count} ({chunkRate:F1}/s) | Total bytes: {totalBytes}");
    Console.WriteLine($"RMS: {rms:F1}/32768 | Peak: {peak}/32768");
    Console.WriteLine($"Non-silent samples present (peak > 0): {(peak > 0 ? "YES" : "NO")}");
    Console.WriteLine($"Azure EN->DE recognition events during this silent window: {recognitionEvents.Count}");
    foreach (var ev in recognitionEvents)
        Console.WriteLine($"  {ev.Time:O} kind={ev.Kind} textLength={ev.TextLength}");
    Console.WriteLine($"TTS-synthesized audio events (counted only, never played): {synthesizedCount}");
    if (errors.Count > 0) Console.WriteLine("Errors: " + string.Join("; ", errors));
    Console.WriteLine();
    Console.WriteLine("--- Full diagnostic log for this run (includes generation IDs, chunk-sent summaries) ---");
    Console.WriteLine(File.ReadAllText(logPath));
}

static void RunMicIsolationTest(string micNameContains, string playbackNameContains)
{
    // Diagnostic 3: does the mic's captured level change when something else is
    // actively playing on the system? A meaningful RMS jump during playback (with no
    // human speaking) would point at acoustic or OS-level contamination.
    var inputDevices = AudioDeviceCatalog.GetInputDevices();
    var mic = inputDevices.FirstOrDefault(d => d.Name.Contains(micNameContains, StringComparison.OrdinalIgnoreCase));
    var outputDevices = AudioDeviceCatalog.GetOutputDevices();
    var playback = outputDevices.FirstOrDefault(d => d.Name.Contains(playbackNameContains, StringComparison.OrdinalIgnoreCase));
    if (mic == null || playback == null)
    {
        Console.WriteLine($"Mic found: {mic != null} | Playback device found: {playback != null}");
        return;
    }

    (double rms, short peak, int chunkCount) CaptureFor(int seconds)
    {
        var source = new AudioCaptureSource(mic.Id, CaptureKind.Microphone);
        var chunks = new List<byte[]>();
        source.PcmChunkCaptured += (_, c) => chunks.Add(c);
        source.Start();
        Thread.Sleep(seconds * 1000);
        source.Stop();
        source.Dispose();

        var totalBytes = chunks.Sum(c => c.Length);
        double sumSquares = 0;
        short peak = 0;
        foreach (var c in chunks)
            for (int i = 0; i + 1 < c.Length; i += 2)
            {
                var s = (short)(c[i] | (c[i + 1] << 8));
                sumSquares += (double)s * s;
                if (Math.Abs((int)s) > peak) peak = (short)Math.Abs((int)s);
            }
        var totalSamples = totalBytes / 2;
        return (totalSamples > 0 ? Math.Sqrt(sumSquares / totalSamples) : 0, peak, chunks.Count);
    }

    Console.WriteLine($"--- Phase A: baseline, '{mic.Name}' capture with nothing playing to '{playback.Name}' (10s, stay silent) ---");
    var baseline = CaptureFor(10);
    Console.WriteLine($"Baseline: RMS={baseline.rms:F1} Peak={baseline.peak} Chunks={baseline.chunkCount}");

    Console.WriteLine($"--- Phase B: same mic capture WHILE playing a tone to '{playback.Name}' (10s, stay silent) ---");
    var toneTask = Task.Run(() => RunRouteTest(playback.Name, 10));
    var duringPlayback = CaptureFor(10);
    toneTask.Wait();
    Console.WriteLine($"During playback: RMS={duringPlayback.rms:F1} Peak={duringPlayback.peak} Chunks={duringPlayback.chunkCount}");

    Console.WriteLine();
    var delta = duringPlayback.rms - baseline.rms;
    Console.WriteLine($"RMS delta (during - baseline): {delta:F1}");
    Console.WriteLine(Math.Abs(delta) > 100
        ? "RESULT: Microphone level CHANGES meaningfully when system playback is active — possible contamination path (D)."
        : "RESULT: Microphone level does NOT meaningfully change with system playback — no evidence of playback-to-mic contamination.");
}

static void RunOutboundFeedbackTest(string micNameContains)
{
    // Diagnostic 4: plays German TTS into CABLE Input while independently watching the
    // Bluetooth mic's own capture level, in three phases (before/during/after), to
    // check for any leakage from our own generated audio into the EN->DE mic path.
    // SAFETY: this DOES send real audio to CABLE Input, which IS what Google Meet's
    // microphone reads from in the approved routing — do not run this while Meet is
    // actually connected to a live call with a real participant.
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var wavPath = Path.Combine(inputDir, "test2_de.wav");
    if (!File.Exists(wavPath))
    {
        Console.WriteLine("Run 'gen-test-audio' first.");
        return;
    }

    var inputDevices = AudioDeviceCatalog.GetInputDevices();
    var mic = inputDevices.FirstOrDefault(d => d.Name.Contains(micNameContains, StringComparison.OrdinalIgnoreCase));
    var outputDevices = AudioDeviceCatalog.GetOutputDevices();
    var cableInput = outputDevices.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
    if (mic == null || cableInput == null)
    {
        Console.WriteLine($"Mic found: {mic != null} | CABLE Input found: {cableInput != null}");
        return;
    }

    var micSource = new AudioCaptureSource(mic.Id, CaptureKind.Microphone);
    var beforeChunks = new List<byte[]>();
    var duringChunks = new List<byte[]>();
    var afterChunks = new List<byte[]>();
    var phase = "before";
    micSource.PcmChunkCaptured += (_, c) =>
    {
        var target = phase == "before" ? beforeChunks : phase == "during" ? duringChunks : afterChunks;
        lock (target) target.Add(c);
    };
    micSource.Start();

    Console.WriteLine("Phase BEFORE (3s, mic capturing, nothing on CABLE Input, stay silent)...");
    Thread.Sleep(3000);

    Console.WriteLine("Phase DURING (playing German TTS into CABLE Input while mic keeps capturing, stay silent)...");
    phase = "during";
    using (var reader = new NAudio.Wave.AudioFileReader(wavPath))
    using (var resampler = new NAudio.Wave.MediaFoundationResampler(reader, new NAudio.Wave.WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 })
    {
        var sink = new AudioPlaybackSink(cableInput.Id);
        sink.Start(16000);
        var buf = new byte[3200];
        int read;
        while ((read = resampler.Read(buf, 0, buf.Length)) > 0)
        {
            var chunk = new byte[read];
            Array.Copy(buf, chunk, read);
            sink.EnqueueAudio(chunk);
            Thread.Sleep(100);
        }
        Thread.Sleep(1000);
        sink.Stop();
        sink.Dispose();
    }

    Console.WriteLine("Phase AFTER (3s, mic capturing, CABLE Input silent again)...");
    phase = "after";
    Thread.Sleep(3000);

    micSource.Stop();
    micSource.Dispose();

    (double rms, short peak) Analyze(List<byte[]> chunks)
    {
        var totalBytes = chunks.Sum(c => c.Length);
        double sumSquares = 0;
        short peak = 0;
        foreach (var c in chunks)
            for (int i = 0; i + 1 < c.Length; i += 2)
            {
                var s = (short)(c[i] | (c[i + 1] << 8));
                sumSquares += (double)s * s;
                if (Math.Abs((int)s) > peak) peak = (short)Math.Abs((int)s);
            }
        var totalSamples = totalBytes / 2;
        return (totalSamples > 0 ? Math.Sqrt(sumSquares / totalSamples) : 0, peak);
    }

    var before = Analyze(beforeChunks);
    var during = Analyze(duringChunks);
    var after = Analyze(afterChunks);

    Console.WriteLine();
    Console.WriteLine($"BEFORE : RMS={before.rms:F1} Peak={before.peak} chunks={beforeChunks.Count}");
    Console.WriteLine($"DURING : RMS={during.rms:F1} Peak={during.peak} chunks={duringChunks.Count}");
    Console.WriteLine($"AFTER  : RMS={after.rms:F1} Peak={after.peak} chunks={afterChunks.Count}");
    Console.WriteLine();
    var contaminated = during.rms > Math.Max(before.rms, after.rms) * 3 + 50;
    Console.WriteLine(contaminated
        ? "RESULT: Mic RMS spiked while CABLE Input was playing — possible outbound feedback contamination (C)."
        : "RESULT: Mic RMS during CABLE Input playback is consistent with before/after — no evidence CABLE Input audio reaches this microphone.");
}

static List<(double Rms, short Peak)> PerChunkStats(List<byte[]> chunks)
{
    var results = new List<(double, short)>(chunks.Count);
    foreach (var c in chunks)
    {
        double sumSquares = 0;
        short peak = 0;
        var n = c.Length / 2;
        for (int i = 0; i + 1 < c.Length; i += 2)
        {
            var s = (short)(c[i] | (c[i + 1] << 8));
            sumSquares += (double)s * s;
            if (Math.Abs((int)s) > peak) peak = (short)Math.Abs((int)s);
        }
        results.Add((n > 0 ? Math.Sqrt(sumSquares / n) : 0, peak));
    }
    return results;
}

static double Percentile(List<double> sorted, double p)
{
    if (sorted.Count == 0) return 0;
    var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
    return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
}

static void RunMicBaselineDistributionTest(string micNameContains, int seconds)
{
    // Test A: not just one aggregate RMS/peak — the full per-chunk distribution, so we
    // can tell "quiet all the way through" from "quiet mostly, with a few loud bursts."
    var inputDevices = AudioDeviceCatalog.GetInputDevices();
    var mic = inputDevices.FirstOrDefault(d => d.Name.Contains(micNameContains, StringComparison.OrdinalIgnoreCase));
    if (mic == null) { Console.WriteLine($"Mic not found: '{micNameContains}'"); return; }

    Console.WriteLine($"Capturing '{mic.Name}' for {seconds}s. No Meet, no playback, no speech.");
    var source = new AudioCaptureSource(mic.Id, CaptureKind.Microphone);
    var chunks = new List<byte[]>();
    source.PcmChunkCaptured += (_, c) => chunks.Add(c);
    source.Start();
    for (int i = 0; i < seconds; i++) { Thread.Sleep(1000); Console.Write("."); }
    Console.WriteLine();
    source.Stop();
    source.Dispose();

    var stats = PerChunkStats(chunks);
    var rmsSorted = stats.Select(s => s.Rms).OrderBy(x => x).ToList();
    var peakSorted = stats.Select(s => (double)s.Peak).OrderBy(x => x).ToList();

    Console.WriteLine();
    Console.WriteLine($"Chunks: {chunks.Count}");
    Console.WriteLine($"RMS distribution:  min={rmsSorted.FirstOrDefault():F1}  p50={Percentile(rmsSorted, 0.5):F1}  p90={Percentile(rmsSorted, 0.9):F1}  p99={Percentile(rmsSorted, 0.99):F1}  max={rmsSorted.LastOrDefault():F1}");
    Console.WriteLine($"Peak distribution: min={peakSorted.FirstOrDefault():F0}  p50={Percentile(peakSorted, 0.5):F0}  p90={Percentile(peakSorted, 0.9):F0}  p99={Percentile(peakSorted, 0.99):F0}  max={peakSorted.LastOrDefault():F0}");
    Console.WriteLine();

    int[] thresholds = { 100, 500, 1000, 2000, 5000, 10000 };
    foreach (var t in thresholds)
    {
        var count = stats.Count(s => s.Peak > t);
        Console.WriteLine($"Chunks with peak > {t,6}: {count,4}/{chunks.Count} ({100.0 * count / Math.Max(chunks.Count, 1):F1}%)");
    }

    var burstChunks = stats.Count(s => s.Peak > 2000);
    var isIntermittent = burstChunks > 0 && burstChunks < chunks.Count * 0.5;
    Console.WriteLine();
    Console.WriteLine(isIntermittent
        ? $"RESULT: Intermittent — {burstChunks}/{chunks.Count} chunks exceed 2000, most don't. Consistent with real, sporadic ambient/voice-like activity, not constant electrical noise floor."
        : burstChunks == 0
            ? "RESULT: No chunk exceeded 2000 — genuinely quiet throughout this window."
            : "RESULT: Amplitude above 2000 for the majority of chunks — not intermittent, more like sustained noise or an active sound source.");
}

static void RunIsolationRepeatTest(string targetDeviceContains, string micNameContains, int repeats)
{
    // Tests B and C: plays the known German phrase into a target render device
    // (CABLE Input for Test B, Speakers for Test C) while independently capturing the
    // mic, repeated N times, looking for a REPEATABLE before/during/after pattern —
    // a one-off rise could be coincidental ambient noise; a rise on every single run
    // would not be.
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var wavPath = Path.Combine(testResultsRoot, "input-audio", "test2_de.wav");
    if (!File.Exists(wavPath)) { Console.WriteLine("Run 'gen-test-audio' first."); return; }

    var outputDevices = AudioDeviceCatalog.GetOutputDevices();
    var target = outputDevices.FirstOrDefault(d => d.Name.Contains(targetDeviceContains, StringComparison.OrdinalIgnoreCase));
    var inputDevices = AudioDeviceCatalog.GetInputDevices();
    var mic = inputDevices.FirstOrDefault(d => d.Name.Contains(micNameContains, StringComparison.OrdinalIgnoreCase));
    if (target == null || mic == null)
    {
        Console.WriteLine($"Target device found: {target != null} | Mic found: {mic != null}");
        return;
    }

    Console.WriteLine($"SAFETY: playing only to '{target.Name}'. Never to Google Meet.");
    Console.WriteLine($"Target: {target.Name} | Mic: {mic.Name} | Repeats: {repeats}");

    var runs = new List<(double before, double during, double after)>();

    for (int run = 1; run <= repeats; run++)
    {
        Console.WriteLine($"--- Run {run}/{repeats} (stay silent) ---");
        var micSource = new AudioCaptureSource(mic.Id, CaptureKind.Microphone);
        var beforeChunks = new List<byte[]>();
        var duringChunks = new List<byte[]>();
        var afterChunks = new List<byte[]>();
        var phase = "before";
        micSource.PcmChunkCaptured += (_, c) =>
        {
            var t = phase == "before" ? beforeChunks : phase == "during" ? duringChunks : afterChunks;
            lock (t) t.Add(c);
        };
        micSource.Start();
        Thread.Sleep(2000);

        phase = "during";
        using (var reader = new NAudio.Wave.AudioFileReader(wavPath))
        using (var resampler = new NAudio.Wave.MediaFoundationResampler(reader, new NAudio.Wave.WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 })
        {
            var sink = new AudioPlaybackSink(target.Id);
            sink.Start(16000);
            var buf = new byte[3200];
            int read;
            while ((read = resampler.Read(buf, 0, buf.Length)) > 0)
            {
                var chunk = new byte[read];
                Array.Copy(buf, chunk, read);
                sink.EnqueueAudio(chunk);
                Thread.Sleep(100);
            }
            Thread.Sleep(500);
            sink.Stop();
            sink.Dispose();
        }

        phase = "after";
        Thread.Sleep(2000);
        micSource.Stop();
        micSource.Dispose();

        double AvgRms(List<byte[]> c) { var s = PerChunkStats(c); return s.Count > 0 ? s.Average(x => x.Rms) : 0; }
        var br = AvgRms(beforeChunks);
        var dr = AvgRms(duringChunks);
        var ar = AvgRms(afterChunks);
        Console.WriteLine($"  BEFORE avgRMS={br:F1} (n={beforeChunks.Count})  DURING avgRMS={dr:F1} (n={duringChunks.Count})  AFTER avgRMS={ar:F1} (n={afterChunks.Count})");
        runs.Add((br, dr, ar));
    }

    Console.WriteLine();
    Console.WriteLine("=== Summary across all runs ===");
    for (int i = 0; i < runs.Count; i++)
        Console.WriteLine($"Run {i + 1}: before={runs[i].before:F1} during={runs[i].during:F1} after={runs[i].after:F1} delta(during-before)={runs[i].during - runs[i].before:F1}");

    // "Repeatable" means EVERY run shows a meaningful, consistent rise — not just the average.
    var consistentRise = runs.All(r => r.during > r.before * 1.5 + 20);
    Console.WriteLine();
    Console.WriteLine(consistentRise
        ? $"RESULT: DURING was meaningfully elevated above BEFORE on ALL {repeats} runs — repeatable correlation with '{target.Name}' playback found."
        : $"RESULT: NOT consistently elevated across all {repeats} runs — no repeatable correlation with '{target.Name}' playback; any single-run rise is more likely explained by ambient variability.");
}

static void RunBtProfileTest()
{
    // Test D: Windows can only have ONE Bluetooth audio profile active at a time for a
    // given device — opening a microphone capture on the HFP-profile "Headset" endpoint
    // is known to force a profile switch away from the high-quality A2DP "Headphones"
    // endpoint. If VT's own English TTS output is routed to "Headphones" (the same
    // physical Bluetooth radio as the mic), a mid-session profile switch could plausibly
    // degrade/reroute that audio at the OS level. This test proves or disproves the
    // switch directly via the render device's own reported mix format.
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();

    void ReportFormat(NAudio.CoreAudioApi.MMDevice device, string label)
    {
        try
        {
            var fmt = device.AudioClient.MixFormat;
            Console.WriteLine($"{label}: '{device.FriendlyName}' mix format = {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}-bit, encoding={fmt.Encoding}, State={device.State}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{label}: could not query '{device.FriendlyName}' ({ex.GetType().Name}: {ex.Message})");
        }
    }

    var renderDevices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active);
    var headphones = renderDevices.FirstOrDefault(d => d.FriendlyName.Contains("Headphones (Noise Airwave", StringComparison.OrdinalIgnoreCase));
    var captureDevices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active);
    var headsetMic = captureDevices.FirstOrDefault(d => d.FriendlyName.Contains("Headset (Noise Airwave", StringComparison.OrdinalIgnoreCase));

    if (headphones == null || headsetMic == null)
    {
        Console.WriteLine($"Headphones (A2DP render) found: {headphones != null} | Headset (HFP capture) found: {headsetMic != null}");
        return;
    }

    ReportFormat(headphones, "BEFORE opening mic");
    ReportFormat(headsetMic, "Headset mic native format");

    var capture = new AudioCaptureSource(headsetMic.ID, CaptureKind.Microphone);
    capture.Start();
    Thread.Sleep(1500); // allow any Bluetooth profile switch to settle

    ReportFormat(headphones, "DURING mic capture");

    capture.Stop();
    capture.Dispose();
    Thread.Sleep(1000);

    ReportFormat(headphones, "AFTER closing mic");
}

static async Task RunSilentMicAzureInspectTestAsync(string deviceNameContains, int seconds)
{
    // Test E: same as silent-mic-azure-test, but ALSO prints the actual recognized text
    // to the CONSOLE ONLY (never to the persistent diagnostic log, never saved to any
    // file) so the qualitative nature of any spurious recognition can be inspected
    // (fragment vs. repeated vs. speech-like) without persisting speech content anywhere.
    var (key, region) = RequireAzureConfig();
    var inputDevices = AudioDeviceCatalog.GetInputDevices();
    var target = inputDevices.FirstOrDefault(d => d.Name.Contains(deviceNameContains, StringComparison.OrdinalIgnoreCase));
    if (target == null) { Console.WriteLine($"No input device found matching '{deviceNameContains}'."); return; }

    Console.WriteLine("SAFETY: TTS output (if any) is counted only, never played to any device.");
    Console.WriteLine("NOTE: recognized text below is printed to console ONLY for this diagnostic — not written to any log or file.");
    Console.WriteLine($"Capturing from '{target.Name}' for {seconds}s. DO NOT SPEAK.");

    // No logger passed (NullDiagnosticLogger default) — this run's console-only text
    // inspection is intentionally separate from the persistent-log path.
    var provider = new AzureSpeechTranslationProvider(key!, region!, "de-DE-KatjaNeural");
    var events = new List<(DateTimeOffset Time, string Kind, string Text)>();
    provider.PartialResult += (_, r) => events.Add((DateTimeOffset.UtcNow, "partial", r.SourceText));
    provider.FinalResult += (_, r) => events.Add((DateTimeOffset.UtcNow, "final", r.SourceText));
    provider.AudioSynthesized += (_, _) => { }; // discarded — never played

    await provider.StartAsync("en-US", "de-DE", CancellationToken.None);

    var source = new AudioCaptureSource(target.Id, CaptureKind.Microphone);
    source.PcmChunkCaptured += (_, chunk) => provider.PushAudio(chunk);
    source.Start();
    for (int i = 0; i < seconds; i++) { Thread.Sleep(1000); Console.Write("."); }
    Console.WriteLine();
    source.Stop();
    source.Dispose();

    await Task.Delay(2000);
    await provider.StopAsync();
    await provider.DisposeAsync();

    Console.WriteLine();
    Console.WriteLine("=== Test E: recognized event content (console-only, not persisted) ===");
    Console.WriteLine($"Total events: {events.Count}");
    foreach (var ev in events)
        Console.WriteLine($"  {ev.Time:HH:mm:ss.fff} [{ev.Kind}] \"{ev.Text}\" (length={ev.Text.Length})");
    var distinctFinals = events.Where(e => e.Kind == "final").Select(e => e.Text).Distinct().Count();
    var totalFinals = events.Count(e => e.Kind == "final");
    Console.WriteLine($"Distinct final texts: {distinctFinals} / {totalFinals} total final events (repetition indicator).");
}

static async Task GenerateMeasurementAudioAsync()
{
    // Step 1 measurement audio — supplements gen-test-audio's existing normal-speech
    // phrases with fast (SSML prosody rate), short, long, self-correction, and
    // multi-utterance cases, per docs/design-notes/streaming-measurement-results.md.
    var (key, region) = RequireAzureConfig();
    var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results", "input-audio");
    Directory.CreateDirectory(outDir);

    async Task SynthSsml(string voice, string lang, string ssmlBody, string fileName, string description)
    {
        var config = SpeechConfig.FromSubscription(key, region);
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);
        var path = Path.Combine(outDir, fileName);
        using var audioConfig = AudioConfig.FromWavFileOutput(path);
        using var synth = new SpeechSynthesizer(config, audioConfig);
        var ssml = $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{lang}\">" +
                   $"<voice name=\"{voice}\">{ssmlBody}</voice></speak>";
        var result = await synth.SpeakSsmlAsync(ssml);
        Console.WriteLine(result.Reason == ResultReason.SynthesizingAudioCompleted
            ? $"Generated {fileName}: {description}"
            : $"FAILED to generate {fileName}: {result.Reason}");
    }

    // C/D: fast speech via SSML prosody rate — a real, reproducible way to control
    // speaking rate deterministically (Azure TTS does not perfectly emulate human
    // disfluent fast speech, but it reliably produces genuinely faster audio).
    await SynthSsml("en-US-JennyNeural", "en-US",
        "<prosody rate=\"+60%\">I would like to schedule a meeting tomorrow and also review the budget before Friday.</prosody>",
        "test_c_fast_en.wav", "fast EN (SSML rate +60%)");
    await SynthSsml("de-DE-KatjaNeural", "de-DE",
        "<prosody rate=\"+60%\">Ich möchte morgen ein Treffen vereinbaren und außerdem das Budget vor Freitag besprechen.</prosody>",
        "test_d_fast_de.wav", "fast DE (SSML rate +60%)");

    // E: short utterance, targeting well under ~250ms of actual speech.
    await SynthSsml("en-US-JennyNeural", "en-US",
        "<prosody rate=\"+80%\">No.</prosody>",
        "test_e_short_en.wav", "very short EN (SSML rate +80%)");
    await SynthSsml("de-DE-KatjaNeural", "de-DE",
        "<prosody rate=\"+80%\">Ja.</prosody>",
        "test_e_short_de.wav", "very short DE (SSML rate +80%)");

    // F: long utterance, normal rate.
    await SynthSsml("en-US-JennyNeural", "en-US",
        "I would like to schedule a meeting tomorrow to discuss the quarterly budget, review the outstanding action items from last week, and also confirm the travel arrangements for the conference next month.",
        "test_f_long_en.wav", "long EN, normal rate");
    await SynthSsml("de-DE-KatjaNeural", "de-DE",
        "Ich möchte morgen ein Treffen vereinbaren, um das vierteljährliche Budget zu besprechen, die offenen Punkte der letzten Woche zu überprüfen und außerdem die Reisevorbereitungen für die Konferenz im nächsten Monat zu bestätigen.",
        "test_f_long_de.wav", "long DE, normal rate");

    // G: self-correction — a natural false-start/revision, normal rate.
    await SynthSsml("en-US-JennyNeural", "en-US",
        "I want to schedule — no wait, I mean reschedule — the meeting for tomorrow afternoon.",
        "test_g_correction_en.wav", "EN with self-correction");
    await SynthSsml("de-DE-KatjaNeural", "de-DE",
        "Ich möchte das Treffen verschieben — nein warte, ich meine bestätigen — für morgen Nachmittag.",
        "test_g_correction_de.wav", "DE with self-correction");

    // H: multiple consecutive utterances in one continuous stream (separated by a
    // pause long enough for Azure's own segmentation to plausibly treat them as
    // separate utterances — SSML <break> gives us a controlled, reproducible gap).
    await SynthSsml("en-US-JennyNeural", "en-US",
        "I would like to schedule a meeting tomorrow.<break time=\"800ms\"/>Please also send the agenda in advance.<break time=\"800ms\"/>Thank you very much.",
        "test_h_consecutive_en.wav", "EN, three consecutive utterances");
    await SynthSsml("de-DE-KatjaNeural", "de-DE",
        "Ich möchte morgen ein Treffen vereinbaren.<break time=\"800ms\"/>Bitte senden Sie auch die Tagesordnung im Voraus.<break time=\"800ms\"/>Vielen Dank.",
        "test_h_consecutive_de.wav", "DE, three consecutive utterances");
}

static async Task RunMeasurementTestAsync()
{
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var logDir = Path.Combine(testResultsRoot, "logs", "measurement");
    Directory.CreateDirectory(logDir);

    var cases = new[]
    {
        ("A_normal_en_to_de", "test1_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("B_normal_de_to_en", "test2_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("C_fast_en_to_de", "test_c_fast_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("D_fast_de_to_en", "test_d_fast_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("E_short_en_to_de", "test_e_short_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("E_short_de_to_en", "test_e_short_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("F_long_en_to_de", "test_f_long_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("F_long_de_to_en", "test_f_long_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("G_correction_en_to_de", "test_g_correction_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("G_correction_de_to_en", "test_g_correction_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("H_consecutive_en_to_de", "test_h_consecutive_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("H_consecutive_de_to_en", "test_h_consecutive_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
    };

    foreach (var (caseName, wavFile, sourceLang, targetLang, voice) in cases)
    {
        var wavPath = Path.Combine(inputDir, wavFile);
        if (!File.Exists(wavPath))
        {
            Console.WriteLine($"SKIP {caseName}: {wavPath} missing — run 'gen-test-audio' and 'gen-measurement-audio' first.");
            continue;
        }

        var logPath = Path.Combine(logDir, $"{caseName}.log");
        if (File.Exists(logPath)) File.Delete(logPath);
        var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

        Console.WriteLine($"\n=== {caseName} ===");
        var provider = new AzureSpeechTranslationProvider(key!, region!, voice, logger);
        var source = new TestFileAudioInputSource(wavPath);
        var completionSignal = new TaskCompletionSource();
        provider.Error += (_, e) => Console.WriteLine($"  [error] {(e.IsFatal ? "FATAL" : "warn")}: {e.Message}");
        source.PcmChunkCaptured += (_, chunk) => provider.PushAudio(chunk);
        source.PlaybackCompleted += (_, _) => completionSignal.TrySetResult();

        await provider.StartAsync(sourceLang, targetLang, CancellationToken.None);
        source.Start();
        await Task.WhenAny(completionSignal.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        await Task.Delay(3000); // let the recognizer finalize the last segment and synthesize

        source.Stop();
        await provider.StopAsync();
        await provider.DisposeAsync();

        if (File.Exists(logPath))
        {
            var lines = await File.ReadAllLinesAsync(logPath);
            var relevant = lines.Where(l =>
                l.Contains("PartialMeasurement") || l.Contains("UtteranceSummary") ||
                l.Contains("SynthesisTiming") || l.Contains("ReconnectDuringUtterance") ||
                l.Contains("UtteranceEvaluated") || l.Contains("Disconnected")).ToList();
            foreach (var line in relevant) Console.WriteLine("  " + line);
            Console.WriteLine($"  (full log: {logPath})");
        }
    }
}

static async Task RunShadowTestAsync()
{
    // Step 3 live shadow-integration measurement. Reuses the exact same real-Azure
    // harness, cases, and WAV files as Step 1's measurement-test (gen-measurement-audio
    // must have already been run) — the ONLY difference is which log lines we print,
    // since the shadow instrumentation is already wired into AzureSpeechTranslationProvider
    // unconditionally (diagnostic-only) and requires no separate code path to exercise.
    // This directly answers "would this stability engine produce useful incremental
    // source segments from real Azure partials?" — it does NOT exercise streaming
    // translation/TTS, which does not exist.
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var logDir = Path.Combine(testResultsRoot, "logs", "shadow");
    Directory.CreateDirectory(logDir);

    var cases = new[]
    {
        ("A_normal_en_to_de", "test1_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("B_normal_de_to_en", "test2_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("C_fast_en_to_de", "test_c_fast_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("D_fast_de_to_en", "test_d_fast_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("E_short_en_to_de", "test_e_short_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("F_long_en_to_de", "test_f_long_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("G_correction_en_to_de", "test_g_correction_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("H_consecutive_en_to_de", "test_h_consecutive_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
    };

    foreach (var (caseName, wavFile, sourceLang, targetLang, voice) in cases)
    {
        var wavPath = Path.Combine(inputDir, wavFile);
        if (!File.Exists(wavPath))
        {
            Console.WriteLine($"SKIP {caseName}: {wavPath} missing — run 'gen-measurement-audio' first.");
            continue;
        }

        var logPath = Path.Combine(logDir, $"{caseName}.log");
        if (File.Exists(logPath)) File.Delete(logPath);
        var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

        Console.WriteLine($"\n=== {caseName} (shadow) ===");
        var provider = new AzureSpeechTranslationProvider(key!, region!, voice, logger);
        var source = new TestFileAudioInputSource(wavPath);
        var completionSignal = new TaskCompletionSource();
        provider.Error += (_, e) => Console.WriteLine($"  [error] {(e.IsFatal ? "FATAL" : "warn")}: {e.Message}");
        source.PcmChunkCaptured += (_, chunk) => provider.PushAudio(chunk);
        source.PlaybackCompleted += (_, _) => completionSignal.TrySetResult();

        // Also observe the PRODUCTION event surface directly, to prove shadow presence
        // doesn't change what these events carry — this is the acceptance-criteria check,
        // not just a log read.
        var finalResultCount = 0;
        var audioSynthesizedCount = 0;
        provider.FinalResult += (_, _) => finalResultCount++;
        provider.AudioSynthesized += (_, _) => audioSynthesizedCount++;

        await provider.StartAsync(sourceLang, targetLang, CancellationToken.None);
        source.Start();
        await Task.WhenAny(completionSignal.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        await Task.Delay(3000);

        source.Stop();
        await provider.StopAsync();
        await provider.DisposeAsync();

        Console.WriteLine($"  production FinalResult events={finalResultCount} AudioSynthesized events={audioSynthesizedCount}");

        if (File.Exists(logPath))
        {
            var lines = await File.ReadAllLinesAsync(logPath);
            var relevant = lines.Where(l =>
                l.Contains("ShadowFirstPartial") || l.Contains("ShadowFirstCommit") ||
                l.Contains("ShadowPartialObserved") || l.Contains("ShadowFinalized") ||
                l.Contains("PartialMeasurement") || l.Contains("UtteranceSummary")).ToList();
            foreach (var line in relevant) Console.WriteLine("  " + line);
            Console.WriteLine($"  (full log: {logPath})");
        }
    }
}

static async Task RunTranslationNaturalizationGeminiTestAsync()
{
    // Step 5.14A — real live run: Azure Translator baseline -> Gemini naturalization ->
    // MeaningPreservationValidator, across the full 38-case corpus, both directions.
    // Fully isolated: no production type is referenced, nothing is wired into
    // AzureSpeechTranslationProvider/DirectionPipeline.
    var geminiKey = VTTranslate.Core.Streaming.GeminiNaturalizationProvider.TryLoadApiKey();
    if (string.IsNullOrEmpty(geminiKey))
    {
        Console.WriteLine("NATURALIZATION PROVIDER UNAVAILABLE — GEMINI_API_KEY is not set.");
        Environment.Exit(1);
        return;
    }

    VTTranslate.Core.Streaming.TranslatorCredentialConfig translatorConfig;
    try
    {
        translatorConfig = VTTranslate.Core.Streaming.TranslatorCredentialLoader.LoadOrThrow();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
        return;
    }

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs", "translation-naturalization-gemini");
    Directory.CreateDirectory(logDir);
    var metadataLogPath = Path.Combine(logDir, "translation-naturalization-gemini.log");
    if (File.Exists(metadataLogPath)) File.Delete(metadataLogPath);
    var metadataLogger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(metadataLogPath);

    // Separate RESULTS artifact (not the metadata-only diagnostic logger) — this file
    // intentionally DOES contain the corpus's own authored text and Gemini's real output,
    // per Step 5.14A §4's explicit requirement to capture source/baseline/candidate for
    // evaluation. This is our own hand-authored experimental corpus, never real user
    // speech, and is written to test-results/ (already .gitignore'd test output), not to
    // any diagnostic logger used by production code paths.
    var resultsPath = Path.Combine(logDir, "gemini-naturalization-results.tsv");
    var resultsLines = new List<string> { "CaseId\tCategory\tDirection\tSource\tBaseline\tGeminiCandidate\tValidationOutcome\tFinalText\tFailureReason\tT0ToT1Ms\tT1ToT2Ms\tT2ToT3Ms\tT0ToT3Ms" };

    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    string modelId;
    try
    {
        modelId = await VTTranslate.Core.Streaming.GeminiNaturalizationProvider.DiscoverModelIdAsync(httpClient, geminiKey, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"NATURALIZATION PROVIDER UNAVAILABLE — Gemini model discovery failed: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    Console.WriteLine("=== Translation naturalization — Gemini live experiment ===");
    Console.WriteLine($"Model discovered (recorded, credential never recorded): {modelId}");
    Console.WriteLine($"Corpus size: {VTTranslate.Core.Streaming.NaturalizationTestCorpus.Cases.Count} cases (categories A-S, both directions)");

    var translationProvider = VTTranslate.Core.Streaming.AzureTranslatorTextProvider.FromCredentialConfig(httpClient, translatorConfig);
    var naturalizationProvider = new VTTranslate.Core.Streaming.GeminiNaturalizationProvider(httpClient, geminiKey, modelId);
    var experiment = new VTTranslate.Core.Streaming.TranslationNaturalizationExperiment(naturalizationProvider, metadataLogger, "gemini-live-experiment");

    var accepted = 0; var rejected = 0; var fallback = 0; var apiFailures = 0; var cancelled = 0;
    var totalLatenciesMs = new List<double>();
    var seq = 0;

    foreach (var testCase in VTTranslate.Core.Streaming.NaturalizationTestCorpus.Cases)
    {
        seq++;
        var direction = $"{testCase.SourceLanguage}->{testCase.TargetLanguage}";

        // T0: baseline Azure translation.
        var baselineResult = await translationProvider.TranslateAsync(
            new VTTranslate.Core.Streaming.TranslationProviderRequest(testCase.SourceLanguage, testCase.TargetLanguage, testCase.SourceText, null),
            CancellationToken.None);

        if (!baselineResult.Success || string.IsNullOrEmpty(baselineResult.CandidateTranslatedText))
        {
            Console.WriteLine($"  [{testCase.Id}] Azure baseline translation FAILED: {baselineResult.FailureReason ?? "n/a"} — skipping this case (Gemini/validator cannot run without a baseline).");
            resultsLines.Add($"{testCase.Id}\t{testCase.Category}\t{direction}\t{Escape(testCase.SourceText)}\tBASELINE_FAILED\t\t\t\t{baselineResult.FailureReason}\t\t\t\t");
            apiFailures++;
            continue;
        }

        VTTranslate.Core.Streaming.NaturalizationOutcomeResult result;
        var attempt = 0;
        const int maxAttempts = 4;
        while (true)
        {
            attempt++;
            var request = new VTTranslate.Core.Streaming.NaturalizationRequest(
                testCase.Id, 1, seq++, baselineResult.CandidateTranslatedText, testCase.SourceLanguage, testCase.TargetLanguage, null, testCase.TerminologyTerms);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                result = await experiment.ProcessAsync(request, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"  [{testCase.Id}] Cancelled/timed out — baseline used.");
                resultsLines.Add($"{testCase.Id}\t{testCase.Category}\t{direction}\t{Escape(testCase.SourceText)}\t{Escape(baselineResult.CandidateTranslatedText)}\t\tCancelled\t{Escape(baselineResult.CandidateTranslatedText)}\ttimeout/cancelled\t\t\t\t");
                cancelled++;
                goto NextCase;
            }

            // HTTP 429 (rate limit) is a transient free-tier quota condition, not a
            // genuine provider/validator finding — retry with backoff rather than
            // recording a rate-limit hit as if it were a real naturalization outcome,
            // which would misrepresent the provider's actual quality/fallback rate.
            if (result.FailureReason == "HTTP 429" && attempt < maxAttempts)
            {
                var backoffSeconds = 15 * attempt;
                Console.WriteLine($"  [{testCase.Id}] Rate-limited (HTTP 429) — retrying in {backoffSeconds}s (attempt {attempt}/{maxAttempts}).");
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds));
                continue;
            }

            break;
        }

        switch (result.ValidationOutcome)
        {
            case VTTranslate.Core.Streaming.ValidationOutcome.SafeToUse: accepted++; break;
            case VTTranslate.Core.Streaming.ValidationOutcome.Rejected: rejected++; break;
            case VTTranslate.Core.Streaming.ValidationOutcome.FallbackToBaseline: fallback++; break;
        }
        if (result.T0ToT3TotalMs.HasValue) totalLatenciesMs.Add(result.T0ToT3TotalMs.Value);

        Console.WriteLine($"  [{testCase.Id}] {testCase.Category} ({direction}): outcome={result.ValidationOutcome} " +
                           $"T0->T1={result.T0ToT1RequestStartMs:F0}ms T1->T2={result.T1ToT2ResponseMs:F0}ms T2->T3={result.T2ToT3ValidationMs:F0}ms " +
                           $"reason={result.FailureReason ?? "n/a"}");

        resultsLines.Add(string.Join('\t',
            testCase.Id, testCase.Category, direction,
            Escape(testCase.SourceText), Escape(baselineResult.CandidateTranslatedText), Escape(result.NaturalizedCandidateText ?? ""),
            result.ValidationOutcome?.ToString() ?? "n/a", Escape(result.FinalText), result.FailureReason ?? "",
            result.T0ToT1RequestStartMs?.ToString("F0") ?? "", result.T1ToT2ResponseMs?.ToString("F0") ?? "",
            result.T2ToT3ValidationMs?.ToString("F0") ?? "", result.T0ToT3TotalMs?.ToString("F0") ?? ""));

        await Task.Delay(TimeSpan.FromSeconds(4.5)); // pacing against the free-tier ~15 requests/minute limit observed during this experiment

        NextCase: ;
    }

    await File.WriteAllLinesAsync(resultsPath, resultsLines);

    Console.WriteLine("\n=== SUMMARY ===");
    Console.WriteLine($"Total cases: {VTTranslate.Core.Streaming.NaturalizationTestCorpus.Cases.Count}");
    Console.WriteLine($"Accepted (SafeToUse): {accepted}");
    Console.WriteLine($"Rejected (validator): {rejected}");
    Console.WriteLine($"Fallback (provider issue/uncertain): {fallback}");
    Console.WriteLine($"Baseline-translation API failures: {apiFailures}");
    Console.WriteLine($"Cancelled/timeout: {cancelled}");
    if (totalLatenciesMs.Count > 0)
    {
        var sorted = totalLatenciesMs.OrderBy(x => x).ToList();
        Console.WriteLine($"T0->T3 latency (ms) — n={sorted.Count}, median={Percentile(sorted, 0.5):F0}, p95={Percentile(sorted, 0.95):F0}, max={sorted[^1]:F0}");
    }
    Console.WriteLine($"\nFull per-case results (source/baseline/candidate text — this corpus is hand-authored, not real user speech): {resultsPath}");
    Console.WriteLine("Metadata-only diagnostic log (no text content): " + metadataLogPath);
}

static string Escape(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

static async Task RunTranslationNaturalizationTestAsync()
{
    // Step 5.14 — a live naturalization run requires a naturalization PROVIDER credential
    // (e.g., an LLM API key). This project has NO existing credential convention for a
    // naturalization/LLM backend (only AZURE_SPEECH_KEY/REGION and AZURE_TRANSLATOR_KEY/
    // REGION/ENDPOINT are established). Per the explicit instruction "Only if the required
    // provider credentials are already legitimately available" and "Do NOT spend time
    // bypassing environmental security controls," this checks ONE plausible, commonly-used
    // variable name and, if absent, stops cleanly — it does NOT invent a vendor
    // integration to fabricate live evidence that was never authorized or configured.
    var naturalizationKey = Environment.GetEnvironmentVariable("NATURALIZATION_PROVIDER_API_KEY");
    if (string.IsNullOrEmpty(naturalizationKey))
    {
        Console.WriteLine("=== Translation naturalization — live run ===");
        Console.WriteLine("BLOCKED: no naturalization provider credential is configured in this environment.");
        Console.WriteLine("Checked environment variable: NATURALIZATION_PROVIDER_API_KEY (not set).");
        Console.WriteLine("This project has no existing credential convention for an LLM/naturalization backend.");
        Console.WriteLine("Per instruction: not bypassing security, not fabricating results. Live naturalization comparison is UNAVAILABLE in this environment.");
        Console.WriteLine($"Test corpus is ready ({VTTranslate.Core.Streaming.NaturalizationTestCorpus.Cases.Count} cases, categories A-S, both directions) for whenever a provider is authorized and configured.");
        return;
    }

    // Reached only if a future authorized run configures NATURALIZATION_PROVIDER_API_KEY.
    // No provider implementation is wired here — deliberately left unimplemented, since
    // building an untestable, never-exercised live HTTP client for an unspecified vendor
    // would be exactly the kind of unvalidated code this project's standing rules warn
    // against. A real live run should implement INaturalizationProvider against the
    // specific, then-authorized vendor and wire it in here.
    Console.WriteLine("A naturalization provider credential IS configured, but no concrete INaturalizationProvider implementation is wired into this command yet.");
    Console.WriteLine("Not proceeding — implementing a live provider integration was out of scope without a specific, authorized vendor/credential to target.");
    await Task.CompletedTask;
}

static async Task RunConversationalStreamingPipelineTestAsync()
{
    // Step 5.13 — end-to-end pipeline through REAL Azure Translator Text (dedicated
    // AZURE_TRANSLATOR_KEY/REGION credentials, never falling back to Speech credentials)
    // and REAL Azure standalone TTS (frozen Step 5.12 IStreamingTtsProvider/
    // AzureStreamingTtsProvider, reused read-only, unmodified). Fully isolated:
    // InMemoryTestPlaybackSink only records metadata, never plays audio through any real
    // output device. This live run provides Step 5.13's OWN evidence only — it does NOT
    // retroactively validate Step 5.12's still-frozen/UNVALIDATED findings.
    VTTranslate.Core.Streaming.TranslatorCredentialConfig translatorConfig;
    try
    {
        translatorConfig = VTTranslate.Core.Streaming.TranslatorCredentialLoader.LoadOrThrow();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
        return;
    }
    var (speechKey, speechRegion) = RequireAzureConfig();

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs", "conversational-streaming-pipeline");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, "conversational-streaming-pipeline.log");
    if (File.Exists(logPath)) File.Delete(logPath);
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    var httpClient = new HttpClient();
    var translationProvider = VTTranslate.Core.Streaming.AzureTranslatorTextProvider.FromCredentialConfig(httpClient, translatorConfig);
    var ttsProvider = new VTTranslate.Core.Streaming.AzureStreamingTtsProvider(speechKey!, speechRegion!);
    var sink = new VTTranslate.Core.Streaming.InMemoryTestPlaybackSink();

    var pipeline = new VTTranslate.Core.Streaming.ConversationalStreamingPipelineExperiment(
        new VTTranslate.Core.Streaming.PrefixStabilityEngine(), translationProvider, ttsProvider, sink, logger,
        "conversational-pipeline-live", contextStrategy: "C1",
        sourceLanguage: "de", targetLanguage: "en", voiceName: "en-US-JennyNeural", maxQueueDepth: 5);

    Console.WriteLine("=== Conversational streaming pipeline — real Azure Translator + real Azure TTS ===");

    // A realistic word-by-word German partial stream for one normal utterance.
    var words = "Guten Tag. Ich hätte gerne einen Termin für nächste Woche.".Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var dropped = new List<VTTranslate.Core.Streaming.PipelineSegmentResult>();
    for (var i = 1; i <= words.Length; i++)
    {
        var partialText = string.Join(" ", words.Take(i));
        var seg = pipeline.ObservePartial(new VTTranslate.Core.Streaming.PipelineSourcePartialEvent("live-u1", 1, i, partialText, DateTimeOffset.UtcNow), dropped);
        if (seg != null) Console.WriteLine($"  Boundary detected at partial #{i}: segment length={seg.SourceText.Length} chars, context length={seg.ContextText.Length} chars");
    }

    var results = await pipeline.DrainAsync(CancellationToken.None);
    foreach (var r in results)
    {
        Console.WriteLine($"  segment#{r.SegmentSequence}: outcome={r.Outcome} " +
                           $"T0->T1={r.T0ToT1TranslationStartMs:F0}ms T1->T2={r.T1ToT2TranslationCompleteMs:F0}ms T2->T3={r.T2ToT3TtsStartMs:F0}ms " +
                           $"T3->T4={r.T3ToT4FirstAudioMs:F0}ms T0->T4(firstAudio)={r.T0ToT4FirstAudioTotalMs:F0}ms T0->T6(delivered)={r.T0ToT6DeliveredTotalMs:F0}ms " +
                           $"bytes={r.TotalBytes} chunks={r.ChunkCount} reason={r.FailureReason ?? "n/a"}");
    }

    Console.WriteLine($"\nDelivered to test sink: {sink.Events.Count}, dropped for backpressure: {dropped.Count}, max observed queue depth: {pipeline.MaxObservedQueueDepth}");
    Console.WriteLine("Every result above is OBSERVED (real Azure calls, synthetic ASR partial stream) — see docs/design-notes/conversational-streaming-pipeline-experiment.md for the full PROVEN/OBSERVED/UNVALIDATED/ASSUMED breakdown.");
}

static async Task RunStreamingTtsFeasibilityTestAsync()
{
    // Step 5.12 — real Azure TTS via the existing, already-proven AZURE_SPEECH_KEY/
    // AZURE_SPEECH_REGION credentials (standalone SpeechSynthesizer — the same mechanism
    // this project's own audio-generation helpers already use, e.g. GenerateTestAudioAsync).
    // Isolated: no production playback, no Google Meet, no physical audio output device —
    // InMemoryTestPlaybackSink only records metadata, never plays sound.
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs", "streaming-tts-feasibility");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, "streaming-tts-feasibility.log");
    if (File.Exists(logPath)) File.Delete(logPath);
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    var ttsProvider = new VTTranslate.Core.Streaming.AzureStreamingTtsProvider(key!, region!);

    // ---- 1. Minimal capability verification ----
    Console.WriteLine("=== TTS capability verification ===");
    var capBytes = 0;
    var capChunks = 0;
    var capResult = await ttsProvider.SynthesizeAsync("Hallo.", "de-DE", "de-DE-KatjaNeural",
        chunk => { capBytes += chunk.Data.Length; capChunks++; }, CancellationToken.None);
    Console.WriteLine($"  German TTS: success={capResult.Success} bytes={capResult.TotalBytes} chunks={capResult.ChunkCount} reason={capResult.FailureReason ?? "n/a"}");
    Console.WriteLine($"  Output format: {VTTranslate.Core.Streaming.AzureStreamingTtsProvider.DefaultOutputFormatDescription}");

    var capResultEn = await ttsProvider.SynthesizeAsync("Hello.", "en-US", "en-US-JennyNeural",
        _ => { }, CancellationToken.None);
    Console.WriteLine($"  English TTS: success={capResultEn.Success} bytes={capResultEn.TotalBytes} chunks={capResultEn.ChunkCount} reason={capResultEn.FailureReason ?? "n/a"}");

    if (!capResult.Success || !capResultEn.Success)
    {
        Console.WriteLine("  Capability verification FAILED for at least one direction — stopping this branch, per instruction not to fabricate a workaround.");
        return;
    }

    // ---- 6. Chunk-size experiment: A-D controlled text sizes, both directions ----
    Console.WriteLine("\n=== Chunk-size experiment ===");
    var chunkCases = new (string Label, string Text, string Lang, string Voice)[]
    {
        ("A_VeryShort_DE", "Ja.", "de-DE", "de-DE-KatjaNeural"),
        ("B_ShortConversational_EN", "Okay, sounds good.", "en-US", "en-US-JennyNeural"),
        ("C_Medium_DE", "Ich möchte morgen einen Termin vereinbaren.", "de-DE", "de-DE-KatjaNeural"),
        ("D_Longer_EN", "Please review the quarterly report and send feedback before Friday's meeting.", "en-US", "en-US-JennyNeural"),
    };

    var sink = new VTTranslate.Core.Streaming.InMemoryTestPlaybackSink();
    var pipeline = new VTTranslate.Core.Streaming.StreamingTtsPipelineExperiment(ttsProvider, sink, logger, "chunk-size-experiment");
    var latencies = new List<double>();
    var firstAudioLatencies = new List<double>();

    for (int i = 0; i < chunkCases.Length; i++)
    {
        var (label, text, lang, voice) = chunkCases[i];
        var t0 = DateTimeOffset.UtcNow;
        var request = new VTTranslate.Core.Streaming.TtsPipelineRequest(label, 1, i + 1, text, true, lang, voice, t0);
        var result = await pipeline.SubmitAsync(request, CancellationToken.None);
        Console.WriteLine($"  {label}: outcome={result.Outcome} T0->T1={result.T0ToT1Ms:F0}ms T1->T2={result.T1ToT2Ms:F0}ms " +
                           $"T2->T3={result.T2ToT3Ms:F0}ms T0->T2={result.T0ToT2Ms:F0}ms T0->T4={result.T0ToT4Ms:F0}ms bytes={result.TotalBytes} chunks={result.ChunkCount}");
        if (result.T0ToT4Ms.HasValue) latencies.Add(result.T0ToT4Ms.Value);
        if (result.T0ToT2Ms.HasValue) firstAudioLatencies.Add(result.T0ToT2Ms.Value);
    }

    // ---- 7. Revision safety: A then B revises A before A should be spoken ----
    Console.WriteLine("\n=== Revision safety: unstable candidate must never reach TTS ===");
    var unstableRequest = new VTTranslate.Core.Streaming.TtsPipelineRequest("revtest", 1, 1, "Ich möchte am Dienstag.", false, "de-DE", "de-DE-KatjaNeural", DateTimeOffset.UtcNow);
    var unstableResult = await pipeline.SubmitAsync(unstableRequest, CancellationToken.None);
    Console.WriteLine($"  Unstable candidate A: outcome={unstableResult.Outcome} (must be RejectedUnstable, never call TTS)");

    var stableRequest = new VTTranslate.Core.Streaming.TtsPipelineRequest("revtest", 1, 1, "Ich möchte am Mittwoch.", true, "de-DE", "de-DE-KatjaNeural", DateTimeOffset.UtcNow);
    var stableResult = await pipeline.SubmitAsync(stableRequest, CancellationToken.None);
    Console.WriteLine($"  Stable revision B: outcome={stableResult.Outcome} (should deliver)");

    // ---- 9. Cancellation: new generation while old synthesis is pending ----
    Console.WriteLine("\n=== Cancellation: generation bump during in-flight synthesis ===");
    var pipeline2 = new VTTranslate.Core.Streaming.StreamingTtsPipelineExperiment(ttsProvider, new VTTranslate.Core.Streaming.InMemoryTestPlaybackSink(), logger, "cancellation-test");
    using var cts = new CancellationTokenSource();
    var longRequest = new VTTranslate.Core.Streaming.TtsPipelineRequest("canceltest", 1, 1,
        "This is a somewhat longer sentence used specifically to give cancellation a real window to occur during synthesis.",
        true, "en-US", "en-US-JennyNeural", DateTimeOffset.UtcNow);
    var synthesisTask = pipeline2.SubmitAsync(longRequest, cts.Token);
    await Task.Delay(30); // let synthesis begin
    cts.Cancel();
    var cancelResult = await synthesisTask;
    Console.WriteLine($"  Cancel-during-synthesis: outcome={cancelResult.Outcome} (should be Cancelled or a rejection, never DeliveredToTestSink)");

    // ---- Summary ----
    Console.WriteLine("\n\n=== SUMMARY ===");
    if (latencies.Count > 0)
    {
        latencies.Sort();
        Console.WriteLine($"  T0->T4 (candidate-Speakable-to-test-sink) latencies: median={latencies[latencies.Count / 2]:F0}ms max={latencies[^1]:F0}ms n={latencies.Count}");
    }
    if (firstAudioLatencies.Count > 0)
    {
        firstAudioLatencies.Sort();
        Console.WriteLine($"  T0->T2 (candidate-Speakable-to-first-audio) latencies: median={firstAudioLatencies[firstAudioLatencies.Count / 2]:F0}ms max={firstAudioLatencies[^1]:F0}ms n={firstAudioLatencies.Count}");
    }
    Console.WriteLine($"  Total sink deliveries: {sink.Events.Count}");
    Console.WriteLine($"(full log: {logPath})");
}

static async Task RunContextWindowOptimizationTestAsync()
{
    // Step 5.11 — same A-N corpus as Steps 5.9/5.10, real independent Translator
    // credentials only, comparing C0-C4 bounded application-side context strategies.
    VTTranslate.Core.Streaming.TranslatorCredentialConfig translatorConfig;
    try
    {
        translatorConfig = VTTranslate.Core.Streaming.TranslatorCredentialLoader.LoadOrThrow();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
        return;
    }

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs", "context-window-optimization");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, "context-window-optimization.log");
    if (File.Exists(logPath)) File.Delete(logPath);
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    var httpClient = new HttpClient();
    var provider = VTTranslate.Core.Streaming.AzureTranslatorTextProvider.FromCredentialConfig(httpClient, translatorConfig);

    // Same A-N corpus as Steps 5.9/5.10, for direct comparability.
    var cases = new (string Name, string SourceLang, string TargetLang, string[] Partials, string FinalSource)[]
    {
        ("A_SimpleStatement", "en-US", "de-DE",
            new[] { "I", "I want", "I want to", "I want to book", "I want to book a table" },
            "I want to book a table."),
        ("B_LongSentence", "en-US", "de-DE",
            new[] { "Please", "Please review the quarterly report", "Please review the quarterly report and send feedback", "Please review the quarterly report and send feedback before Friday" },
            "Please review the quarterly report and send feedback before Friday's meeting."),
        ("C_Question", "en-US", "de-DE",
            new[] { "Can", "Can you", "Can you send", "Can you send the invoice" },
            "Can you send the invoice?"),
        ("D_ModalConstruction", "en-US", "de-DE",
            new[] { "I", "I would", "I would like", "I would like to", "I would like to schedule a meeting" },
            "I would like to schedule a meeting tomorrow."),
        ("E_SelfCorrection", "en-US", "de-DE",
            new[] { "Let's meet on Tuesday", "Let's meet on Wednesday" },
            "Let's meet on Wednesday, actually."),
        ("F_GermanSimpleStatement", "de-DE", "en-US",
            new[] { "Ich", "Ich möchte", "Ich möchte morgen", "Ich möchte morgen einen", "Ich möchte morgen einen Termin" },
            "Ich möchte morgen einen Termin vereinbaren."),
        ("G_GermanSubordinateClause", "de-DE", "en-US",
            new[] { "Ich denke", "Ich denke, dass", "Ich denke, dass wir das Treffen verschieben sollten" },
            "Ich denke, dass wir das Treffen verschieben sollten."),
        ("H_GermanSeparableVerb", "de-DE", "en-US",
            new[] { "Ich rufe", "Ich rufe dich", "Ich rufe dich morgen früh", "Ich rufe dich morgen früh an" },
            "Ich rufe dich morgen früh an."),
        ("I_GermanModalConstruction", "de-DE", "en-US",
            new[] { "Ich", "Ich kann", "Ich kann das Treffen", "Ich kann das Treffen verschieben" },
            "Ich kann das Treffen leider verschieben."),
        ("J_GermanSelfCorrection", "de-DE", "en-US",
            new[] { "Lass uns am Dienstag treffen", "Lass uns am Mittwoch treffen" },
            "Lass uns am Mittwoch treffen, tatsächlich."),
        ("K_FastSpeech", "en-US", "de-DE",
            new[] { "I would like to schedule a meeting tomorrow because", "I would like to schedule a meeting tomorrow because the client asked" },
            "I would like to schedule a meeting tomorrow because the client asked for it."),
        ("L_Colloquial", "en-US", "de-DE",
            new[] { "So", "So um I was thinking", "So um I was thinking maybe we could grab lunch" },
            "So, um, I was thinking maybe we could grab lunch."),
        ("M_BusinessTechnical", "en-US", "de-DE",
            new[] { "Please update", "Please update the invoice", "Please update the invoice with the new purchase order number" },
            "Please update the invoice with the new purchase order number."),
        ("N1_ConsecutiveUtterance", "en-US", "de-DE",
            new[] { "Okay." },
            "Okay."),
        ("N2_ConsecutiveUtterance", "en-US", "de-DE",
            new[] { "See", "See you then" },
            "See you then."),
        ("O_Negation", "en-US", "de-DE",
            new[] { "I", "I do not", "I do not want", "I do not want to reschedule" },
            "I do not want to reschedule the meeting."),
        ("P_ShortUtterance", "de-DE", "en-US",
            Array.Empty<string>(),
            "Nein."),
    };

    var strategyTotals = VTTranslate.Core.Streaming.ContextWindowTranslationExperiment.StrategyNames.ToDictionary(n => n, _ => (requests: 0, chars: 0));

    foreach (var c in cases)
    {
        Console.WriteLine($"\n=== {c.Name} ({c.SourceLang}->{c.TargetLang}) ===");
        var experiment = new VTTranslate.Core.Streaming.ContextWindowTranslationExperiment(
            new VTTranslate.Core.Streaming.PrefixStabilityEngine(), provider, logger,
            $"{c.SourceLang}->{c.TargetLang}", generation: 1, c.SourceLang, c.TargetLang);

        for (int i = 0; i < c.Partials.Length; i++)
        {
            var obs = new VTTranslate.Core.Streaming.ContextWindowSourceObservation(c.Name, i + 1, c.Partials[i], DateTimeOffset.UtcNow, false);
            var results = await experiment.ObservePartialAsync(obs, CancellationToken.None);
            foreach (var r in results)
                Console.WriteLine($"  partial#{i + 1} {r.Strategy}: revisionKind={r.RevisionKind} translationState={r.TranslationState} speechState={r.SpeechState} " +
                                   $"segChars={r.SegmentCharacters} ctxChars={r.ContextCharacters} totalChars={r.TotalRequestCharacters} " +
                                   $"sourceToRequestMs={r.SourceToRequestMs:F0} latencyMs={r.TranslationLatencyMs:F0}");
        }

        var finalObs = new VTTranslate.Core.Streaming.ContextWindowSourceObservation(c.Name, c.Partials.Length + 1, c.FinalSource, DateTimeOffset.UtcNow, true);
        var summaries = await experiment.ObserveFinalAsync(finalObs, CancellationToken.None);
        foreach (var s in summaries)
        {
            var (reqs, chars) = strategyTotals[s.Strategy];
            strategyTotals[s.Strategy] = (reqs + s.RequestCount, chars + s.CumulativeCharactersSent);
            Console.WriteLine($"  [FINAL] {s.Strategy}: requests={s.RequestCount} contradictions={s.ContradictionCount} revisions={s.RevisionCount} " +
                               $"cumChars={s.CumulativeCharactersSent} maxReqChars={s.MaxRequestCharacters} amplificationVsC0={s.AmplificationVsC0:F2} " +
                               $"missing={(s.ApproxMissingTokenCount?.ToString() ?? "null")} duplicate={(s.ApproxDuplicateTokenCount?.ToString() ?? "null")} " +
                               $"earliestLatencyMs={(s.EarliestLatencyMs?.ToString("F0") ?? "null")} " +
                               $"earliestCandidateForSpeechDelayMs={(s.EarliestCandidateForSpeechDelayMs?.ToString("F0") ?? "null")} " +
                               $"finalLatencyMs={(s.FinalTranslationLatencyMs?.ToString("F0") ?? "null")} finalSucceeded={s.FinalTranslationSucceeded} " +
                               $"anyReachedSpeakable={s.AnyContentReachedSpeakable}");
        }
    }

    Console.WriteLine("\n\nSTRATEGY TOTALS ACROSS ALL CASES:");
    foreach (var name in VTTranslate.Core.Streaming.ContextWindowTranslationExperiment.StrategyNames)
    {
        var (reqs, chars) = strategyTotals[name];
        Console.WriteLine($"  {name}: totalRequests={reqs} totalChars={chars}");
    }
    Console.WriteLine($"(full log: {logPath})");
}

static async Task RunContextAwareTranslationTestAsync()
{
    // Step 5.10 — same A-N corpus as Step 5.9, real independent Translator credentials
    // only, comparing Approach A (application-side accumulated context) vs. Approach B
    // (minimal segment-only translation, no context).
    VTTranslate.Core.Streaming.TranslatorCredentialConfig translatorConfig;
    try
    {
        translatorConfig = VTTranslate.Core.Streaming.TranslatorCredentialLoader.LoadOrThrow();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
        return;
    }

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs", "context-aware-translation");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, "context-aware-translation.log");
    if (File.Exists(logPath)) File.Delete(logPath);
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    var httpClient = new HttpClient();
    var provider = VTTranslate.Core.Streaming.AzureTranslatorTextProvider.FromCredentialConfig(httpClient, translatorConfig);

    // Same A-N corpus as Step 5.9's provisional-translation-architecture-test, for direct comparability.
    var cases = new (string Name, string SourceLang, string TargetLang, string[] Partials, string FinalSource)[]
    {
        ("A_SimpleStatement", "en-US", "de-DE",
            new[] { "I", "I want", "I want to", "I want to book", "I want to book a table" },
            "I want to book a table."),
        ("B_LongSentence", "en-US", "de-DE",
            new[] { "Please", "Please review the quarterly report", "Please review the quarterly report and send feedback", "Please review the quarterly report and send feedback before Friday" },
            "Please review the quarterly report and send feedback before Friday's meeting."),
        ("C_Question", "en-US", "de-DE",
            new[] { "Can", "Can you", "Can you send", "Can you send the invoice" },
            "Can you send the invoice?"),
        ("D_ModalConstruction", "en-US", "de-DE",
            new[] { "I", "I would", "I would like", "I would like to", "I would like to schedule a meeting" },
            "I would like to schedule a meeting tomorrow."),
        ("E_SelfCorrection", "en-US", "de-DE",
            new[] { "Let's meet on Tuesday", "Let's meet on Wednesday" },
            "Let's meet on Wednesday, actually."),
        ("F_GermanSimpleStatement", "de-DE", "en-US",
            new[] { "Ich", "Ich möchte", "Ich möchte morgen", "Ich möchte morgen einen", "Ich möchte morgen einen Termin" },
            "Ich möchte morgen einen Termin vereinbaren."),
        ("G_GermanSubordinateClause", "de-DE", "en-US",
            new[] { "Ich denke", "Ich denke, dass", "Ich denke, dass wir das Treffen verschieben sollten" },
            "Ich denke, dass wir das Treffen verschieben sollten."),
        ("H_GermanSeparableVerb", "de-DE", "en-US",
            new[] { "Ich rufe", "Ich rufe dich", "Ich rufe dich morgen früh", "Ich rufe dich morgen früh an" },
            "Ich rufe dich morgen früh an."),
        ("I_GermanModalConstruction", "de-DE", "en-US",
            new[] { "Ich", "Ich kann", "Ich kann das Treffen", "Ich kann das Treffen verschieben" },
            "Ich kann das Treffen leider verschieben."),
        ("J_GermanSelfCorrection", "de-DE", "en-US",
            new[] { "Lass uns am Dienstag treffen", "Lass uns am Mittwoch treffen" },
            "Lass uns am Mittwoch treffen, tatsächlich."),
        ("K_FastSpeech", "en-US", "de-DE",
            new[] { "I would like to schedule a meeting tomorrow because", "I would like to schedule a meeting tomorrow because the client asked" },
            "I would like to schedule a meeting tomorrow because the client asked for it."),
        ("L_Colloquial", "en-US", "de-DE",
            new[] { "So", "So um I was thinking", "So um I was thinking maybe we could grab lunch" },
            "So, um, I was thinking maybe we could grab lunch."),
        ("M_BusinessTechnical", "en-US", "de-DE",
            new[] { "Please update", "Please update the invoice", "Please update the invoice with the new purchase order number" },
            "Please update the invoice with the new purchase order number."),
        ("N_ConsecutiveUtterance1", "en-US", "de-DE",
            new[] { "Okay." },
            "Okay."),
        ("N_ConsecutiveUtterance2", "en-US", "de-DE",
            new[] { "See", "See you then" },
            "See you then."),
    };

    var totalRequests = 0;
    var totalContextChars = 0;
    var totalSegmentChars = 0;

    foreach (var c in cases)
    {
        Console.WriteLine($"\n=== {c.Name} ({c.SourceLang}->{c.TargetLang}) ===");
        var experiment = new VTTranslate.Core.Streaming.ContextAwareTranslationExperiment(
            new VTTranslate.Core.Streaming.PrefixStabilityEngine(), provider, logger,
            $"{c.SourceLang}->{c.TargetLang}", generation: 1, c.SourceLang, c.TargetLang);

        for (int i = 0; i < c.Partials.Length; i++)
        {
            var obs = new VTTranslate.Core.Streaming.ContextAwareSourceObservation(c.Name, i + 1, c.Partials[i], DateTimeOffset.UtcNow, false);
            var results = await experiment.ObservePartialAsync(obs, CancellationToken.None);
            foreach (var r in results)
                Console.WriteLine($"  partial#{i + 1} {r.Approach}: revisionKind={r.RevisionKind} speechState={r.SpeechState} " +
                                   $"contextChars={r.ContextCharactersSent} segmentChars={r.SegmentCharactersSent} latencyMs={r.LatencyMs:F0} cumulativeTokens={r.CumulativeCandidateTokenCount}");
        }

        var finalObs = new VTTranslate.Core.Streaming.ContextAwareSourceObservation(c.Name, c.Partials.Length + 1, c.FinalSource, DateTimeOffset.UtcNow, true);
        var summaries = await experiment.ObserveFinalAsync(finalObs, CancellationToken.None);
        foreach (var s in summaries)
        {
            totalRequests += s.RequestCount;
            totalContextChars += s.TotalContextCharactersSent;
            totalSegmentChars += s.TotalSegmentCharactersSent;
            Console.WriteLine($"  [FINAL] {s.Approach}: requests={s.RequestCount} contradictions={s.ContradictionCount} revisions={s.RevisionCount} " +
                               $"contextChars={s.TotalContextCharactersSent} segmentChars={s.TotalSegmentCharactersSent} " +
                               $"missing={(s.ApproxMissingTokenCount?.ToString() ?? "null")} duplicate={(s.ApproxDuplicateTokenCount?.ToString() ?? "null")} " +
                               $"earliestLatencyMs={(s.EarliestLatencyMs?.ToString("F0") ?? "null")} " +
                               $"earliestCandidateForSpeechDelayMs={(s.EarliestCandidateForSpeechDelayMs?.ToString("F0") ?? "null")} " +
                               $"finalLatencyMs={(s.FinalTranslationLatencyMs?.ToString("F0") ?? "null")} finalSucceeded={s.FinalTranslationSucceeded} " +
                               $"anyReachedSpeakable={s.AnyContentReachedSpeakable}");
        }
    }

    Console.WriteLine($"\n\nTOTAL translation requests: {totalRequests}");
    Console.WriteLine($"TOTAL context characters sent: {totalContextChars}");
    Console.WriteLine($"TOTAL segment characters sent: {totalSegmentChars}");
    Console.WriteLine($"(full log: {logPath})");
}

static async Task RunProvisionalTranslationArchitectureTestAsync()
{
    // Step 5.9 — real, independent Azure Translator API, dedicated Translator credentials
    // only, comparing Architectures A/B/C's request volume, latency, and reconciliation
    // behavior against realistic incremental ASR-style source sequences.
    VTTranslate.Core.Streaming.TranslatorCredentialConfig translatorConfig;
    try
    {
        translatorConfig = VTTranslate.Core.Streaming.TranslatorCredentialLoader.LoadOrThrow();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
        return;
    }

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs", "provisional-translation-architecture");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, "provisional-translation-architecture.log");
    if (File.Exists(logPath)) File.Delete(logPath);
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    var httpClient = new HttpClient();
    var provider = VTTranslate.Core.Streaming.AzureTranslatorTextProvider.FromCredentialConfig(httpClient, translatorConfig);

    var cases = new (string Name, string SourceLang, string TargetLang, string[] Partials, string FinalSource)[]
    {
        ("A_SimpleStatement", "en-US", "de-DE",
            new[] { "I", "I want", "I want to", "I want to book", "I want to book a table" },
            "I want to book a table."),
        ("B_LongSentence", "en-US", "de-DE",
            new[] { "Please", "Please review the quarterly report", "Please review the quarterly report and send feedback", "Please review the quarterly report and send feedback before Friday" },
            "Please review the quarterly report and send feedback before Friday's meeting."),
        ("C_Question", "en-US", "de-DE",
            new[] { "Can", "Can you", "Can you send", "Can you send the invoice" },
            "Can you send the invoice?"),
        ("D_ModalConstruction", "en-US", "de-DE",
            new[] { "I", "I would", "I would like", "I would like to", "I would like to schedule a meeting" },
            "I would like to schedule a meeting tomorrow."),
        ("E_SelfCorrection", "en-US", "de-DE",
            new[] { "Let's meet on Tuesday", "Let's meet on Wednesday" },
            "Let's meet on Wednesday, actually."),
        ("F_GermanSimpleStatement", "de-DE", "en-US",
            new[] { "Ich", "Ich möchte", "Ich möchte morgen", "Ich möchte morgen einen", "Ich möchte morgen einen Termin" },
            "Ich möchte morgen einen Termin vereinbaren."),
        ("G_GermanSubordinateClause", "de-DE", "en-US",
            new[] { "Ich denke", "Ich denke, dass", "Ich denke, dass wir das Treffen verschieben sollten" },
            "Ich denke, dass wir das Treffen verschieben sollten."),
        ("H_GermanSeparableVerb", "de-DE", "en-US",
            new[] { "Ich rufe", "Ich rufe dich", "Ich rufe dich morgen früh", "Ich rufe dich morgen früh an" },
            "Ich rufe dich morgen früh an."),
        ("I_GermanModalConstruction", "de-DE", "en-US",
            new[] { "Ich", "Ich kann", "Ich kann das Treffen", "Ich kann das Treffen verschieben" },
            "Ich kann das Treffen leider verschieben."),
        ("J_GermanSelfCorrection", "de-DE", "en-US",
            new[] { "Lass uns am Dienstag treffen", "Lass uns am Mittwoch treffen" },
            "Lass uns am Mittwoch treffen, tatsächlich."),
        ("K_FastSpeech", "en-US", "de-DE",
            new[] { "I would like to schedule a meeting tomorrow because", "I would like to schedule a meeting tomorrow because the client asked" },
            "I would like to schedule a meeting tomorrow because the client asked for it."),
        ("L_Colloquial", "en-US", "de-DE",
            new[] { "So", "So um I was thinking", "So um I was thinking maybe we could grab lunch" },
            "So, um, I was thinking maybe we could grab lunch."),
        ("M_BusinessTechnical", "en-US", "de-DE",
            new[] { "Please update", "Please update the invoice", "Please update the invoice with the new purchase order number" },
            "Please update the invoice with the new purchase order number."),
        ("N_ConsecutiveUtterance1", "en-US", "de-DE",
            new[] { "Okay." },
            "Okay."),
        ("N_ConsecutiveUtterance2", "en-US", "de-DE",
            new[] { "See", "See you then" },
            "See you then."),
    };

    var totalRequests = 0;
    var totalChars = 0;

    foreach (var c in cases)
    {
        Console.WriteLine($"\n=== {c.Name} ({c.SourceLang}->{c.TargetLang}) ===");
        var experiment = new VTTranslate.Core.Streaming.ProvisionalTranslationArchitectureExperiment(
            new VTTranslate.Core.Streaming.PrefixStabilityEngine(), provider, logger,
            $"{c.SourceLang}->{c.TargetLang}", generation: 1, c.SourceLang, c.TargetLang);

        for (int i = 0; i < c.Partials.Length; i++)
        {
            var obs = new VTTranslate.Core.Streaming.ProvisionalSourceObservation(c.Name, i + 1, c.Partials[i], DateTimeOffset.UtcNow, false);
            var results = await experiment.ObservePartialAsync(obs, CancellationToken.None);
            foreach (var r in results)
                Console.WriteLine($"  partial#{i + 1} {r.Architecture}: translationState={r.TranslationState} speechState={r.SpeechState} " +
                                   $"changed={r.TranslationChangedFromPrevious} supersedes={r.SupersedesPrevious} prevValid={r.PreviousContentStillValid} " +
                                   $"latencyMs={r.LatencyMs:F0} cumulativeTokens={r.CumulativeCandidateTokenCount}");
        }

        var finalObs = new VTTranslate.Core.Streaming.ProvisionalSourceObservation(c.Name, c.Partials.Length + 1, c.FinalSource, DateTimeOffset.UtcNow, true);
        var summaries = await experiment.ObserveFinalAsync(finalObs, CancellationToken.None);
        foreach (var s in summaries)
        {
            totalRequests += s.TranslationRequestCount;
            totalChars += s.CharactersSentTotal;
            Console.WriteLine($"  [FINAL] {s.Architecture}: requests={s.TranslationRequestCount} revisions={s.RevisionCount} contradictions={s.ContradictionCount} " +
                               $"charsSent={s.CharactersSentTotal} missing={(s.ApproxMissingTokenCount?.ToString() ?? "null")} duplicate={(s.ApproxDuplicateTokenCount?.ToString() ?? "null")} " +
                               $"earliestLatencyMs={(s.EarliestTranslationLatencyMs?.ToString("F0") ?? "null")} " +
                               $"earliestStableDraftDelayMs={(s.EarliestStableDraftDelayMs?.ToString("F0") ?? "null")} " +
                               $"earliestCandidateForSpeechDelayMs={(s.EarliestCandidateForSpeechDelayMs?.ToString("F0") ?? "null")} " +
                               $"finalLatencyMs={(s.FinalTranslationLatencyMs?.ToString("F0") ?? "null")} finalSucceeded={s.FinalTranslationSucceeded} " +
                               $"anyReachedSpeakable={s.AnyContentReachedSpeakable}");
        }
    }

    Console.WriteLine($"\n\nTOTAL translation requests across all cases/architectures: {totalRequests}");
    Console.WriteLine($"TOTAL characters sent: {totalChars}");
    Console.WriteLine($"(full log: {logPath})");
}

static async Task RunSemanticSegmentTranslationTestAsync()
{
    // Step 5.8 — realistic incremental ASR-style sequences, real Policy A source
    // stability, the deterministic SemanticCompletionHeuristic, and the genuine
    // independent Azure Translator API (dedicated Translator credentials only).
    VTTranslate.Core.Streaming.TranslatorCredentialConfig translatorConfig;
    try
    {
        translatorConfig = VTTranslate.Core.Streaming.TranslatorCredentialLoader.LoadOrThrow();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
        return;
    }

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs", "semantic-segment-translation");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, "semantic-segment-translation.log");
    if (File.Exists(logPath)) File.Delete(logPath);
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    var httpClient = new HttpClient();
    var provider = VTTranslate.Core.Streaming.AzureTranslatorTextProvider.FromCredentialConfig(httpClient, translatorConfig);

    var cases = new (string Name, string SourceLang, string TargetLang, string[] Partials, string FinalSource)[]
    {
        ("A_SimpleStatement", "en-US", "de-DE",
            new[] { "I", "I want", "I want to", "I want to book", "I want to book a table" },
            "I want to book a table."),
        ("B_Question", "en-US", "de-DE",
            new[] { "Can", "Can you", "Can you send", "Can you send the invoice" },
            "Can you send the invoice?"),
        ("C_ModalVerb", "en-US", "de-DE",
            new[] { "I", "I would", "I would like", "I would like to", "I would like to schedule a meeting" },
            "I would like to schedule a meeting tomorrow."),
        ("D_GermanSubordinateClause", "de-DE", "en-US",
            new[] { "Ich denke", "Ich denke, dass", "Ich denke, dass wir", "Ich denke, dass wir das Treffen verschieben sollten" },
            "Ich denke, dass wir das Treffen verschieben sollten."),
        ("E_GermanSeparableVerb", "de-DE", "en-US",
            new[] { "Ich rufe", "Ich rufe dich", "Ich rufe dich morgen", "Ich rufe dich morgen früh", "Ich rufe dich morgen früh an" },
            "Ich rufe dich morgen früh an."),
        ("F_LongSentence", "en-US", "de-DE",
            new[] { "Please", "Please review", "Please review the quarterly report", "Please review the quarterly report and send feedback", "Please review the quarterly report and send feedback before Friday's meeting" },
            "Please review the quarterly report and send feedback before Friday's meeting."),
        ("G_ConjunctionContinuation", "en-US", "de-DE",
            new[] { "The meeting went well", "The meeting went well and", "The meeting went well and we", "The meeting went well and we agreed on next steps" },
            "The meeting went well and we agreed on next steps."),
        ("H_SelfCorrection", "en-US", "de-DE",
            new[] { "Let's meet on Tuesday", "Let's meet on Wednesday" },
            "Let's meet on Wednesday, actually."),
        ("I_FastSpeech", "en-US", "de-DE",
            new[] { "I would like to schedule a meeting tomorrow because", "I would like to schedule a meeting tomorrow because the client asked" },
            "I would like to schedule a meeting tomorrow because the client asked for it."),
        ("J_ShortJa", "de-DE", "en-US",
            Array.Empty<string>(),
            "Ja."),
        ("K_IncompleteSentence", "en-US", "de-DE",
            new[] { "I was", "I was going", "I was going to say something but" },
            "I was going to say something but"),
        ("L_ConsecutiveUtterance1", "en-US", "de-DE",
            new[] { "Okay." },
            "Okay."),
        ("L_ConsecutiveUtterance2", "en-US", "de-DE",
            new[] { "See", "See you then" },
            "See you then."),
        ("M_ColloquialPhrase", "en-US", "de-DE",
            new[] { "So", "So um I was thinking", "So um I was thinking maybe we could grab lunch" },
            "So, um, I was thinking maybe we could grab lunch."),
        ("N_BusinessTechnical", "en-US", "de-DE",
            new[] { "Please update", "Please update the invoice", "Please update the invoice with the new purchase order number" },
            "Please update the invoice with the new purchase order number."),
    };

    foreach (var c in cases)
    {
        Console.WriteLine($"\n=== {c.Name} ({c.SourceLang}->{c.TargetLang}) ===");
        var experiment = new VTTranslate.Core.Streaming.SemanticSegmentTranslationExperiment(
            new VTTranslate.Core.Streaming.PrefixStabilityEngine(), provider, logger,
            $"{c.SourceLang}->{c.TargetLang}", generation: 1, c.SourceLang, c.TargetLang);

        for (int i = 0; i < c.Partials.Length; i++)
        {
            var obs = new VTTranslate.Core.Streaming.SemanticSourceObservation(c.Name, i + 1, c.Partials[i], DateTimeOffset.UtcNow, false);
            var results = await experiment.ObservePartialAsync(obs, CancellationToken.None);
            foreach (var r in results)
                Console.WriteLine($"  partial#{i + 1} {r.Policy}: sourceState={r.SourceState} translationState={r.TranslationState} " +
                                   $"hasNewCandidate={r.HasNewCandidate} reason=\"{r.HeuristicReason}\" latencyMs={r.TranslationLatencyMs:F0} cumulativeTokens={r.CumulativeCandidateTokenCount}");
        }

        var finalObs = new VTTranslate.Core.Streaming.SemanticSourceObservation(c.Name, c.Partials.Length + 1, c.FinalSource, DateTimeOffset.UtcNow, true);
        var summaries = await experiment.ObserveFinalAsync(finalObs, CancellationToken.None);
        foreach (var s in summaries)
            Console.WriteLine($"  [FINAL] {s.Policy}: requests={s.TranslationRequestCount} revisions={s.RevisionCount} contradictions={s.ContradictionCount} " +
                               $"missing={(s.ApproxMissingTokenCount?.ToString() ?? "null")} duplicate={(s.ApproxDuplicateTokenCount?.ToString() ?? "null")} " +
                               $"earliestSemanticCommitDelayMs={(s.EarliestSemanticCommitDelayMs?.ToString("F0") ?? "null")} " +
                               $"finalTranslationLatencyMs={(s.FinalTranslationLatencyMs?.ToString("F0") ?? "null")} finalSucceeded={s.FinalTranslationSucceeded}");
    }

    Console.WriteLine($"\n\n(full log: {logPath})");
}

static async Task RunStreamingTranslationDecisionTestAsync()
{
    // Step 5.7 — realistic incremental ASR-style partial sequences (NOT the 2-segment
    // synthetic splits of Steps 5.5/5.6b), driven through the real, unmodified Policy A
    // (PrefixStabilityEngine) and the genuine, independent Azure Translator API via
    // dedicated Translator credentials (never AZURE_SPEECH_KEY/AZURE_SPEECH_REGION, never
    // a mock/fake for these live results — mocks are for unit tests only).
    VTTranslate.Core.Streaming.TranslatorCredentialConfig translatorConfig;
    try
    {
        translatorConfig = VTTranslate.Core.Streaming.TranslatorCredentialLoader.LoadOrThrow();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
        return;
    }

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs", "streaming-translation-decision");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, "streaming-translation-decision.log");
    if (File.Exists(logPath)) File.Delete(logPath);
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    var httpClient = new HttpClient();
    var provider = VTTranslate.Core.Streaming.AzureTranslatorTextProvider.FromCredentialConfig(httpClient, translatorConfig);

    // Each case: a realistic sequence of GROWING partial texts (simulating ASR partial
    // progression), the source/target language, and the true final source text.
    var cases = new (string Name, string SourceLang, string TargetLang, string[] Partials, string FinalSource)[]
    {
        ("A_NormalSpeech", "en-US", "de-DE",
            new[] { "I", "I would", "I would like", "I would like to", "I would like to schedule", "I would like to schedule a", "I would like to schedule a meeting", "I would like to schedule a meeting tomorrow" },
            "I would like to schedule a meeting tomorrow."),
        ("B_FastSpeech", "en-US", "de-DE",
            new[] { "I would like to schedule", "I would like to schedule a meeting tomorrow", "I would like to schedule a meeting tomorrow because" },
            "I would like to schedule a meeting tomorrow because the client asked for it."),
        ("C_SelfCorrection", "en-US", "de-DE",
            new[] { "I would like to meet", "I would like to meet on", "I would like to meet on Tuesday", "I would like to meet on Wednesday" },
            "I would like to meet on Wednesday, actually."),
        ("D_LongSentence", "en-US", "de-DE",
            new[] { "Please", "Please review", "Please review the quarterly", "Please review the quarterly report", "Please review the quarterly report and", "Please review the quarterly report and send", "Please review the quarterly report and send feedback", "Please review the quarterly report and send feedback before", "Please review the quarterly report and send feedback before Friday" },
            "Please review the quarterly report and send feedback before Friday's meeting."),
        ("E_ShortUtterance", "de-DE", "en-US",
            Array.Empty<string>(), // straight to Final — no partials, matching Step 1's short-utterance finding
            "Ja."),
        ("F_German", "de-DE", "en-US",
            new[] { "Ich", "Ich möchte", "Ich möchte morgen", "Ich möchte morgen ein", "Ich möchte morgen ein Treffen", "Ich möchte morgen ein Treffen vereinbaren" },
            "Ich möchte morgen ein Treffen vereinbaren."),
        ("G_English", "en-US", "de-DE",
            new[] { "Can", "Can you", "Can you send", "Can you send the", "Can you send the invoice", "Can you send the invoice today" },
            "Can you send the invoice today?"),
        ("H_Colloquial", "en-US", "de-DE",
            new[] { "So", "So um", "So um I was", "So um I was thinking", "So um I was thinking maybe", "So um I was thinking maybe we could grab lunch" },
            "So, um, I was thinking maybe we could grab lunch."),
        ("I_Incomplete", "en-US", "de-DE",
            new[] { "I was", "I was going", "I was going to say", "I was going to say something but" },
            "I was going to say something but"),
        ("J_RepeatedCorrected", "en-US", "de-DE",
            new[] { "Send it to", "Send it to John", "Send it to John, I mean", "Send it to John, I mean Jonathan" },
            "Send it to John, I mean Jonathan, by Friday."),
        ("K_ConsecutiveUtterance1", "en-US", "de-DE",
            new[] { "Okay", "Okay." },
            "Okay."),
        ("K_ConsecutiveUtterance2", "en-US", "de-DE",
            new[] { "See", "See you", "See you then" },
            "See you then."),
        ("L_TurnBoundarySpeakerA", "en-US", "de-DE",
            new[] { "What time", "What time works", "What time works for you" },
            "What time works for you?"),
        ("L_TurnBoundarySpeakerB", "de-DE", "en-US",
            new[] { "Wie", "Wie wäre es", "Wie wäre es mit", "Wie wäre es mit drei Uhr" },
            "Wie wäre es mit drei Uhr?"),
    };

    foreach (var c in cases)
    {
        Console.WriteLine($"\n=== {c.Name} ({c.SourceLang}->{c.TargetLang}) ===");
        var experiment = new VTTranslate.Core.Streaming.StreamingTranslationDecisionExperiment(
            new VTTranslate.Core.Streaming.PrefixStabilityEngine(), provider, logger,
            $"{c.SourceLang}->{c.TargetLang}", generation: 1, c.SourceLang, c.TargetLang);

        var t0 = DateTimeOffset.UtcNow;
        for (int i = 0; i < c.Partials.Length; i++)
        {
            var obs = new VTTranslate.Core.Streaming.StreamingSourceObservation(c.Name, i + 1, c.Partials[i], DateTimeOffset.UtcNow, false);
            var results = await experiment.ObservePartialAsync(obs, CancellationToken.None);
            foreach (var r in results)
                Console.WriteLine($"  partial#{i + 1} {r.Strategy}: sourceState={r.SourceState} translationState={r.TranslationState} " +
                                   $"hasNewCandidate={r.HasNewCandidate} sourceCommitToRequestMs={r.SourceCommitToTranslationRequestMs:F0} " +
                                   $"requestLatencyMs={r.TranslationRequestLatencyMs:F0} cumulativeTokens={r.CumulativeCandidateTokenCount}");
        }

        var finalObs = new VTTranslate.Core.Streaming.StreamingSourceObservation(c.Name, c.Partials.Length + 1, c.FinalSource, DateTimeOffset.UtcNow, true);
        var summaries = await experiment.ObserveFinalAsync(finalObs, CancellationToken.None);
        foreach (var s in summaries)
            Console.WriteLine($"  [FINAL] {s.Strategy}: requests={s.TranslationRequestCount} revisions={s.RevisionCount} " +
                               $"missing={(s.ApproxMissingTokenCount?.ToString() ?? "null")} duplicate={(s.ApproxDuplicateTokenCount?.ToString() ?? "null")} " +
                               $"earliestCandidateLatencyMs={(s.EarliestCandidateLatencyMs?.ToString("F0") ?? "null")} " +
                               $"finalTranslationLatencyMs={(s.FinalTranslationLatencyMs?.ToString("F0") ?? "null")} finalSucceeded={s.FinalTranslationSucceeded}");
    }

    Console.WriteLine($"\n\n(full log: {logPath})");
}

static async Task VerifyTranslatorCredentialsAsync()
{
    // Step 5.6a — updated to use DEDICATED Translator credentials
    // (AZURE_TRANSLATOR_KEY/AZURE_TRANSLATOR_REGION/AZURE_TRANSLATOR_ENDPOINT), never the
    // Speech-only AZURE_SPEECH_KEY/AZURE_SPEECH_REGION, and never falls back between them
    // — see TranslatorCredentialLoader. If dedicated Translator credentials are missing,
    // this fails clearly (LoadOrThrow) rather than silently reusing Speech credentials,
    // which is exactly what produced Step 5.6's 401s. Never logs/prints the key, secrets,
    // authorization headers, or full environment variable contents — only safe metadata.
    VTTranslate.Core.Streaming.TranslatorCredentialConfig config;
    try
    {
        config = VTTranslate.Core.Streaming.TranslatorCredentialLoader.LoadOrThrow();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
        return;
    }

    Console.WriteLine($"Translator credential status: {VTTranslate.Core.Streaming.TranslatorCredentialLoader.DescribeSafely(config)}");
    Console.WriteLine("(Per privacy rule: key VALUE is never printed — only its length, to confirm it was actually read.)");

    var httpClient = new HttpClient();
    var provider = VTTranslate.Core.Streaming.AzureTranslatorTextProvider.FromCredentialConfig(httpClient, config);

    async Task<bool> RunDirectionAsync(string label, string sourceLang, string targetLang, string text)
    {
        Console.WriteLine($"\n=== {label} ===");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.TranslateAsync(
            new VTTranslate.Core.Streaming.TranslationProviderRequest(sourceLang, targetLang, text), CancellationToken.None);
        sw.Stop();

        var endpointHost = config.Endpoint != null ? new Uri(config.Endpoint).Host : "api.cognitive.microsofttranslator.com";
        Console.WriteLine($"  Endpoint: {endpointHost}/translate?api-version=3.0 (genuine standalone Azure Translator Text API — NOT the bundled Speech Translation used elsewhere in this project)");
        Console.WriteLine($"  HTTP status: {result.HttpStatusCode?.ToString() ?? "n/a (request-level failure, see below)"}");
        Console.WriteLine($"  Authentication succeeded: {result.Success}");
        Console.WriteLine($"  Response latency: {sw.Elapsed.TotalMilliseconds:F0}ms");
        if (result.Success)
        {
            Console.WriteLine($"  Returned translation: \"{result.CandidateTranslatedText}\"");
            Console.WriteLine($"  Source came from genuine standalone Translator API: true");
        }
        else
        {
            Console.WriteLine($"  Failure reason: {result.FailureReason}");
        }
        return result.Success;
    }

    var deToEnOk = await RunDirectionAsync("German -> English", "de", "en", "Ich möchte morgen ein Treffen vereinbaren.");
    var enToDeOk = await RunDirectionAsync("English -> German", "en", "de", "I would like to schedule a meeting tomorrow.");

    Console.WriteLine($"\n\nSUMMARY: German->English succeeded={deToEnOk}, English->German succeeded={enToDeOk}");
    if (deToEnOk && enToDeOk)
        Console.WriteLine("Both basic directional calls succeeded — safe to proceed to the full translation-provider-feasibility-test.");
    else
        Console.WriteLine("At least one basic directional call FAILED — per instruction, STOP here. Do not proceed to the full 13-case experiment.");
}

static async Task RunTranslationProviderFeasibilityTestAsync()
{
    // Step 5.5/5.6a — real, end-to-end attempt to use a genuinely independent translation
    // provider (Azure Translator Text v3.0), via the actual AzureTranslatorTextProvider +
    // IncrementalTranslationProviderExperiment code path (not an ad hoc probe). This
    // requires only text input, not the speech pipeline, since it evaluates text-translation
    // provider capability independent of speech recognition. As of Step 5.6a, uses
    // DEDICATED AZURE_TRANSLATOR_KEY/AZURE_TRANSLATOR_REGION credentials — never
    // AZURE_SPEECH_KEY/AZURE_SPEECH_REGION, never a fallback between them (see
    // TranslatorCredentialLoader; fails clearly via LoadOrThrow if not configured).
    VTTranslate.Core.Streaming.TranslatorCredentialConfig translatorConfig;
    try
    {
        translatorConfig = VTTranslate.Core.Streaming.TranslatorCredentialLoader.LoadOrThrow();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
        return;
    }

    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var logDir = Path.Combine(testResultsRoot, "logs", "translation-provider-feasibility");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, "feasibility.log");
    if (File.Exists(logPath)) File.Delete(logPath);
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    var httpClient = new HttpClient();
    var provider = VTTranslate.Core.Streaming.AzureTranslatorTextProvider.FromCredentialConfig(httpClient, translatorConfig);

    // Representative stable-source segments for each of the 13 required test cases —
    // fed directly as text (no audio needed for a text-translation-provider capability
    // check). Segmented into 2 partial-style commits + a final per case, matching the
    // shape ObserveStableSegmentAsync/ObserveFinalAsync expect.
    var cases = new (string Name, string Seg1, string Seg2, string FinalSource, string FinalTranslated)[]
    {
        ("1_English", "I would like", "to schedule a meeting tomorrow.", "I would like to schedule a meeting tomorrow.", "(unavailable — see result)"),
        ("2_German", "Ich möchte morgen", "ein Treffen vereinbaren.", "Ich möchte morgen ein Treffen vereinbaren.", "(unavailable — see result)"),
        ("3_FastSpeech", "I would like to", "schedule a meeting and review the budget.", "I would like to schedule a meeting and review the budget.", "(unavailable — see result)"),
        ("4_LongSentence", "I would like to schedule a meeting tomorrow", "to discuss the quarterly budget and travel arrangements.", "I would like to schedule a meeting tomorrow to discuss the quarterly budget and travel arrangements.", "(unavailable — see result)"),
        ("5_ShortSentence", "No.", "", "No.", "(unavailable — see result)"),
        ("6_SelfCorrection", "I want to schedule", "no wait, reschedule the meeting.", "I want to reschedule the meeting.", "(unavailable — see result)"),
        ("7_MultipleUtterances", "Okay.", "See you then.", "See you then.", "(unavailable — see result)"),
        ("8_Conversational", "So um I was thinking", "maybe we could grab lunch tomorrow.", "So, um, I was thinking maybe we could grab lunch tomorrow.", "(unavailable — see result)"),
        ("9_BusinessTerminology", "Please update the invoice", "with the new purchase order number.", "Please update the invoice with the new purchase order number.", "(unavailable — see result)"),
        ("10_Idiomatic", "It's raining cats", "and dogs outside.", "It's raining cats and dogs outside.", "(unavailable — see result)"),
        ("11_Punctuation", "Please send the report, the invoice,", "and the summary — but not the draft, okay?", "Please send the report, the invoice, and the summary — but not the draft, okay?", "(unavailable — see result)"),
        ("12_Incomplete", "I was going to say", "something but", "I was going to say something but", "(unavailable — see result)"),
        ("13_FinalDivergesFromIncremental", "I think we should meet", "on Tuesday", "Actually, let's meet on Thursday instead.", "(unavailable — see result)"),
    };

    var experiment = new VTTranslate.Core.Streaming.IncrementalTranslationProviderExperiment(provider, logger, "en-US->de-DE", generation: 1);
    var anySuccess = false;
    var allStatusCodes = new HashSet<int>();

    foreach (var c in cases)
    {
        Console.WriteLine($"\n=== {c.Name} ===");
        var cumulative = c.Seg1;
        var r1 = await experiment.ObserveStableSegmentAsync(c.Name, 1, c.Seg1, cumulative, "en-US", "de-DE", CancellationToken.None);
        foreach (var r in r1)
        {
            Console.WriteLine($"  {r.StrategyName}: succeeded={r.CallSucceeded} httpStatus={r.HttpStatusCode} latencyMs={r.LatencyMs:F0} stability={r.Stability}");
            if (r.CallSucceeded) anySuccess = true;
            if (r.HttpStatusCode.HasValue) allStatusCodes.Add(r.HttpStatusCode.Value);
        }

        if (!string.IsNullOrEmpty(c.Seg2))
        {
            cumulative = (cumulative + " " + c.Seg2).Trim();
            var r2 = await experiment.ObserveStableSegmentAsync(c.Name, 2, c.Seg2, cumulative, "en-US", "de-DE", CancellationToken.None);
            foreach (var r in r2)
            {
                Console.WriteLine($"  {r.StrategyName}: succeeded={r.CallSucceeded} httpStatus={r.HttpStatusCode} latencyMs={r.LatencyMs:F0} stability={r.Stability}");
                if (r.CallSucceeded) anySuccess = true;
                if (r.HttpStatusCode.HasValue) allStatusCodes.Add(r.HttpStatusCode.Value);
            }
        }

        // Step 5.6b fix: obtain the REAL final translation via one more genuine provider
        // call, rather than the Step-5.5-era "(unavailable — see result)" placeholder text
        // (which produced meaningless reconciliation numbers now that real translations
        // are succeeding — every case's finalTokenCount was identically 4, i.e. the
        // placeholder's own token count, not a real final translation). This does not
        // modify IncrementalTranslationProviderExperiment itself — only the test data fed
        // into it, using the same provider/abstraction unchanged.
        var finalTranslationResult = await provider.TranslateAsync(
            new VTTranslate.Core.Streaming.TranslationProviderRequest("en-US", "de-DE", c.FinalSource), CancellationToken.None);
        var realFinalTranslated = finalTranslationResult.Success
            ? finalTranslationResult.CandidateTranslatedText!
            : c.FinalTranslated; // fall back to the placeholder only if even this call fails — never fabricate

        var final = await experiment.ObserveFinalAsync(c.Name, c.FinalSource, realFinalTranslated, "en-US", "de-DE", CancellationToken.None);
        foreach (var r in final)
            Console.WriteLine($"  [final] {r.StrategyName}: anySuccessfulCall={r.AnySuccessfulCall} callCount={r.CallCount} successCount={r.SuccessCount}");
    }

    Console.WriteLine($"\n\nSUMMARY across all {cases.Length} cases:");
    Console.WriteLine($"  Any successful translation call: {anySuccess}");
    Console.WriteLine($"  HTTP status codes observed: {string.Join(", ", allStatusCodes)}");
    Console.WriteLine($"  (full log: {logPath})");
}

static async Task GenerateStep5AudioAsync()
{
    // Step 5 additional case: L (terminology/business vocabulary).
    var (key, region) = RequireAzureConfig();
    var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results", "input-audio");
    Directory.CreateDirectory(outDir);

    async Task SynthSsml(string voice, string lang, string ssmlBody, string fileName, string description)
    {
        var config = SpeechConfig.FromSubscription(key, region);
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);
        var path = Path.Combine(outDir, fileName);
        using var audioConfig = AudioConfig.FromWavFileOutput(path);
        using var synth = new SpeechSynthesizer(config, audioConfig);
        var ssml = $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{lang}\">" +
                   $"<voice name=\"{voice}\">{ssmlBody}</voice></speak>";
        var result = await synth.SpeakSsmlAsync(ssml);
        Console.WriteLine(result.Reason == ResultReason.SynthesizingAudioCompleted
            ? $"Generated {fileName}: {description}"
            : $"FAILED to generate {fileName}: {result.Reason}");
    }

    await SynthSsml("en-US-JennyNeural", "en-US",
        "Please update the invoice with the new purchase order number and forward it to accounts payable before the quarterly reconciliation.",
        "test_l_terminology_en.wav", "EN, business/terminology-heavy sentence");
}

static async Task RunTranslationShadowTestAsync()
{
    // Step 5 live incremental-translation shadow evaluation. Reuses Step 1/4's existing
    // WAV files for A-D, F-H, Step 4's conversational/punctuation/mid-correction files for
    // I/J/K (relettered per Step 5's own A-L list, which differs from Step 4's), and the
    // new Step 5 terminology file for L. Case E is investigated separately via
    // case-e-investigate (already re-runnable and now also captures TranslationShadow*
    // log lines, since the shadow wiring is unconditional).
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var logDir = Path.Combine(testResultsRoot, "logs", "translation-shadow");
    Directory.CreateDirectory(logDir);

    var cases = new[]
    {
        ("A_normal_en_to_de", "test1_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("B_normal_de_to_en", "test2_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("C_fast_en_to_de", "test_c_fast_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("D_fast_de_to_en", "test_d_fast_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("F_long_en_to_de", "test_f_long_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("G_correction_en_to_de", "test_g_correction_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("H_consecutive_en_to_de", "test_h_consecutive_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("I_conversational_en_to_de", "test_j_conversational_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("J_punctuation_en_to_de", "test_k_punctuation_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("K_midcorrection_en_to_de", "test_l_midcorrection_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("L_terminology_en_to_de", "test_l_terminology_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
    };

    foreach (var (caseName, wavFile, sourceLang, targetLang, voice) in cases)
    {
        var wavPath = Path.Combine(inputDir, wavFile);
        if (!File.Exists(wavPath))
        {
            Console.WriteLine($"SKIP {caseName}: {wavPath} missing — run 'gen-measurement-audio', 'gen-step4-audio', and/or 'gen-step5-audio' first.");
            continue;
        }

        var logPath = Path.Combine(logDir, $"{caseName}.log");
        if (File.Exists(logPath)) File.Delete(logPath);
        var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

        Console.WriteLine($"\n=== {caseName} (translation shadow) ===");
        var provider = new AzureSpeechTranslationProvider(key!, region!, voice, logger);
        var source = new TestFileAudioInputSource(wavPath);
        var completionSignal = new TaskCompletionSource();
        provider.Error += (_, e) => Console.WriteLine($"  [error] {(e.IsFatal ? "FATAL" : "warn")}: {e.Message}");
        source.PcmChunkCaptured += (_, chunk) => provider.PushAudio(chunk);
        source.PlaybackCompleted += (_, _) => completionSignal.TrySetResult();

        var finalResultCount = 0;
        var audioSynthesizedCount = 0;
        provider.FinalResult += (_, _) => finalResultCount++;
        provider.AudioSynthesized += (_, _) => audioSynthesizedCount++;

        await provider.StartAsync(sourceLang, targetLang, CancellationToken.None);
        source.Start();
        await Task.WhenAny(completionSignal.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        await Task.Delay(3000);

        source.Stop();
        await provider.StopAsync();
        await provider.DisposeAsync();

        Console.WriteLine($"  production FinalResult events={finalResultCount} AudioSynthesized events={audioSynthesizedCount}");

        if (File.Exists(logPath))
        {
            var lines = await File.ReadAllLinesAsync(logPath);
            var relevant = lines.Where(l =>
                l.Contains("TranslationShadowPartial") || l.Contains("TranslationShadowFinalReconciliation") ||
                l.Contains("ShadowFinalized")).ToList();
            foreach (var line in relevant) Console.WriteLine("  " + line);
            Console.WriteLine($"  (full log: {logPath})");
        }
    }
}

static async Task GenerateStep4AudioAsync()
{
    // Step 4 additional cases: I (rapid consecutive utterances), J (natural conversational
    // sentence), K (punctuation-heavy sentence), L (mid-sentence correction, distinct from
    // Step 1's case G which was a false-start/restart rather than a same-utterance word swap).
    var (key, region) = RequireAzureConfig();
    var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results", "input-audio");
    Directory.CreateDirectory(outDir);

    async Task SynthSsml(string voice, string lang, string ssmlBody, string fileName, string description)
    {
        var config = SpeechConfig.FromSubscription(key, region);
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);
        var path = Path.Combine(outDir, fileName);
        using var audioConfig = AudioConfig.FromWavFileOutput(path);
        using var synth = new SpeechSynthesizer(config, audioConfig);
        var ssml = $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{lang}\">" +
                   $"<voice name=\"{voice}\">{ssmlBody}</voice></speak>";
        var result = await synth.SpeakSsmlAsync(ssml);
        Console.WriteLine(result.Reason == ResultReason.SynthesizingAudioCompleted
            ? $"Generated {fileName}: {description}"
            : $"FAILED to generate {fileName}: {result.Reason}");
    }

    await SynthSsml("en-US-JennyNeural", "en-US",
        "Okay.<break time=\"400ms\"/>Sure, that works.<break time=\"400ms\"/>See you then.<break time=\"400ms\"/>Bye.",
        "test_i_rapid_en.wav", "EN, several rapid consecutive short utterances");

    await SynthSsml("en-US-JennyNeural", "en-US",
        "So, um, I was thinking maybe we could grab lunch tomorrow if you're free around noon.",
        "test_j_conversational_en.wav", "EN, natural conversational sentence with filler words");

    await SynthSsml("en-US-JennyNeural", "en-US",
        "Please send the report, the invoice, and the summary — but not the draft, okay?",
        "test_k_punctuation_en.wav", "EN, sentence containing varied punctuation");

    await SynthSsml("en-US-JennyNeural", "en-US",
        "Can you send it to John, I mean Jonathan, by Friday afternoon?",
        "test_l_midcorrection_en.wav", "EN, mid-sentence correction (name swap, not a restart)");
}

static async Task RunCommitPolicyTestAsync()
{
    // Step 4 live commit-policy comparison. Reuses the exact real-Azure harness from
    // shadow-test (cases A-D, F-H) plus the new Step 4 cases I-L. Case E is deliberately
    // excluded here — see InvestigateCaseEAsync, run separately per Step 4's explicit
    // instruction to investigate it on its own rather than lump it into the main sweep.
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var inputDir = Path.Combine(testResultsRoot, "input-audio");
    var logDir = Path.Combine(testResultsRoot, "logs", "commit-policy");
    Directory.CreateDirectory(logDir);

    var cases = new[]
    {
        ("A_normal_en_to_de", "test1_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("B_normal_de_to_en", "test2_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("C_fast_en_to_de", "test_c_fast_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("D_fast_de_to_en", "test_d_fast_de.wav", "de-DE", "en-US", "en-US-JennyNeural"),
        ("F_long_en_to_de", "test_f_long_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("G_correction_en_to_de", "test_g_correction_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("H_consecutive_en_to_de", "test_h_consecutive_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("I_rapid_consecutive_en_to_de", "test_i_rapid_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("J_conversational_en_to_de", "test_j_conversational_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("K_punctuation_en_to_de", "test_k_punctuation_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
        ("L_midcorrection_en_to_de", "test_l_midcorrection_en.wav", "en-US", "de-DE", "de-DE-KatjaNeural"),
    };

    foreach (var (caseName, wavFile, sourceLang, targetLang, voice) in cases)
    {
        var wavPath = Path.Combine(inputDir, wavFile);
        if (!File.Exists(wavPath))
        {
            Console.WriteLine($"SKIP {caseName}: {wavPath} missing — run 'gen-measurement-audio' and/or 'gen-step4-audio' first.");
            continue;
        }

        var logPath = Path.Combine(logDir, $"{caseName}.log");
        if (File.Exists(logPath)) File.Delete(logPath);
        var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

        Console.WriteLine($"\n=== {caseName} (Policy A vs Policy B) ===");
        var provider = new AzureSpeechTranslationProvider(key!, region!, voice, logger);
        var source = new TestFileAudioInputSource(wavPath);
        var completionSignal = new TaskCompletionSource();
        provider.Error += (_, e) => Console.WriteLine($"  [error] {(e.IsFatal ? "FATAL" : "warn")}: {e.Message}");
        source.PcmChunkCaptured += (_, chunk) => provider.PushAudio(chunk);
        source.PlaybackCompleted += (_, _) => completionSignal.TrySetResult();

        var finalResultCount = 0;
        var audioSynthesizedCount = 0;
        provider.FinalResult += (_, _) => finalResultCount++;
        provider.AudioSynthesized += (_, _) => audioSynthesizedCount++;

        await provider.StartAsync(sourceLang, targetLang, CancellationToken.None);
        source.Start();
        await Task.WhenAny(completionSignal.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        await Task.Delay(3000);

        source.Stop();
        await provider.StopAsync();
        await provider.DisposeAsync();

        Console.WriteLine($"  production FinalResult events={finalResultCount} AudioSynthesized events={audioSynthesizedCount}");

        if (File.Exists(logPath))
        {
            var lines = await File.ReadAllLinesAsync(logPath);
            var relevant = lines.Where(l =>
                l.Contains("|PolicyA] Shadow") || l.Contains("|PolicyB] Shadow") ||
                l.Contains("PartialMeasurement") || l.Contains("UtteranceSummary")).ToList();
            foreach (var line in relevant) Console.WriteLine("  " + line);
            Console.WriteLine($"  (full log: {logPath})");
        }
    }
}

static async Task InvestigateCaseEAsync()
{
    // Dedicated Case E (short utterance) investigation, per Step 4's explicit instruction
    // to determine: did Azure produce no events, did audio reach the recognizer, did a
    // timeout occur before recognition, is this harness-specific, or is the utterance
    // itself problematic? Does NOT modify eligibility thresholds.
    var (key, region) = RequireAzureConfig();
    var testResultsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-results");
    var wavPath = Path.Combine(testResultsRoot, "input-audio", "test_e_short_en.wav");
    var logDir = Path.Combine(testResultsRoot, "logs", "case-e-investigation");
    Directory.CreateDirectory(logDir);

    if (!File.Exists(wavPath))
    {
        Console.WriteLine($"Cannot investigate: {wavPath} missing — run 'gen-measurement-audio' first.");
        return;
    }

    var fileInfo = new FileInfo(wavPath);
    // 16kHz, 16-bit mono PCM: 32000 bytes/sec of audio, ~44-byte WAV header.
    var approxAudioMs = (fileInfo.Length - 44) / 32.0;
    Console.WriteLine($"test_e_short_en.wav: {fileInfo.Length} bytes, approx {approxAudioMs:F0}ms of audio.");

    var logPath = Path.Combine(logDir, "case_e_long_timeout.log");
    if (File.Exists(logPath)) File.Delete(logPath);
    var logger = new VTTranslate.Core.Diagnostics.FileDiagnosticLogger(logPath);

    Console.WriteLine("\n=== Case E investigation: 60s timeout, explicit chunk/connection tracking ===");
    var provider = new AzureSpeechTranslationProvider(key!, region!, "de-DE-KatjaNeural", logger);
    var source = new TestFileAudioInputSource(wavPath);
    var completionSignal = new TaskCompletionSource();
    var chunkCount = 0;
    var totalBytesPushed = 0L;
    provider.Error += (_, e) => Console.WriteLine($"  [error] {(e.IsFatal ? "FATAL" : "warn")}: {e.Message}");
    provider.StatusChanged += (_, s) => Console.WriteLine($"  [status] {s}");
    provider.PartialResult += (_, _) => Console.WriteLine("  [PartialResult event fired]");
    provider.FinalResult += (_, r) => Console.WriteLine($"  [FinalResult event fired] textLength={r.SourceText.Length}");
    source.PcmChunkCaptured += (_, chunk) =>
    {
        chunkCount++;
        totalBytesPushed += chunk.Length;
        provider.PushAudio(chunk);
    };
    source.PlaybackCompleted += (_, _) =>
    {
        Console.WriteLine($"  [harness] playback completed: {chunkCount} chunks, {totalBytesPushed} bytes pushed to PushAudio");
        completionSignal.TrySetResult();
    };

    var swConnect = System.Diagnostics.Stopwatch.StartNew();
    await provider.StartAsync("en-US", "de-DE", CancellationToken.None);
    Console.WriteLine($"  [harness] StartAsync returned after {swConnect.ElapsedMilliseconds}ms");
    source.Start();

    // Deliberately much longer than the standard 30s window, to distinguish "Azure just
    // needed more time" from "Azure will never produce an event for this input."
    var waited = await Task.WhenAny(completionSignal.Task, Task.Delay(TimeSpan.FromSeconds(60)));
    Console.WriteLine(waited == completionSignal.Task
        ? "  [harness] playback completion signaled normally"
        : "  [harness] TIMED OUT waiting for playback completion (60s) — harness-side stall, not just recognition delay");

    // Extra settle time well beyond the 3s used elsewhere, in case recognition is simply slow.
    await Task.Delay(TimeSpan.FromSeconds(15));

    source.Stop();
    await provider.StopAsync();
    await provider.DisposeAsync();

    Console.WriteLine($"\n  SUMMARY: chunks pushed={chunkCount} bytes pushed={totalBytesPushed} (audio DID reach PushAudio: {chunkCount > 0})");
    if (File.Exists(logPath))
    {
        var lines = await File.ReadAllLinesAsync(logPath);
        Console.WriteLine("  --- full diagnostic log ---");
        foreach (var line in lines) Console.WriteLine("  " + line);
        Console.WriteLine($"  (full log: {logPath})");
    }
}

record TestCase(string Name, string WavPath, string SourceLang, string TargetLang, string ExpectedSourceText, string TargetVoice);

record TestResult(
    string Name, string SourceLang, string TargetLang, string Expected,
    string ActualTranscript, string ActualTranslation,
    double RecognitionMs, double SynthesisMs, double EndToEndMs,
    bool Passed, string Notes, List<string> Errors, int FinalResultCount);
