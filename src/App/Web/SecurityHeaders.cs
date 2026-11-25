namespace FamilyMemories.Web;

/// <summary>
/// Strict defaults for every response. Scripts may only come from our own origin
/// (no inline scripts), and the app can't be framed.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' data: blob:; " +
        "media-src 'self' blob:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var h = context.Response.Headers;
                // The Hangfire dashboard (admin-only) ships inline scripts; everything else is strict.
                h.ContentSecurityPolicy = context.Request.Path.StartsWithSegments("/admin/jobs")
                    ? ContentSecurityPolicy.Replace("script-src 'self'", "script-src 'self' 'unsafe-inline'")
                        .Replace("style-src 'self'", "style-src 'self' 'unsafe-inline'")
                    : context.Request.Path.Equals("/tree", StringComparison.OrdinalIgnoreCase)
                        // family-chart positions cards with style attributes; still no inline scripts.
                        ? ContentSecurityPolicy.Replace("style-src 'self'", "style-src 'self'; style-src-attr 'unsafe-inline'")
                        : ContentSecurityPolicy;
                h.XContentTypeOptions = "nosniff";
                h.XFrameOptions = "DENY";
                h["Referrer-Policy"] = "same-origin";
                h["Permissions-Policy"] = "camera=(), geolocation=(), microphone=(self), payment=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                return Task.CompletedTask;
            });
            await next();
        });
}
