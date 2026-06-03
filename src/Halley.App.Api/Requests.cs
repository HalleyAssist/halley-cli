namespace Halley.App.Api;

public sealed record UserLoginRequest(string Username, string Password);

public sealed record ApiKeyLoginRequest(string Secret);

public sealed class CreateApiKeyRequest
{
    public required ApiKeyWriteModel ApiKey { get; init; }
}

public sealed class ApiKeyWriteModel
{
    public required int OrganisationId { get; init; }

    public required IReadOnlyList<string> Permissions { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class OrganisationWriteRequest
{
    public required OrganisationWriteModel Organisation { get; init; }

    public string? AdminUserName { get; init; }

    public bool? CreateDefaultResources { get; init; }

    public AlertNotificationTemplateModel? AlertNotificationTemplate { get; init; }

    public bool? PersonalOrganisation { get; init; }
}

public sealed class OrganisationWriteModel
{
    public required string Name { get; init; }

    public IReadOnlyList<string>? Facilities { get; init; }

    public string? NotificationTemplate { get; init; }

    public IReadOnlyList<string>? AuthIps { get; init; }
}

public sealed class AlertNotificationTemplateModel
{
    public required string Title { get; init; }
}

public sealed class UserWriteRequest
{
    public required UserWriteModel User { get; init; }
}

public sealed class UserWriteModel
{
    public required string Name { get; init; }

    public required string Password { get; init; }

    public int? ContactId { get; init; }

    public string? Country { get; init; }

    public UserContactWriteModel? Contact { get; init; }
}

public sealed class UserContactWriteModel
{
    public required string Name { get; init; }

    public string Role { get; init; } = "user";

    public required IReadOnlyList<ContactDetailWriteModel> Details { get; init; }
}

public sealed class ContactDetailWriteModel
{
    public required string Type { get; init; }

    public required string Value { get; init; }

    public IReadOnlyList<ContactFilterWriteModel> Filter { get; init; } = [];
}

public sealed class ContactFilterWriteModel
{
    public required string Key { get; init; }

    public required string Value { get; init; }
}

public sealed class ListOrganisationsQuery
{
    public int? Offset { get; init; }

    public string? Order { get; init; }

    public int? Size { get; init; }

    public string? Name { get; init; }
}

public sealed class ListUsersQuery
{
    public int? Offset { get; init; }

    public string? Order { get; init; }

    public int? Size { get; init; }

    public int? OrganisationId { get; init; }
}
