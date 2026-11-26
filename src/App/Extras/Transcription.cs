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

/// <summary>Talks to onerahmet/openai-whisper-asr-webservice (POST /asr). Nothing leaves the family's server.</summary>
public sealed class WhisperTranscriber(IHttpClientFactory http, AppOptions options) : ITranscriber
{
    public async Task<string> TranscribeAsync(string audioPath, CancellationToken ct)
    {
        if (options.WhisperUrl is null) throw new InvalidOperationException("WHISPER_URL is not set.");
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(await File.ReadAllBytesAsync(audioPath, ct));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
        form.Add(audio, "audio_file", Path.GetFileName(audioPath));
        var url = new Uri(new Uri(options.WhisperUrl.TrimEnd('/') + "/"), "asr?task=transcribe&language=en&output=json&encode=true");
        using var response = await http.CreateClient("whisper").PostAsync(url, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Whisper answered {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? "" : "";
    }
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
