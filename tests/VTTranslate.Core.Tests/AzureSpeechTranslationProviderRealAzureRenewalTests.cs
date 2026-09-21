using VTTranslate.Core.Providers;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Phase 7.2 — REAL AZURE VALIDATION of the actual PRODUCTION implementation (not a
/// reimplementation): calls the real, shipped
/// <see cref="AzureSpeechTranslationProvider.StartAsync"/>,
/// <see cref="AzureSpeechTranslationProvider.PushAudio"/>, and
/// <see cref="AzureSpeechTranslationProvider.TryUpdateAuthorizationTokenAsync"/>
/// against a live Azure Speech endpoint. This goes further than the Phase 7.2A
/// experiment (which validated raw Azure SDK behavior via a standalone harness) —
/// this test exercises the PRODUCTION method itself, proving the actual shipped code
/// integrates correctly with the validated SDK behavior.
///
/// Two independent short-lived tokens are obtained via the same STS mechanism
/// <c>AzureProviderCredentialIssuer</c> already uses server-side
/// (no Entra tenant is configured in this environment to exercise the full
/// authenticated <c>/provider-access</c> HTTP chain — see
/// docs/phase-7.2-long-running-translation-session-continuity.md's own "REAL AZURE
/// VALIDATION" addendum for the identical, already-accepted scope boundary). Self-skips
/// (never fails the whole suite, never fabricates a result) when
/// AZURE_SPEECH_KEY/AZURE_SPEECH_REGION are not configured — see
/// <see cref="SkipIfNoAzureSpeechCredentialsFactAttribute"/>.
///
/// Never logs recognized/translated text; never logs the raw token value.
/// </summary>
public class AzureSpeechTranslationProviderRealAzureRenewalTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "VTTranslate.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not locate repository root (VTTranslate.sln) from test output directory.");
    }

    private static async Task<string> IssueTokenAsync(HttpClient http, string region, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken");
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Content = new StringContent(string.Empty);
        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static byte[] ReadPcmDataFromWav(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        reader.ReadBytes(12); // "RIFF" + size + "WAVE"
        while (true)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();
            if (chunkId == "data") return reader.ReadBytes(chunkSize);
            reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
        }
    }

    [SkipIfNoAzureSpeechCredentialsFact("test-results/input-audio/test_f_long_de.wav")]
    public async Task ProductionProvider_TryUpdateAuthorizationTokenAsync_ContinuesRecognitionOnTheSameRecognizer_AgainstRealAzure()
    {
        var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION")!;
        var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY")!;
        using var http = new HttpClient();

        var tokenA = await IssueTokenAsync(http, region, key);

        // The actual, shipped production factory (Phase 7.1) — not a reimplementation.
        await using var provider = AzureSpeechTranslationProvider.FromAuthorizationToken(tokenA, region, "en-US-JennyNeural");

        var recognizedCount = 0;
        var recognizedAfterRenewal = 0;
        var fatalErrors = new List<string>();
        DateTimeOffset? renewalAppliedAt = null;

        provider.FinalResult += (_, r) =>
        {
            Interlocked.Increment(ref recognizedCount);
            if (renewalAppliedAt is not null && DateTimeOffset.UtcNow > renewalAppliedAt)
                Interlocked.Increment(ref recognizedAfterRenewal);
            // Never assert/log r.SourceText/r.TranslatedText — metadata only.
        };
        provider.Error += (_, e) => { if (e.IsFatal) fatalErrors.Add(e.Message); };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await provider.StartAsync("de-DE", "en-US", cts.Token);

        var wavPath = Path.Combine(RepoRoot(), "test-results", "input-audio", "test_f_long_de.wav");
        var pcm = ReadPcmDataFromWav(wavPath);
        const int chunkBytes = 3200; // ~100ms at 16kHz/16-bit/mono — same production chunking assumption as MainViewModel's real audio pipeline
        var renewed = false;
        var renewalAtByte = pcm.Length / 3; // renew during active speech, not silence

        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            var len = Math.Min(chunkBytes, pcm.Length - offset);
            provider.PushAudio(pcm.AsSpan(offset, len)); // the actual production method
            await Task.Delay(90);

            if (!renewed && offset + len >= renewalAtByte)
            {
                var tokenB = await IssueTokenAsync(http, region, key);
                // THE method under test — the actual production renewal API.
                var applied = await provider.TryUpdateAuthorizationTokenAsync(tokenB, CancellationToken.None);
                renewalAppliedAt = DateTimeOffset.UtcNow;
                renewed = true;

                Assert.True(applied, "TryUpdateAuthorizationTokenAsync must return true for a live, already-started provider.");
            }
        }

        await Task.Delay(3000); // allow a trailing final result to arrive
        await provider.StopAsync();

        Assert.True(renewed, "the renewal step must actually have executed for this test to be meaningful");
        Assert.True(recognizedCount >= 1, $"expected at least one final recognition result; got {recognizedCount}");
        Assert.True(recognizedAfterRenewal >= 1, $"expected recognition to continue AFTER the live token replacement; got {recognizedAfterRenewal} post-renewal final result(s) out of {recognizedCount} total");
        Assert.Empty(fatalErrors); // no forced reconnect/fatal error caused by the renewal itself
    }
}
