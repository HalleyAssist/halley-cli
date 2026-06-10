namespace Halley.App.Api;

public interface IHalleyApiClient
{
    Task<ApiCallResult> LoginUserAsync(UserLoginRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> LoginApiKeyAsync(ApiKeyLoginRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> CreateCallRequestAsync(string token, CallRequestCreateRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> GetCallRequestAsync(string token, string callRequestUuid, CancellationToken cancellationToken = default);

    Task<ApiCallResult> ListCallResultsAsync(string token, ListCallResultsQuery query, CancellationToken cancellationToken = default);

    Task<ApiCallResult> ListCallResultsForRequestAsync(string token, string callRequestUuid, CancellationToken cancellationToken = default);

    Task<ApiCallResult> DeleteCallResultAsync(string token, string callResultUuid, CancellationToken cancellationToken = default);

    Task<ApiCallResult> ListCallTemplatesAsync(string token, ListCallTemplatesQuery query, CancellationToken cancellationToken = default);

    Task<ApiCallResult> GetCallTemplateAsync(string token, string templateReference, CancellationToken cancellationToken = default);

    Task<ApiCallResult> ListApiKeysAsync(string token, int? organisationId, CancellationToken cancellationToken = default);

    Task<ApiCallResult> GetApiKeyAsync(string token, string id, CancellationToken cancellationToken = default);

    Task<ApiCallResult> CreateApiKeyAsync(string token, CreateApiKeyRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> RevokeApiKeyAsync(string token, string id, CancellationToken cancellationToken = default);

    Task<ApiCallResult> ListOrganisationsAsync(string token, ListOrganisationsQuery query, CancellationToken cancellationToken = default);

    Task<ApiCallResult> GetOrganisationAsync(string token, int organisationId, CancellationToken cancellationToken = default);

    Task<ApiCallResult> CreateOrganisationAsync(string token, OrganisationWriteRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> PatchOrganisationAsync(string token, int organisationId, OrganisationWriteRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> PutOrganisationAsync(string token, int organisationId, OrganisationWriteRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> DeleteOrganisationAsync(string token, int organisationId, CancellationToken cancellationToken = default);

    Task<ApiCallResult> ListUsersAsync(string token, ListUsersQuery query, CancellationToken cancellationToken = default);

    Task<ApiCallResult> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default);

    Task<ApiCallResult> GetUserAsync(string token, string name, CancellationToken cancellationToken = default);

    Task<ApiCallResult> CreateUserAsync(string token, UserWriteRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> PatchUserAsync(string token, string name, UserWriteRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> PutUserAsync(string token, string name, UserWriteRequest request, CancellationToken cancellationToken = default);

    Task<ApiCallResult> DeleteUserAsync(string token, string name, CancellationToken cancellationToken = default);
}
