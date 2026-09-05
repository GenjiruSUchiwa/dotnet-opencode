namespace OpenCode.Client;

using System.Net.Http.Json;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public async Task<LocationResponse<IReadOnlyList<FormInfo>>> ListFormRequestsAsync(string? directory = null,
        string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/form/request" + Query(("location[directory]", directory), ("location[workspace]", workspace)),
            FormProtocolJsonContext.Default.LocationFormsResult, ct);
        if (result.Data.Any(form => form is null)) throw Malformed("form.request.list", "The form catalog contains a null form.");
        return result;
    }

    public async Task<ApiResult<IReadOnlyList<FormInfo>>> ListFormsAsync(string owner, string? directory = null,
        string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, FormPath(owner, null, null, directory, workspace), FormProtocolJsonContext.Default.FormsResult, ct);
        if (result.Data.Any(form => form is null || form.SessionId != owner))
            throw Malformed("session.form.list", "The form catalog contains a null form or a different owner.");
        return result;
    }

    public async Task<ApiResult<FormInfo>> CreateFormAsync(string owner, FormCreatePayload input, string? directory = null,
        string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = await RequestAsync(HttpMethod.Post, FormPath(owner, null, null, directory, workspace),
            FormProtocolJsonContext.Default.FormResult, ct, JsonContent.Create(input, FormProtocolJsonContext.Default.FormCreatePayload));
        if (result.Data.SessionId != owner || (input.Id is { } id && result.Data.Id != id))
            throw Malformed("session.form.create", "The form response does not match the submitted owner/identity.");
        return result;
    }

    public async Task<ApiResult<FormInfo>> GetFormAsync(string owner, FormId id, string? directory = null,
        string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, FormPath(owner, id, null, directory, workspace), FormProtocolJsonContext.Default.FormResult, ct);
        if (result.Data.SessionId != owner || result.Data.Id != id)
            throw Malformed("session.form.get", "The form response does not match the requested owner/identity.");
        return result;
    }

    public Task<ApiResult<FormState>> FormStateAsync(string owner, FormId id, string? directory = null,
        string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, FormPath(owner, id, "/state", directory, workspace), FormProtocolJsonContext.Default.StateResult, ct);

    public Task ReplyFormAsync(string owner, FormId id, FormAnswer answer, string? directory = null,
        string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return NoContentAsync(HttpMethod.Post, FormPath(owner, id, "/reply", directory, workspace), ct,
            JsonContent.Create(new FormReply(answer), FormProtocolJsonContext.Default.FormReply));
    }

    public Task CancelFormAsync(string owner, FormId id, string? directory = null,
        string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, FormPath(owner, id, "/cancel", directory, workspace), ct);

    private static string FormPath(string owner, FormId? id, string? operation, string? directory, string? workspace)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        if (id is { } formId) ArgumentException.ThrowIfNullOrEmpty(formId.Value);
        return "/api/session/" + Uri.EscapeDataString(owner) + "/form" + (id is { } value ? "/" + Uri.EscapeDataString(value.Value) : "")
            + operation + Query(("location[directory]", directory), ("location[workspace]", workspace));
    }
}
