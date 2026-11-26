namespace FamilyMemories.Data;

public enum EmailKind
{
    LoginLink = 1,
    Invitation = 2,
    Reminder = 3,
    FollowUp = 4,
    ReadSummary = 5,
    Digest = 6,
    ExportReady = 7,
    AdminAlert = 8,
    Test = 9,
    AdminNudge = 10,
}

public enum EmailStatus { Queued = 0, Sent = 1, Failed = 2 }

/// <summary>Every email the app sends (§8), with its outcome. Failures are retried with back-off.</summary>
public sealed class EmailLog
{
    public Guid Id { get; set; }
    public EmailKind Kind { get; set; }
    public Guid? UserId { get; set; }
    public string ToAddress { get; set; } = "";
    public string ToName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string TextBody { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    /// <summary>For one-click unsubscribe (RFC 8058) on digest-style mail.</summary>
    public string? UnsubscribeUrl { get; set; }
    /// <summary>Answer-by-email (Phase 6): replies go here.</summary>
    public string? ReplyTo { get; set; }
    public EmailStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

public enum ReminderFrequency { Weekly = 1, EveryTwoWeeks = 2, Monthly = 3, Off = 4 }

/// <summary>A person's reminder choices (null = use the family-wide default) and bookkeeping.</summary>
public sealed class ReminderSettings
{
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public ReminderFrequency? Frequency { get; set; }
    public DayOfWeek? Day { get; set; }
    public TimeOnly? Time { get; set; }
    public string? TimeZoneId { get; set; }
    /// <summary>"Remind me in 2 weeks".</summary>
    public DateTime? SnoozedUntil { get; set; }
    /// <summary>The scheduled moment last handled (sent or deliberately skipped).</summary>
    public DateTime? LastScheduledAt { get; set; }
    public DateTime? LastReminderSentAt { get; set; }
    /// <summary>Reminders sent since the person last wrote anything (for the nudge to admins).</summary>
    public int RemindersSinceActivity { get; set; }
    public DateTime? NudgeSentAt { get; set; }
    public bool DigestOptOut { get; set; }
    public bool ReadNotificationsOptOut { get; set; }
    public DateTime? LastDigestAt { get; set; }
}

/// <summary>An admin's edited version of a built-in email template.</summary>
public sealed class EmailTemplateOverride
{
    public EmailKind Kind { get; set; }
    public string Subject { get; set; } = "";
    public string TextBody { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
