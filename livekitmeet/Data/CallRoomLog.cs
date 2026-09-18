namespace livekitmeet.Data;

public sealed class CallRoomLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RoomName { get; set; } = string.Empty;
    public string RoomUrl { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public ICollection<CallParticipantSession> Participants { get; set; } = new List<CallParticipantSession>();
}

public sealed class CallParticipantSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CallRoomLogId { get; set; }
    public Guid UserId { get; set; }
    public Guid? InvitationId { get; set; }
    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LeftAtUtc { get; set; }
    public int? DurationSeconds { get; set; }
    public CallRoomLog CallRoomLog { get; set; } = null!;
    public AppUser User { get; set; } = null!;
}