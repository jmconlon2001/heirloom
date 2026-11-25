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

    public string KeysDir => Path.Combine(DataDir, "keys");
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
