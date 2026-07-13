using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dmart.Services;

// dmart-style workflow: validates the action against the ticket's workflow definition
// before applying the state change. Mirrors the Python serve_request_progress flow:
//   1. Load the ticket entry
//   2. Look up the actor's roles (for role-gated transitions)
//   3. Ask WorkflowEngine whether currentState + action is allowed
//   4. Apply the patch (state, is_open, optional resolution_reason)
//   5. Optionally record a comment attachment on the ticket
public sealed class WorkflowService(
    EntryRepository entries,
    UserRepository users,
    EntryService entryService,
    AttachmentRepository attachments,
    WorkflowEngine engine)
{
    public async Task<Response> ProgressAsync(
        Locator ticket, string action, string? actor,
        Dictionary<string, object>? attrs, CancellationToken ct = default)
    {
        var existing = await entries.GetAsync(ticket.SpaceName, ticket.Subpath, ticket.Shortname, ticket.Type, ct);
        if (existing is null)
            return Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "ticket not found", ErrorTypes.Request);

        if (string.IsNullOrEmpty(existing.WorkflowShortname))
            return Response.Fail(InternalErrorCode.WORKFLOW_BODY_NOT_FOUND,
                "ticket has no workflow_shortname", ErrorTypes.Request);

        var currentState = existing.State ?? "";
        var actorRoles = await GetActorRolesAsync(actor, ct);

        var transition = await engine.EvaluateAsync(
            existing.SpaceName, existing.WorkflowShortname, currentState, action, actorRoles, ct);

        if (!transition.Allowed)
            return Response.Fail(InternalErrorCode.INVALID_TICKET_STATUS,
                transition.Error ?? "transition not allowed", ErrorTypes.Request);

        // Build the patch dmart applies on a successful transition
        var patch = new Dictionary<string, object>
        {
            ["state"] = transition.NewState!,
        };
        if (transition.IsOpen.HasValue) patch["is_open"] = transition.IsOpen.Value;

        // resolution_reason is required for transitions that mark the ticket closed
        if (transition.ResolutionRequired)
        {
            if (attrs is null || !attrs.TryGetValue("resolution_reason", out var reasonObj) || reasonObj is null)
                return Response.Fail(InternalErrorCode.MISSING_DATA,
                    "this transition requires a resolution_reason", ErrorTypes.Request);
            patch["resolution_reason"] = reasonObj;
        }
        else if (attrs is not null && attrs.TryGetValue("resolution_reason", out var reasonObj))
        {
            // Caller provided one even though it's optional — accept it.
            if (reasonObj is not null) patch["resolution_reason"] = reasonObj;
        }

        var result = await entryService.UpdateAsync(ticket, patch, actor, ct, actionOverride: "progress_ticket");
        if (!result.IsOk)
            return Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? "request");

        if (attrs is not null && attrs.TryGetValue("comment", out var commentObj))
        {
            var commentText = ConvertToString(commentObj);
            if (!string.IsNullOrEmpty(commentText))
                await CreateCommentAsync(ticket, commentText, transition.NewState!, actor, ct);
        }

        return Response.Ok(attributes: new()
        {
            ["state"] = transition.NewState!,
            ["is_open"] = transition.IsOpen ?? true,
        });
    }
    private async Task CreateCommentAsync(Locator ticket, string body, string newState, string? actor, CancellationToken ct)
    {
        var now = TimeUtils.Now();
        var uuid = Guid.NewGuid();
        var subpath = ticket.Subpath.TrimEnd('/') + "/" + ticket.Shortname;
        var shortname = uuid.ToString("N")[..8];

        var attachment = new Attachment
        {
            Uuid = uuid.ToString(),
            Shortname = shortname,
            SpaceName = ticket.SpaceName,
            Subpath = subpath,
            ResourceType = ResourceType.Comment,
            OwnerShortname = actor ?? "",
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(
                    new JsonObject { ["body"] = body, ["state"] = newState }.ToJsonString()).RootElement,
            },
            CreatedAt = now,
            UpdatedAt = now,
        };
        await attachments.TryInsertAsync(attachment, ct);
    }

    private static string? ConvertToString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null => null,
            _ => el.GetRawText(),
        },
        _ => v.ToString(),
    };

    private async Task<IReadOnlyCollection<string>> GetActorRolesAsync(string? actor, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(actor)) return Array.Empty<string>();
        var user = await users.GetByShortnameAsync(actor, ct);
        return user?.Roles ?? (IReadOnlyCollection<string>)Array.Empty<string>();
    }
}
