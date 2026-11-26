using System.Net;

namespace FamilyMemories.Configuration;

/// <summary>
/// All runtime settings, read from flat UPPER_SNAKE environment variables (see .env.example).
/// Read once at startup; registered as a singleton.
/// </summary>
public sealed class AppOptions
{
    public string AppName { get; init; } = "Family Memories";

    /// <summary>Absolute public URL (no trailing slash). Used for every link that leaves the app (emails, QR codes).</summary>
    public Uri PublicBaseUrl { get; init; } = new("http://localhost:8095");

    /// <summary>Root for media, keys, logs, exports, config. "/data" in the container.</summary>
    public string DataDir { get; init; } = "/data";

    public string ConnectionString { get; init; } = "";

    /// <summary>Reverse proxies whose X-Forwarded-* headers we trust (IPs or CIDR ranges).</summary>
    public IReadOnlyList<IPNetwork> TrustedProxies { get; init; } = [];

    public string? GotenbergUrl { get; init; }

    public string? AdminEmail { get; init; }
    public string AdminName { get; init; } = "Family Admin";

    public string HelpName { get; init; } = "";
    public string HelpPhone { get; init; } = "";

    public SmtpOptions Smtp { get; init; } = new();

    public TimeSpan MagicLinkLifetime { get; init; } = TimeSpan.FromDays(14);
    public TimeSpan RememberedDeviceLifetime { get; init; } = TimeSpan.FromDays(180);

    /// <summary>Take a pg_dump before applying pending EF migrations to a non-empty database.</summary>
    public bool PreMigrationBackup { get; init; } = true;

    /// <summary>Sign-in form posts allowed per client IP per 10 minutes.</summary>
    public int AuthRateLimit { get; init; } = 10;

    /// <summary>Visibility for new memories (DEFAULT_VISIBILITY: Private | AllFamily). Default All family.</summary>
    public FamilyMemories.Data.Visibility DefaultVisibility { get; init; } = FamilyMemories.Data.Visibility.AllFamily;

    /// <summary>Upload limits in bytes (MAX_IMAGE_MB, MAX_AUDIO_MB, MAX_VIDEO_MB). Images and PDFs share the image limit.</summary>
    public long MaxImageBytes { get; init; } = 25L * 1024 * 1024;
    public long MaxAudioBytes { get; init; } = 200L * 1024 * 1024;
    public long MaxVideoBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>How long a printed QR login card works (LOGIN_CARD_DAYS, default 30).</summary>
    public TimeSpan LoginCardLifetime { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Run Hangfire (JOBS_ENABLED, default true). Tests turn it off and run jobs inline.</summary>
    public bool JobsEnabled { get; init; } = true;

    public string FfmpegPath { get; init; } = "ffmpeg";
    public string FfprobePath { get; init; } = "ffprobe";

    // ---- Backups (Phase 2) ----
    /// <summary>Primary restic repository (RESTIC_REPOSITORY). Put /backups on a different disk from /data.</summary>
    public string ResticRepository { get; init; } = "/backups/restic";
    /// <summary>RESTIC_PASSWORD. Backups are off until it is set.</summary>
    public string? ResticPassword { get; init; }
    /// <summary>Optional second repository (any restic backend), e.g. "b2:bucket:family-memories".</summary>
    public string? OffsiteRepository { get; init; }
    public string? OffsitePassword { get; init; }
    /// <summary>Local time of the nightly backup, "HH:mm" (BACKUP_TIME, default 02:30).</summary>
    public string BackupTime { get; init; } = "02:30";
    public int KeepDaily { get; init; } = 7;
    public int KeepWeekly { get; init; } = 4;
    public int KeepMonthly { get; init; } = 12;
    /// <summary>Folder holding pg_dump/pg_restore/psql; empty = use PATH (PG_BIN_DIR).</summary>
    public string PgBinDir { get; init; } = "";
    public string ResticPath { get; init; } = "restic";
    /// <summary>The family's time zone for schedules and emails (APP_TIMEZONE).</summary>
    public string TimeZoneId { get; init; } = "America/New_York";
    public bool BackupsConfigured => !string.IsNullOrWhiteSpace(ResticPassword);

    // ---- Admin alerts ----
    public string? NtfyUrl { get; init; }
    public string? NtfyTopic { get; init; }
    public string? NtfyToken { get; init; }

    // ---- Optional extras (Phase 6): transcription and paper packets on by default (FEATURE_…=false turns one off); email replies off unless switched on ----
    /// <summary>FEATURE_TRANSCRIPTION (default on): write down voice recordings with a Whisper server at WHISPER_URL.</summary>
    public bool TranscriptionSwitchedOn { get; init; } = true;
    public string? WhisperUrl { get; init; }
    /// <summary>WHISPER_MODEL: the model name sent to an OpenAI-compatible server (WHISPER_URL ending in /v1).</summary>
    public string WhisperModel { get; init; } = "whisper-1";
    public bool TranscriptionEnabled => TranscriptionSwitchedOn && WhisperUrl is not null;
    /// <summary>FEATURE_EMAIL_REPLIES (default off): replies to reminder emails become drafts, once REPLY_ADDRESS is set.</summary>
    public bool EmailRepliesSwitchedOn { get; init; }
    public bool EmailRepliesEnabled => EmailRepliesSwitchedOn && ReplyAddress is { } a && a.Contains('@');
    /// <summary>Address replies go to, e.g. memories@example.com; replies use memories+CODE@example.com.</summary>
    public string? ReplyAddress { get; init; }
    public string ImapHost { get; init; } = "";
    public int ImapPort { get; init; } = 993;
    public bool ImapSsl { get; init; } = true;
    public string? ImapUsername { get; init; }
    public string? ImapPassword { get; init; }
    public string ImapFolder { get; init; } = "INBOX";
    /// <summary>DEMO_MODE: sample family plus one-tap sign-in as any role. For beta testing only, never with real stories.</summary>
    public bool DemoMode { get; init; }

    /// <summary>STATUS_TOKEN: lets a monitor (Uptime Kuma, Healthchecks…) read the detailed /api/status with "Authorization: Bearer …".</summary>
    public string? StatusToken { get; init; }
    /// <summary>PROJECT_URL: where the landing page links to for the project's code ("" hides the link).</summary>
    public string? ProjectUrl { get; init; } = DefaultProjectUrl;
    public const string DefaultProjectUrl = "https://github.com/jmconlon2001/heirloom";

    /// <summary>The app's version, baked in at build time (APP_VERSION build arg on release images; "dev" otherwise).</summary>
    public static string Version { get; } = ReadVersion();
    private static string ReadVersion()
    {
        var v = typeof(AppOptions).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "dev";
        var plus = v.IndexOf('+');                 // drop the "+commit" suffix the SDK adds
        return plus > 0 ? v[..plus] : v;
    }

    /// <summary>FEATURE_PAPER_PACKETS (default on): printable question packets with writing lines (needs Gotenberg).</summary>
    public bool PaperPacketsSwitchedOn { get; init; } = true;
    public bool PaperPacketsEnabled => PaperPacketsSwitchedOn && GotenbergUrl is not null;

    public TimeZoneInfo TimeZone => TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var tz) ? tz : TimeZoneInfo.Utc;
    public string PgTool(string name) => string.IsNullOrEmpty(PgBinDir) ? name : Path.Combine(PgBinDir, name);

    public string KeysDir => Path.Combine(DataDir, "keys");
    public string ConfigDir => Path.Combine(DataDir, "config");
    public string BackupStagingDir => Path.Combine(DataDir, "backup-staging");
    public string MediaDir => Path.Combine(DataDir, "media");
    public string UploadsDir => Path.Combine(DataDir, "uploads");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string PreMigrationBackupDir => Path.Combine(DataDir, "backups", "pre-migration");

    public Uri BuildUrl(string relativePathAndQuery) =>
        new(PublicBaseUrl, relativePathAndQuery.TrimStart('/'));

    public static AppOptions FromConfiguration(IConfiguration c)
    {
        var baseUrl = (c["PUBLIC_BASE_URL"] ?? "http://localhost:8095").TrimEnd('/') + "/";

        return new AppOptions
        {
            AppName = NonEmpty(c["APP_NAME"]) ?? "Family Memories",
            PublicBaseUrl = new Uri(baseUrl, UriKind.Absolute),
            DataDir = NonEmpty(c["DATA_DIR"]) ?? "/data",
            ConnectionString = NonEmpty(c.GetConnectionString("Default")) ?? BuildConnectionString(c),
            TrustedProxies = ParseNetworks(c["TRUSTED_PROXIES"]),
            GotenbergUrl = NonEmpty(c["GOTENBERG_URL"]),
            AdminEmail = NonEmpty(c["ADMIN_EMAIL"]),
            AdminName = NonEmpty(c["ADMIN_NAME"]) ?? "Family Admin",
            HelpName = c["HELP_NAME"] ?? "",
            HelpPhone = c["HELP_PHONE"] ?? "",
            PreMigrationBackup = !string.Equals(c["PRE_MIGRATION_BACKUP"], "false", StringComparison.OrdinalIgnoreCase),
            DefaultVisibility = string.Equals(c["DEFAULT_VISIBILITY"], "Private", StringComparison.OrdinalIgnoreCase)
                ? FamilyMemories.Data.Visibility.Private : FamilyMemories.Data.Visibility.AllFamily,
            MaxImageBytes = Megabytes(c["MAX_IMAGE_MB"], 25),
            MaxAudioBytes = Megabytes(c["MAX_AUDIO_MB"], 200),
            MaxVideoBytes = Megabytes(c["MAX_VIDEO_MB"], 1024),
            LoginCardLifetime = TimeSpan.FromDays(int.TryParse(c["LOGIN_CARD_DAYS"], out var cardDays) && cardDays > 0 ? cardDays : 30),
            ResticRepository = NonEmpty(c["RESTIC_REPOSITORY"]) ?? "/backups/restic",
            ResticPassword = NonEmpty(c["RESTIC_PASSWORD"]),
            OffsiteRepository = NonEmpty(c["OFFSITE_RESTIC_REPOSITORY"]),
            OffsitePassword = NonEmpty(c["OFFSITE_RESTIC_PASSWORD"]) ?? NonEmpty(c["RESTIC_PASSWORD"]),
            BackupTime = NonEmpty(c["BACKUP_TIME"]) ?? "02:30",
            KeepDaily = int.TryParse(c["BACKUP_KEEP_DAILY"], out var kd) ? kd : 7,
            KeepWeekly = int.TryParse(c["BACKUP_KEEP_WEEKLY"], out var kw) ? kw : 4,
            KeepMonthly = int.TryParse(c["BACKUP_KEEP_MONTHLY"], out var km) ? km : 12,
            PgBinDir = c["PG_BIN_DIR"] ?? "",
            ResticPath = NonEmpty(c["RESTIC_PATH"]) ?? "restic",
            TimeZoneId = NonEmpty(c["APP_TIMEZONE"]) ?? NonEmpty(c["TZ"]) ?? "America/New_York",
            TranscriptionSwitchedOn = FlagDefaultOn(c["FEATURE_TRANSCRIPTION"]),
            WhisperUrl = NonEmpty(c["WHISPER_URL"]),
            WhisperModel = NonEmpty(c["WHISPER_MODEL"]) ?? "whisper-1",
            EmailRepliesSwitchedOn = Flag(c["FEATURE_EMAIL_REPLIES"]),
            ReplyAddress = NonEmpty(c["REPLY_ADDRESS"]),
            ImapHost = c["IMAP_HOST"] ?? "",
            ImapPort = int.TryParse(c["IMAP_PORT"], out var imapPort) ? imapPort : 993,
            ImapSsl = !string.Equals(c["IMAP_SSL"], "false", StringComparison.OrdinalIgnoreCase),
            ImapUsername = NonEmpty(c["IMAP_USERNAME"]),
            ImapPassword = NonEmpty(c["IMAP_PASSWORD"]),
            ImapFolder = NonEmpty(c["IMAP_FOLDER"]) ?? "INBOX",
            PaperPacketsSwitchedOn = FlagDefaultOn(c["FEATURE_PAPER_PACKETS"]),
            DemoMode = Flag(c["DEMO_MODE"]),
            StatusToken = NonEmpty(c["STATUS_TOKEN"]),
            ProjectUrl = c["PROJECT_URL"] is { } projectUrl ? NonEmpty(projectUrl) : DefaultProjectUrl,
            NtfyUrl = NonEmpty(c["NTFY_URL"]),
            NtfyTopic = NonEmpty(c["NTFY_TOPIC"]),
            NtfyToken = NonEmpty(c["NTFY_TOKEN"]),
            JobsEnabled = !string.Equals(c["JOBS_ENABLED"], "false", StringComparison.OrdinalIgnoreCase),
            FfmpegPath = NonEmpty(c["FFMPEG_PATH"]) ?? "ffmpeg",
            FfprobePath = NonEmpty(c["FFPROBE_PATH"]) ?? "ffprobe",
            AuthRateLimit = int.TryParse(c["AUTH_RATE_LIMIT"], out var limit) && limit > 0 ? limit : 10,
            Smtp = new SmtpOptions
            {
                Host = c["SMTP_HOST"] ?? "",
                Port = int.TryParse(c["SMTP_PORT"], out var port) ? port : 587,
                Security = Enum.TryParse<SmtpSecurity>(c["SMTP_SECURITY"], ignoreCase: true, out var sec) ? sec : SmtpSecurity.StartTls,
                Username = NonEmpty(c["SMTP_USERNAME"]),
                Password = NonEmpty(c["SMTP_PASSWORD"]),
                FromAddress = NonEmpty(c["SMTP_FROM_ADDRESS"]) ?? "memories@localhost",
                FromName = NonEmpty(c["SMTP_FROM_NAME"]) ?? NonEmpty(c["APP_NAME"]) ?? "Family Memories",
            },
        };
    }

    private static string BuildConnectionString(IConfiguration c) =>
        $"Host={c["POSTGRES_HOST"] ?? "db"};Port={c["POSTGRES_PORT"] ?? "5432"};" +
        $"Database={c["POSTGRES_DB"] ?? "family_memories"};Username={c["POSTGRES_USER"] ?? "family_memories"};" +
        $"Password={c["POSTGRES_PASSWORD"] ?? ""}";

    internal static IReadOnlyList<IPNetwork> ParseNetworks(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<IPNetwork>();
        foreach (var raw in value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Contains('/'))
            {
                result.Add(IPNetwork.Parse(raw));
            }
            else
            {
                var ip = IPAddress.Parse(raw);
                result.Add(new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
        }
        return result;
    }

    private static bool FlagDefaultOn(string? value) =>
        string.IsNullOrWhiteSpace(value) || !(value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) || value.Trim() == "0" || value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase));
    private static bool Flag(string? value) => value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    private static long Megabytes(string? value, long fallback) =>
        (long.TryParse(value, out var mb) && mb > 0 ? mb : fallback) * 1024 * 1024;

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public enum SmtpSecurity { None, StartTls, SslOnConnect }

public sealed class SmtpOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string FromAddress { get; init; } = "memories@localhost";
    public string FromName { get; init; } = "Family Memories";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
