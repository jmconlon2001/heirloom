using System.Collections;
using System.Net;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;
using Scriban;
using Scriban.Runtime;

namespace FamilyMemories.Email;

public sealed record EmailTemplate(string Subject, string TextBody, string HtmlBody);

public sealed record RenderedEmail(string Subject, string TextBody, string HtmlBody);

/// <summary>
/// Email wording (§8). Every kind has a built-in template; admins can override subject, text and HTML
/// under Family admin → Email. Templates are Scriban (<c>{{ name }}</c>). For the HTML version every value
/// is HTML-encoded before rendering, so an edited template can't break the page; <c>{{ button url "Label" }}</c>
/// draws the one big button. Every message is wrapped in the same layout with the help line and an
/// email-settings / unsubscribe link.
/// </summary>
public sealed class EmailTemplates(IServiceScopeFactory scopes, AppOptions options)
{
    public static readonly IReadOnlyDictionary<EmailKind, EmailTemplate> Defaults = new Dictionary<EmailKind, EmailTemplate>
    {
        [EmailKind.LoginLink] = new(
            "Your sign-in link for {{ app_name }}",
            """
            Hello {{ name }},

            Here is your link to sign in to {{ app_name }}:

            {{ link }}

            The link works once and stays good for 14 days. After you use it, this device will remember you.

            If you didn't ask for this, you can ignore this email.
            """,
            """
            <p style="font-size:22px;margin:0 0 16px">Hello {{ name }},</p>
            <p style="margin:0 0 24px">Tap the big button to sign in to {{ app_name }}.</p>
            {{ button link "Sign me in" }}
            <p style="margin:0 0 16px">The button works once and stays good for 14 days. After you use it, this device will remember you.</p>
            <p style="margin:0 0 16px;color:#4a4238">If you didn't ask for this, you can ignore this email.</p>
            """),

        [EmailKind.Invitation] = new(
            "{{ inviter_name }} invited you to {{ app_name }}",
            """
            Hello {{ name }},

            {{ inviter_name }} has invited you to {{ app_name }}, a private place for our family to save its stories.

            Tap this link to get started. There's no password to remember:

            {{ link }}

            The link works once and stays good for 14 days. After you use it, this device will remember you.
            """,
            """
            <p style="font-size:22px;margin:0 0 16px">Hello {{ name }},</p>
            <p style="margin:0 0 16px">{{ inviter_name }} has invited you to {{ app_name }}, a private place for our family to save its stories.</p>
            <p style="margin:0 0 24px">Tap the big button to get started. There's no password to remember.</p>
            {{ button link "Get started" }}
            <p style="margin:0 0 16px">The button works once and stays good for 14 days. After you use it, this device will remember you.</p>
            """),

        [EmailKind.Reminder] = new(
            "{{ if asked_by }}{{ asked_by }} asked you a question{{ else }}A question for you: {{ prompt_title }}{{ end }}",
            """
            Hello {{ name }},

            {{ if asked_by }}{{ asked_by }} asked you:{{ else }}Here is this week's question:{{ end }}

            {{ question }}
            {{ for hint in hints }}
              - {{ hint }}{{ end }}

            Answer it here (the link signs you in):
            {{ link }}
            {{ if reply_by_email }}
            Or simply reply to this email with your story. You can attach photos too.
            {{ end }}
            {{ progress }}
            """,
            """
            <p style="font-size:22px;margin:0 0 16px">Hello {{ name }},</p>
            <p style="margin:0 0 8px;color:#4a4238">{{ if asked_by }}{{ asked_by }} asked you:{{ else }}Here is this week's question:{{ end }}</p>
            <p style="font-size:26px;line-height:1.35;font-weight:bold;margin:0 0 16px">{{ question }}</p>
            {{ if hints.size > 0 }}<p style="margin:0 0 4px">Some things you might write about:</p>
            <ul style="margin:0 0 24px;padding-left:24px">{{ for hint in hints }}<li style="margin:0 0 6px">{{ hint }}</li>{{ end }}</ul>{{ end }}
            {{ button link "Answer this question" }}
            {{ if reply_by_email }}<p style="margin:0 0 16px">Or simply <strong>reply to this email</strong> with your story. You can attach photos too.</p>{{ end }}
            <p style="margin:0 0 16px;color:#4a4238">{{ progress }}</p>
            """),

        [EmailKind.ReadSummary] = new(
            "{{ headline }}",
            """
            Hello {{ name }},

            {{ for story in stories }}{{ story.readers }} read your story "{{ story.title }}".
            {{ end }}
            Your stories mean a lot to the family. Thank you for sharing them.
            """,
            """
            <p style="font-size:22px;margin:0 0 16px">Hello {{ name }},</p>
            {{ for story in stories }}<p style="margin:0 0 12px"><strong>{{ story.readers }}</strong> read your story <a href="{{ story.url }}" style="color:#2f5d46">“{{ story.title }}”</a>.</p>{{ end }}
            <p style="margin:16px 0">Your stories mean a lot to the family. Thank you for sharing them.</p>
            """),

        [EmailKind.Digest] = new(
            "New family stories this week",
            """
            Hello {{ name }},

            Here's what the family shared this week:
            {{ for story in stories }}
            - "{{ story.title }}", told by {{ story.teller }}
              {{ story.url }}{{ end }}
            """,
            """
            <p style="font-size:22px;margin:0 0 16px">Hello {{ name }},</p>
            <p style="margin:0 0 16px">Here's what the family shared this week:</p>
            {{ for story in stories }}<p style="margin:0 0 14px"><a href="{{ story.url }}" style="color:#2f5d46;font-weight:bold;font-size:21px">{{ story.title }}</a><br><span style="color:#4a4238">told by {{ story.teller }}</span></p>{{ end }}
            """),

        [EmailKind.ExportReady] = new(
            "Your {{ export_name }} is ready",
            """
            Hello {{ name }},

            The {{ export_name }} you asked for is ready to download:
            {{ link }}
            """,
            """
            <p style="font-size:22px;margin:0 0 16px">Hello {{ name }},</p>
            <p style="margin:0 0 24px">The {{ export_name }} you asked for is ready.</p>
            {{ button link "Download it" }}
            """),

        [EmailKind.AdminAlert] = new(
            "[{{ app_name }}] {{ subject }}",
            """
            {{ message }}

            Details: {{ link }}
            """,
            """
            <p style="margin:0 0 16px">{{ message }}</p>
            {{ button link "See the details" }}
            """),

        [EmailKind.AdminNudge] = new(
            "{{ storyteller }} hasn't added a story in a while",
            """
            Hello {{ name }},

            {{ storyteller }} hasn't added a story in {{ weeks }} weeks, even with {{ reminders }} reminder emails. Maybe give them a call?

            {{ link }}
            """,
            """
            <p style="font-size:22px;margin:0 0 16px">Hello {{ name }},</p>
            <p style="margin:0 0 24px"><strong>{{ storyteller }}</strong> hasn't added a story in {{ weeks }} weeks, even with {{ reminders }} reminder emails. Maybe give them a call?</p>
            {{ button link "See their questions" }}
            """),

        [EmailKind.Test] = new(
            "Test email from {{ app_name }}",
            """
            Hello {{ name }},

            This is a test. If you can read it, {{ app_name }} can send email.
            """,
            """
            <p style="font-size:22px;margin:0 0 16px">Hello {{ name }},</p>
            <p>This is a test. If you can read it, {{ app_name }} can send email.</p>
            """),
    };

    public static readonly IReadOnlyDictionary<EmailKind, string> Descriptions = new Dictionary<EmailKind, string>
    {
        [EmailKind.LoginLink] = "Sign-in link (someone asked for one on the sign-in page)",
        [EmailKind.Invitation] = "Invitation (sent when you add someone)",
        [EmailKind.Reminder] = "Reminder with the next question (and follow-up questions)",
        [EmailKind.ReadSummary] = "\"The family read your story\"",
        [EmailKind.Digest] = "Weekly \"New family stories\"",
        [EmailKind.ExportReady] = "Export is ready",
        [EmailKind.AdminAlert] = "Alert to admins (backups)",
        [EmailKind.AdminNudge] = "Nudge to admins when a storyteller goes quiet",
        [EmailKind.Test] = "Test email",
    };

    /// <summary>Example values for previews and test sends.</summary>
    public Dictionary<string, object?> SampleModel(EmailKind kind) => kind switch
    {
        EmailKind.Reminder => new()
        {
            ["question"] = "Where and when were you born? Do you know how your name was chosen?",
            ["prompt_title"] = "Where you were born",
            ["hints"] = new List<object> { "What town or city were you born in?", "Were you named after anyone in the family?" },
            ["asked_by"] = null,
            ["link"] = options.BuildUrl("account/magic-link?token=example").ToString(),
            ["progress"] = "You've shared 12 stories so far.",
            ["reply_by_email"] = options.EmailRepliesEnabled,
        },
        EmailKind.ReadSummary => new()
        {
            ["headline"] = "3 family members read your story",
            ["stories"] = new List<object> { new Dictionary<string, object?> { ["title"] = "The farm", ["readers"] = "Lily, Ed and Ann", ["url"] = options.BuildUrl("library").ToString() } },
        },
        EmailKind.Digest => new()
        {
            ["stories"] = new List<object> { new Dictionary<string, object?> { ["title"] = "The day I met Joe", ["teller"] = "Rose", ["url"] = options.BuildUrl("library").ToString() } },
        },
        EmailKind.AdminNudge => new() { ["storyteller"] = "Grandma Rose", ["weeks"] = 3, ["reminders"] = 3, ["link"] = options.BuildUrl("admin/users").ToString() },
        EmailKind.AdminAlert => new() { ["subject"] = "Backup failed", ["message"] = "The nightly backup failed: the backup disk is not mounted.", ["link"] = options.BuildUrl("admin/backups").ToString() },
        EmailKind.ExportReady => new() { ["export_name"] = "family book", ["link"] = options.BuildUrl("admin/exports").ToString() },
        EmailKind.Invitation => new() { ["inviter_name"] = "Jacob", ["link"] = options.BuildUrl("account/magic-link?token=example").ToString() },
        _ => new() { ["link"] = options.BuildUrl("account/magic-link?token=example").ToString() },
    };

    public async Task<EmailTemplate> GetAsync(EmailKind kind)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var custom = await db.EmailTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Kind == kind);
        return custom is null ? Defaults[kind] : new EmailTemplate(custom.Subject, custom.TextBody, custom.HtmlBody);
    }

    /// <summary>Checks a template compiles; returns the first error in plain words, or null.</summary>
    public static string? Validate(string template)
    {
        var parsed = Template.Parse(template);
        return parsed.HasErrors ? string.Join("; ", parsed.Messages.Select(m => m.Message)) : null;
    }

    public async Task<RenderedEmail> RenderAsync(EmailKind kind, string recipientName, IDictionary<string, object?> model, string? preferencesUrl, EmailTemplate? overrideTemplate = null)
    {
        var template = overrideTemplate ?? await GetAsync(kind);
        var values = new Dictionary<string, object?>(model)
        {
            ["app_name"] = options.AppName,
            ["name"] = recipientName,
            ["help_name"] = options.HelpName,
            ["help_phone"] = options.HelpPhone,
        };

        var subject = Render(template.Subject, values, html: false).Replace('\n', ' ').Trim();
        var text = Render(template.TextBody, values, html: false).Trim() + TextFooter(preferencesUrl);
        var html = Layout(Render(template.HtmlBody, values, html: true), preferencesUrl);
        return new RenderedEmail(subject, text, html);
    }

    private static string Render(string source, IDictionary<string, object?> values, bool html)
    {
        var template = Template.Parse(source);
        if (template.HasErrors) throw new InvalidOperationException("Email template error: " + string.Join("; ", template.Messages));

        var globals = new ScriptObject();
        foreach (var (k, v) in values) globals[k] = html ? Encode(v) : v;
        globals.Import("button", new Func<string, string, string>(Button));
        var context = new TemplateContext { StrictVariables = false, EnableRelaxedMemberAccess = true };
        context.PushGlobal(globals);
        return template.Render(context);
    }

    /// <summary>HTML-encodes strings (recursively through lists and dictionaries) so templates can't inject markup.</summary>
    private static object? Encode(object? value) => value switch
    {
        null => null,
        string s => WebUtility.HtmlEncode(s),
        IDictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => Encode(kv.Value)),
        IEnumerable e and not string => e.Cast<object?>().Select(Encode).ToList(),
        _ => value,
    };

    /// <summary>The single big button. Arguments are already HTML-encoded.</summary>
    private static string Button(string url, string label) =>
        $"""<p style="margin:0 0 24px"><a href="{url}" style="display:inline-block;background:#2f5d46;color:#ffffff;text-decoration:none;font-family:Arial,sans-serif;font-size:24px;font-weight:bold;padding:18px 32px;border-radius:10px">{label}</a></p>""";

    /// <summary>The same wording as the "Need help?" box on the site.</summary>
    private string HelpLine()
    {
        bool name = !string.IsNullOrWhiteSpace(options.HelpName), phone = !string.IsNullOrWhiteSpace(options.HelpPhone);
        return (name, phone) switch
        {
            (true, true) => $"Need help? Call {options.HelpName} at {options.HelpPhone}.",
            (false, true) => $"Need help? Call {options.HelpPhone}.",
            (true, false) => $"Need help? Ask {options.HelpName}.",
            _ => "",
        };
    }

    private string TextFooter(string? preferencesUrl)
    {
        var lines = new List<string>();
        if (HelpLine() is { Length: > 0 } help) lines.Add(help);
        if (preferencesUrl is not null) lines.Add($"Email settings or unsubscribe: {preferencesUrl}");
        return lines.Count == 0 ? "" : "\n\n--\n" + string.Join("\n", lines);
    }

    private string Layout(string content, string? preferencesUrl)
    {
        var help = HelpLine();
        var footer = (help.Length > 0 ? WebUtility.HtmlEncode(help) : "")
            + (preferencesUrl is null ? "" : (help.Length > 0 ? "<br>" : "") + $"""<a href="{WebUtility.HtmlEncode(preferencesUrl)}" style="color:#4a4238">Email settings or unsubscribe</a>""");
        return $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head>
            <body style="margin:0;padding:24px;background:#fffdf8;font-family:Georgia,'Times New Roman',serif;color:#1f1b16;font-size:20px;line-height:1.6">
            <div style="max-width:600px;margin:0 auto">
            <p style="font-family:Georgia,'Times New Roman',serif;font-weight:bold;font-size:22px;color:#2f5d46;margin:0 0 24px">{WebUtility.HtmlEncode(options.AppName)}</p>
            {content}
            {(footer.Length > 0 ? $"""<hr style="border:0;border-top:1px solid #d8d0c2;margin:32px 0 16px"><p style="font-size:16px;color:#4a4238;margin:0">{footer}</p>""" : "")}
            </div></body></html>
            """;
    }
}
