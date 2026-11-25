using System.Diagnostics;
using System.Text;

namespace FamilyMemories.Media;

public sealed record ToolResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs ffmpeg/ffprobe (and later restic, pandoc) with argument lists, never a shell string.</summary>
public static class ExternalTool
{
    /// <param name="environment">Extra environment variables (passwords go here, never in arguments).</param>
    public static async Task<ToolResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} did not start");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = PumpAsync(process.StandardOutput, stdout);
        var errTask = PumpAsync(process.StandardError, stderr);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{fileName} took longer than {timeout}");
        }
        await Task.WhenAll(outTask, errTask);
        return new ToolResult(process.ExitCode, stdout.ToString(), Tail(stderr.ToString(), 2000));
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
        {
            sink.Append(buffer, 0, n);
            if (sink.Length > 1_000_000) sink.Remove(0, sink.Length - 1_000_000);
        }
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : s[^max..];
}
