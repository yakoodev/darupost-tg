using TgAutoposter.Domain.Common;

namespace TgAutoposter.Api.Contracts;

public sealed record LoginRequest(string Email, string Password);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, CurrentUserResponse User);

public sealed record ChannelRoleResponse(Guid ChannelId, string ChannelName, ChannelRoleType Role);

public sealed record CurrentUserResponse(
    Guid Id,
    string DisplayName,
    string? Email,
    bool IsGlobalOwner,
    IReadOnlyList<ChannelRoleResponse> Roles);

public sealed record UserListItemResponse(
    Guid Id,
    string DisplayName,
    string? Email,
    string? TelegramUsername,
    bool IsEnabled,
    bool IsGlobalOwner,
    bool HasPassword,
    IReadOnlyList<ChannelRoleResponse> Roles);

public sealed record CreateUserRequest(
    string DisplayName,
    string? Email,
    string? Password,
    string? TelegramUsername,
    bool IsGlobalOwner);

public sealed record UpdateUserRequest(
    string DisplayName,
    string? Email,
    string? TelegramUsername,
    bool IsEnabled,
    bool IsGlobalOwner,
    string? NewPassword);

public sealed record AssignRoleRequest(Guid ChannelId, ChannelRoleType Role);
