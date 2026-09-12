namespace LiveKitMeet.Tray;

public sealed record AuthTokenResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

public sealed record CallInvitationMessage(
    Guid InvitationId,
    string RoomName,
    string RoomUrl,
    string FromUserName,
    string FromDisplayName,
    bool AudioEnabled,
    bool VideoEnabled,
    DateTime ExpiresAtUtc);
