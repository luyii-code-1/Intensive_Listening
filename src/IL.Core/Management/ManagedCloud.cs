namespace IL.Core.Management;

public sealed record ManagedCloudPolicy
{
    public string Mode { get; init; } = "followLocal";
    public bool RequireLogin { get; init; }
    public bool LockAccount { get; init; }
}
public sealed record ManagedCloudCommand(string Id, string Type, string TargetSessionId, double CreatedAt);
public sealed record ManagedCloudAccount(string Id, string Email, string DisplayName);
public sealed record ManagedCloudSnapshot(long PolicyRevision, bool Enabled, string Authentication, string Connection,
    ManagedCloudAccount? Account, string? SessionId, string? ErrorCode);
public sealed record ManagedCloudReceipt(string Status, string Revocation, string? ErrorCode = null);
