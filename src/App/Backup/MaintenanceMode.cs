using System.Net;
using FamilyMemories.Configuration;

namespace FamilyMemories.Backup;

/// <summary>While a restore runs, every visitor sees a calm "back in a few minutes" page.</summary>
public sealed class MaintenanceMode
{
    private volatile string? message;
    public bool IsOn => message is not null;
    public string? Message => message;
    public void Enter(string text) => message = text;
    public void Exit() => message = null;
}

public static class MaintenanceMiddleware
{
    /// <summary>Must run before authentication: during a restore the database is being replaced.</summary>
    public static IApplicationBuilder UseMaintenancePage(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var maintenance = context.RequestServices.GetRequiredService<MaintenanceMode>();
        var path = context.Request.Path;
        if (!maintenance.IsOn || path.StartsWithSegments("/healthz") || path.StartsWithSegments("/css")
            || path.StartsWithSegments("/js") || path.StartsWithSegments("/img") || path.StartsWithSegments("/fonts"))
        {
            await next();
            return;
        }

        var app = context.RequestServices.GetRequiredService<AppOptions>();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "60";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($"""
            <!DOCTYPE html>
            <html lang="en" class="text-0"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="refresh" content="20">
            <title>Back in a few minutes · {WebUtility.HtmlEncode(app.AppName)}</title>
            <link rel="stylesheet" href="/css/site.css"></head>
            <body><main id="main" class="wrap">
            <h1>Back in a few minutes</h1>
            <p class="lead">{WebUtility.HtmlEncode(maintenance.Message ?? "")}</p>
            <p>Your stories are safe. This page will refresh by itself.</p>
            </main></body></html>
            """);
    });
}
