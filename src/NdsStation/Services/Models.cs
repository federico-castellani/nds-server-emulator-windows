namespace NdsStation.Services;

public sealed record GameInfo(string Id, string Name, string? Url);

public sealed record AdminUser(int Id, string Username, bool IsAdmin, bool MustChangePassword);

public sealed record SearchUser(int Id, string Username);

/// <summary>A player's save as the admin sees it.</summary>
public sealed record AdminSave(string GameId, string Title, long Size, DateTimeOffset? UpdatedAt);

public sealed record LoginResult(string Token, bool MustChangePassword, bool IsAdmin);

public sealed class ApiException : Exception
{
    public ApiException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }

    public bool IsUnauthorized => StatusCode is 401 or 403;

    /// <summary>The server holds a newer version than the one this upload was based on.</summary>
    public bool IsConflict => StatusCode == 409;
}
