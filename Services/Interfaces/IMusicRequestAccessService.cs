namespace ChizuChan.Services.Interfaces;

public enum MusicRequestOperation
{
    Search,
    Request
}

public enum MusicRequestAccessStatus
{
    Allowed,
    Unauthorized,
    RateLimited
}

public readonly record struct MusicRequestAccessResult(
    MusicRequestAccessStatus Status,
    TimeSpan RetryAfter)
{
    public static MusicRequestAccessResult Allowed() =>
        new(MusicRequestAccessStatus.Allowed, TimeSpan.Zero);

    public static MusicRequestAccessResult Unauthorized() =>
        new(MusicRequestAccessStatus.Unauthorized, TimeSpan.Zero);

    public static MusicRequestAccessResult RateLimited(TimeSpan retryAfter) =>
        new(MusicRequestAccessStatus.RateLimited, retryAfter);
}

public interface IMusicRequestAccessService
{
    MusicRequestAccessResult CheckAccess(ulong userId, MusicRequestOperation operation);
}