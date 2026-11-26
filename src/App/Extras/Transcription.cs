using System.Net.Http.Headers;
using System.Text.Json;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Media;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Extras;

/// <summary>Turns a recording into text. Production: a local Whisper container (faster-whisper, CPU, "small" model).</summary>
public interface ITranscriber
{
    Task<string> TranscribeAsync(string audioPath, CancellationToken ct);
}

/// <summary>
/// Talks to a Whisper server. Two kinds are understood:
/// <list type="bullet">
/// <item>onerahmet/openai-whisper-asr-webservice (the stack's own <c>whisper</c> container): POST {WHISPER_URL}/asr.</item>
/// <item>Anything OpenAI-compatible, when WHISPER_URL ends in <c>/v1</c> (whisper.cpp's whisper-server, speaches, LocalAI…,
/// e.g. on a Mac mini using its GPU): POST {WHISPER_URL}/audio/transcriptions with model WHISPER_MODEL.</item>
/// </list>
/// Either way nothing leaves the family's own machines.
/// </summary>
public sealed class WhisperTranscriber(IHttpClientFactory http, AppOptions options) : ITranscriber
{
    public async Task<string> TranscribeAsync(string audioPath, CancellationToken ct)
    {
        if (options.WhisperUrl is null) throw new InvalidOperationException("WHISPER_URL is not set.");
        var baseUrl = options.WhisperUrl.TrimEnd('/');
        var openAi = IsOpenAiStyle(baseUrl);
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(await File.ReadAllBytesAsync(audioPath, ct));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
        Uri url;
        if (openAi)
        {
            form.Add(audio, "file", Path.GetFileName(audioPath));
            form.Add(new StringContent(options.WhisperModel), "model");
            form.Add(new StringContent("en"), "language");
            form.Add(new StringContent("json"), "response_format");
            url = new Uri(baseUrl + "/audio/transcriptions");
        }
        else
        {
            form.Add(audio, "audio_file", Path.GetFileName(audioPath));
            url = new Uri(baseUrl + "/asr?task=transcribe&language=en&output=json&encode=true");
        }
        using var response = await http.CreateClient("whisper").PostAsync(url, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Whisper answered {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? "" : "";
    }

    /// <summary>WHISPER_URL ending in /v1 means an OpenAI-compatible server.</summary>
    public static bool IsOpenAiStyle(string whisperUrl) => whisperUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase);
}

public sealed class TranscriptionJob(AppDbContext db, ITranscriber transcriber, MediaStorage storage, ILogger<TranscriptionJob> log)
{
    [Hangfire.AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid attachmentId, CancellationToken ct)
    {
        var a = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId, ct);
        if (a is null || a.TranscriptState is not TranscriptState.Pending) return;
        try
        {
            var text = await transcriber.TranscribeAsync(storage.PathFor(a, MediaVariant.Audio), ct);
            a.Transcript = text.Length > 100_000 ? text[..100_000] : text;
            a.TranscriptState = string.IsNullOrWhiteSpace(text) ? TranscriptState.Failed : TranscriptState.Ready;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Transcription of {AttachmentId} failed", attachmentId);
            a.TranscriptState = TranscriptState.Failed;
        }
        await db.SaveChangesAsync(ct);
    }
}
