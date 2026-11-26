using System.Net;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Antiforgery;

namespace FamilyMemories.Email;

public static class EmailPreviewEndpoint
{
    /// <summary>
    /// The preview shown in an iframe on the admin "Email wording" page.
    /// GET renders the saved wording; POST renders the draft being edited (with example details).
    /// </summary>
    public static void MapEmailPreview(this IEndpointRouteBuilder app) =>
        app.MapMethods("/admin/email/preview/{kind}", ["GET", "POST"], async (EmailKind kind, HttpContext http,
            EmailTemplates templates, IAntiforgery antiforgery) =>
        {
            if (!EmailTemplates.Defaults.ContainsKey(kind)) return Results.NotFound();
            EmailTemplate? draft = null;
            if (HttpMethods.IsPost(http.Request.Method))
            {
                await antiforgery.ValidateRequestAsync(http);
                var form = await http.Request.ReadFormAsync();
                draft = new EmailTemplate(form["Subject"].ToString(), form["TextBody"].ToString(), form["HtmlBody"].ToString());
                foreach (var part in new[] { draft.Subject, draft.TextBody, draft.HtmlBody })
                {
                    if (EmailTemplates.Validate(part) is { } error)
                        return Results.Content(
                            $"""<p style="font-family:sans-serif;color:#a4161a">There's a mistake in the template: {WebUtility.HtmlEncode(error)}</p>""",
                            "text/html; charset=utf-8");
                }
            }

            var rendered = await templates.RenderAsync(kind, "Rose", templates.SampleModel(kind), "https://example.invalid/email/preferences", draft);
            var subjectBar = $"""<div style="font-family:sans-serif;background:#eee;padding:8px 12px;font-size:15px;margin:0 0 16px"><strong>Subject:</strong> {WebUtility.HtmlEncode(rendered.Subject)}</div>""";
            var html = rendered.HtmlBody.Replace("<div style=\"max-width:600px", subjectBar + "<div style=\"max-width:600px");
            return Results.Content(html, "text/html; charset=utf-8");
        }).RequireAuthorization("Admin");
}
