using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Media;
using ImageMagick;
using QRCoder;

namespace FamilyMemories.Exports;

/// <summary>
/// HTML → PDF through Gotenberg's Chromium (§2). The document and every asset it uses are sent
/// together (Gotenberg has no network access to the app), and Chromium waits until Paged.js has
/// laid out the pages (<c>window.pagedDone</c>) before printing.
/// </summary>
public sealed partial class GotenbergClient(IHttpClientFactory http, AppOptions options)
{
    public async Task<byte[]> HtmlToPdfAsync(string folder, bool waitForPagedJs, bool outline, CancellationToken ct)
    {
        if (options.GotenbergUrl is null) throw new InvalidOperationException("GOTENBERG_URL is not set, so PDFs can't be made.");
        using var form = new MultipartFormDataContent();
        foreach (var file in Directory.GetFiles(folder))
        {
            var content = new ByteArrayContent(await File.ReadAllBytesAsync(file, ct));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "files", Path.GetFileName(file));
        }
        void Field(string name, string value) => form.Add(new StringContent(value), name);
        Field("preferCssPageSize", "true");
        Field("printBackground", "true");
        foreach (var m in new[] { "marginTop", "marginBottom", "marginLeft", "marginRight" }) Field(m, "0");
        if (waitForPagedJs) Field("waitForExpression", "window.pagedDone === true");
        // A JavaScript error (e.g. Paged.js rejecting a CSS rule) fails at once with the message, instead of waiting for the timeout.
        Field("failOnConsoleExceptions", "true");
        if (outline) Field("generateDocumentOutline", "true");

        var client = http.CreateClient("gotenberg-render");
        using var response = await client.PostAsync(new Uri(new Uri(options.GotenbergUrl.TrimEnd('/') + "/"), "forms/chromium/convert/html"), form, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gotenberg could not make the PDF ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Counts pages in a Chromium-made PDF (page objects, not the page tree).</summary>
    public static int CountPages(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        return PageRegex().Matches(text).Count;
    }

    [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
    private static partial Regex PageRegex();
}

/// <summary>Prepares images for a document: right size for screen or print, sRGB JPEG, no metadata.</summary>
public sealed class ExportImages(MediaStorage storage)
{
    /// <param name="maxPixels">Longest side: about 1600 for a screen PDF (~150 dpi), 3000 for print (300 dpi).</param>
    public (string FileName, int Width, int Height) Prepare(Attachment a, string folder, int maxPixels, string prefix = "img")
    {
        var name = $"{prefix}-{a.Id:N}.jpg";
        var source = a.Kind == AttachmentKind.Video ? storage.PathFor(a, MediaVariant.Poster)
            : File.Exists(storage.PathFor(a, MediaVariant.Original)) ? storage.PathFor(a, MediaVariant.Original)
            : storage.PathFor(a, MediaVariant.Web);
        try
        {
            using var image = new MagickImage(source);
            image.AutoOrient();
            if (image.Width > maxPixels || image.Height > maxPixels)
                image.Resize(new MagickGeometry((uint)maxPixels, (uint)maxPixels) { Greater = true });
            image.Strip();
            image.ColorSpace = ColorSpace.sRGB;
            image.Format = MagickFormat.Jpeg;
            image.Quality = 88;
            image.Write(Path.Combine(folder, name));
            return (name, (int)image.Width, (int)image.Height);
        }
        catch (MagickException)
        {
            // Fall back to the web copy if the original can't be read.
            File.Copy(storage.PathFor(a, MediaVariant.Web), Path.Combine(folder, name), overwrite: true);
            using var info = new MagickImage(Path.Combine(folder, name));
            return (name, (int)info.Width, (int)info.Height);
        }
    }

    public static string Qr(string url, string folder, Guid id)
    {
        var name = $"qr-{id:N}.png";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        File.WriteAllBytes(Path.Combine(folder, name), new PngByteQRCode(data).GetGraphic(10));
        return name;
    }
}
