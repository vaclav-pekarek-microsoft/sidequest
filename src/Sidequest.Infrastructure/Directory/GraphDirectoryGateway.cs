using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Shared;
using Sidequest.Domain.Rules;

namespace Sidequest.Infrastructure.Directory;

/// <summary>Real Graph user/group selection and complete nested-group expansion under an explicit workforce policy.</summary>
/// <remarks>Only security and Microsoft 365 groups are supported. Transitive user pages are completely enumerated
/// and deduplicated before returning; unsupported, permission-limited, malformed, or incomplete data fails explicitly.
/// Member userType is an additional guest-exclusion check, never the workforce eligibility proof.</remarks>
/// <param name="http">Host-owned dedicated HTTP client; automatic redirects should be disabled.</param>
/// <param name="tokens">Trusted same-tenant application-token acquisition.</param>
/// <param name="options">Explicit tenant-approved workforce extension and operational limits.</param>
/// <param name="clock">Clock used for Retry-After date calculations and cancellation-aware delays.</param>
public sealed class GraphDirectoryGateway(HttpClient http, IGraphAccessTokenProvider tokens,
    GraphDirectoryOptions options, TimeProvider clock) : IDirectoryGateway
{
    private string UserSelect => $"id,displayName,mail,userPrincipalName,userType,accountEnabled,{options.WorkforceExtension}";

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        options.Validate();
        query = InputRules.Text(query, "Search", 2, 100).Replace("'", "''", StringComparison.Ordinal);
        var path = $"users?$select={UserSelect}&$top=100&$filter=" +
            Uri.EscapeDataString($"startswith(displayName,'{query}') or startswith(userPrincipalName,'{query}')");
        var users = await ReadPagesAsync(path, false, cancellationToken).ConfigureAwait(false);
        return users.Select(ParseUser).Where(x => x.IsEligible).DistinctBy(x => x.ObjectId).Take(100).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default)
    {
        options.Validate();
        query = InputRules.Text(query, "Search", 2, 100).Replace("'", "''", StringComparison.Ordinal);
        var rows = await ReadPagesAsync("groups?$select=id,displayName,securityEnabled,groupTypes&$top=100&$filter=" +
            Uri.EscapeDataString($"startswith(displayName,'{query}')"), false, cancellationToken).ConfigureAwait(false);
        return rows.Where(SupportedGroup).Select(x => new DirectoryGroup(RequiredId(x),
            RequiredString(x, "displayName"))).DistinctBy(x => x.ObjectId).Take(100).ToArray();
    }

    /// <inheritdoc />
    public async Task<DirectoryUser> GetUserAsync(Guid objectId, CancellationToken cancellationToken = default)
    {
        options.Validate();
        ValidateId(objectId);
        using var json = await GetAsync(GraphUri($"users/{objectId:D}?$select={UserSelect}"), cancellationToken).ConfigureAwait(false);
        var user = ParseUser(json.RootElement);
        if (user.ObjectId != objectId)
            throw Failure("The directory returned an unexpected identity.");
        return user;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryUser>> ExpandGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        options.Validate();
        ValidateId(groupId);
        using (var group = await GetAsync(GraphUri(
            $"groups/{groupId:D}?$select=id,displayName,securityEnabled,groupTypes"), cancellationToken).ConfigureAwait(false))
        {
            if (RequiredId(group.RootElement) != groupId || !SupportedGroup(group.RootElement))
                throw new DomainException(ErrorCode.Validation, "Choose a supported security or Microsoft 365 group.", "Group");
        }
        var rows = await ReadPagesAsync(
            $"groups/{groupId:D}/transitiveMembers/microsoft.graph.user?$select={UserSelect}&$top=999&$count=true",
            true, cancellationToken).ConfigureAwait(false);
        return rows.Select(ParseUser).Where(x => x.IsEligible).DistinctBy(x => x.ObjectId).ToArray();
    }

    private async Task<IReadOnlyList<JsonElement>> ReadPagesAsync(string path, bool complete, CancellationToken cancellationToken)
    {
        var results = new List<JsonElement>();
        var next = GraphUri(path);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        for (var page = 0; ; page++)
        {
            if (page >= options.MaximumPages || !visited.Add(next.AbsoluteUri))
                throw Failure("Directory expansion pagination was incomplete or exceeded its configured limit.");
            using var document = await GetAsync(next, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("value", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw Failure("The directory returned an incomplete page.");
            foreach (var row in rows.EnumerateArray())
            {
                var id = RequiredId(row);
                if (ids.Add(id))
                    results.Add(row.Clone());
                if (ids.Count > options.MaximumRecipients)
                    throw new DomainException(ErrorCode.Validation, "Directory expansion exceeds the configured recipient limit.");
            }
            if (!complete || !document.RootElement.TryGetProperty("@odata.nextLink", out var link))
                return results;
            if (link.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var candidate) ||
                candidate.Scheme != Uri.UriSchemeHttps || candidate.Host != "graph.microsoft.com" ||
                !candidate.IsDefaultPort || candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0 ||
                candidate.AbsolutePath != next.AbsolutePath)
                throw Failure("Directory pagination returned an untrusted continuation.");
            next = candidate;
        }
    }

    private async Task<JsonDocument> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var delay = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date is { } date ? date - clock.GetUtcNow() : TimeSpan.FromSeconds(30));
                    if (delay < TimeSpan.Zero)
                        delay = TimeSpan.Zero;
                    // Do not shorten Graph's requested delay. The dispatcher renews leases while handlers wait.
                    await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
                    if (attempt >= options.MaximumThrottlingRetries)
                        throw Failure("The directory is throttling requests. Retry later.");
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw response.StatusCode == HttpStatusCode.NotFound
                        ? new DomainException(ErrorCode.Validation, "The selected directory object is unavailable.")
                        : Failure("Directory access failed. Check provider availability, approved permissions, and configuration.");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw Failure("The directory is unavailable. Existing individual memberships are unchanged.");
            }
            catch (JsonException)
            {
                throw Failure("The directory returned invalid data.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failure("The directory request timed out.");
            }
        }
    }

    private DirectoryUser ParseUser(JsonElement user)
    {
        var id = RequiredId(user);
        var displayName = RequiredString(user, "displayName");
        var userType = RequiredString(user, "userType");
        if (!user.TryGetProperty("accountEnabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Failure("Directory user details are incomplete; approved user-read permissions are required.");
        var approved = user.TryGetProperty(options.WorkforceExtension, out var eligibility) &&
            eligibility.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False &&
            string.Equals(eligibility.ValueKind == JsonValueKind.String ? eligibility.GetString() : eligibility.GetRawText(),
                options.WorkforceValue, StringComparison.Ordinal);
        var email = user.TryGetProperty("mail", out var mail) && mail.ValueKind == JsonValueKind.String
            ? mail.GetString() ?? "" : "";
        // A UPN is not necessarily a routable mailbox. Missing mail remains explicit downstream delivery failure.
        return new(options.TenantId, id, displayName, email,
            approved && enabled.GetBoolean() && userType == "Member");
    }

    private static bool SupportedGroup(JsonElement group)
    {
        if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("securityEnabled", out var security) ||
            security.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !group.TryGetProperty("groupTypes", out var types) || types.ValueKind != JsonValueKind.Array)
            throw Failure("Directory group details are incomplete.");
        return security.GetBoolean() || types.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "Unified");
    }

    private static Guid RequiredId(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(id.GetString(), out var value) || value == Guid.Empty)
            throw Failure("Directory identity data is invalid or incomplete.");
        return value;
    }

    private static string RequiredString(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw Failure("Directory identity data is incomplete; approved read permissions are required.");
        return value.GetString()!;
    }

    private static Uri GraphUri(string path) => new($"https://graph.microsoft.com/v1.0/{path}");

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "Choose a valid directory object.");
    }

    private static DomainException Failure(string message) => new(ErrorCode.DependencyUnavailable, message);
}
