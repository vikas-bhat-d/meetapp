namespace livekitmeet.Services;

public static class CallInvitationPolicy
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
}