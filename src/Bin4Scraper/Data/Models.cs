namespace Bin4Scraper.Data;

public static class CheckStatus
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class DeliveryStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";

    /// <summary>The provider may or may not have sent the message; never retried automatically.</summary>
    public const string Unknown = "unknown";
}

public static class DeliveryKind
{
    public const string Feature = "feature";
    public const string Alert = "alert";
    public const string Test = "test";
}

public sealed class CheckRun
{
    public long Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string TriggeredBy { get; set; } = "";
    public string Status { get; set; } = CheckStatus.Running;
    public string? Title { get; set; }
    public string? Fingerprint { get; set; }
    public string? ItemsJson { get; set; }
    public string? Message { get; set; }
    public bool Changed { get; set; }
    public string? Error { get; set; }
    public string? SnapshotFile { get; set; }
}

public sealed class Recipient
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Delivery
{
    public long Id { get; set; }
    public long? CheckId { get; set; }
    public long? RecipientId { get; set; }
    public string Phone { get; set; } = "";
    public string Kind { get; set; } = DeliveryKind.Feature;
    public string Message { get; set; } = "";
    public string Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public string? Provider { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
}
