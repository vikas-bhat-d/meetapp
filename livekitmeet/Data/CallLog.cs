namespace livekitmeet.Data;

public static class CallLogStatuses
{
    public const string Ringing = "Ringing";
    public const string Answered = "Answered";
    public const string Ended = "Ended";
    public const string Declined = "Declined";
    public const string Cancelled = "Cancelled";
    public const string Failed = "Failed";
}

public sealed class CallLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvitationId { get; set; }
    public Guid CallerId { get; set; }
    public Guid RecipientId { get; set; }
    public AppUser Caller { get; set; } = null!;
    public AppUser Recipient { get; set; } = null!;
    public string RoomName { get; set; } = string.Empty;
    public string RoomUrl { get; set; } = string.Empty;
    public string Status { get; set; } = CallLogStatuses.Ringing;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AnsweredAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public int? DurationSeconds { get; set; }
}