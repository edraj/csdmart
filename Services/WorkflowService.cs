using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using System.Text.Json;

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
    WorkflowEngine engine,
    ILogger<WorkflowService> log)
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

        if (attrs is not null && attrs.TryGetValue("comment", out var commentObj)
            && AttrHelper.ConvertToString(commentObj) is { Length: > 0 } commentText)
        {
            await CreateCommentAsync(ticket, commentText, transition.NewState!, actor, ct);
        }

        return Response.Ok(attributes: new()
        {
            ["state"] = transition.NewState!,
            ["is_open"] = transition.IsOpen ?? true,
        });
    }

    // Records the optional progress comment as a Comment attachment under the
    // ticket. BEST-EFFORT by design: the ticket transition has already been
    // persisted when this runs, so a comment failure must not fail the
    // request — the caller would see an error, retry, and hit
    // INVALID_TICKET_STATUS on the already-transitioned ticket. Failures
    // (including a shortname collision, TryInsertAsync => false) are logged
    // and swallowed.
    //
    // NOTE: this write bypasses /managed/request, so attachment-create plugin
    // events do NOT fire for progress comments — only the progress_ticket
    // update event fires (via EntryService.UpdateAsync above). Plugins
    // listening for Comment creates will not observe these rows.
    private async Task CreateCommentAsync(Locator ticket, string body, string newState, string? actor, CancellationToken ct)
    {
        // attachments.owner_shortname is NOT NULL and an FK to users. The
        // /managed group requires auth, so actor is always present on the
        // HTTP path — but this service accepts a nullable actor; skip the
        // comment rather than trip the FK after a successful transition.
        if (string.IsNullOrEmpty(actor))
        {
            log.LogWarning(
                "progress comment on {Space}{Subpath}/{Shortname} skipped: no actor to attribute it to",
                ticket.SpaceName, ticket.Subpath, ticket.Shortname);
            return;
        }

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
            OwnerShortname = actor,
            IsActive = true,
            // Both comment shapes: payload.body JSON (the modern form) AND
            // the flat body/state columns that legacy Comment consumers read
            // (see RequestHandler.CreateAttachmentAsync's "legacy Comment
            // rows" note) — same values, so either reader works.
            Body = body,
            State = newState,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object> { ["body"] = body, ["state"] = newState },
                    DmartJsonContext.Default.DictionaryStringObject),
            },
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            if (!await attachments.TryInsertAsync(attachment, ct))
                log.LogWarning(
                    "progress comment on {Space}{Subpath}/{Shortname} not written: shortname {CommentShortname} already exists",
                    ticket.SpaceName, ticket.Subpath, ticket.Shortname, shortname);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex,
                "progress comment on {Space}{Subpath}/{Shortname} failed; the transition itself already applied",
                ticket.SpaceName, ticket.Subpath, ticket.Shortname);
        }
    }

    private async Task<IReadOnlyCollection<string>> GetActorRolesAsync(string? actor, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(actor)) return Array.Empty<string>();
        var user = await users.GetByShortnameAsync(actor, ct);
        return user?.Roles ?? (IReadOnlyCollection<string>)Array.Empty<string>();
    }
}
