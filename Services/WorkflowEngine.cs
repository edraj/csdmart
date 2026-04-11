using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.Services;

// Loads workflow definitions from the entries table (typically under /workflows
// subpath, resource_type=ticket... actually dmart stores workflows as `content`
// resources with a known schema). The workflow's payload.body holds:
//   {
//     "initial_state": "draft",
//     "states": [
//       { "state": "draft",     "next": [{ "action": "submit", "to": "submitted", "roles": ["editor"] }] },
//       { "state": "submitted", "next": [{ "action": "approve", "to": "approved" }, { "action": "reject", "to": "rejected", "resolution_required": true }] },
//       ...
//     ],
//     "closed_states": ["approved", "rejected", "cancelled"]
//   }
public sealed class WorkflowEngine(EntryRepository entries, ILogger<WorkflowEngine> log)
{
    public sealed record TransitionResult(
        bool Allowed,
        string? Error,
        string? NewState,
        bool? IsOpen,
        bool ResolutionRequired);

    public async Task<TransitionResult> EvaluateAsync(
        string spaceName, string workflowShortname, string currentState, string action,
        IReadOnlyCollection<string> actorRoles, CancellationToken ct = default)
    {
        var workflow = await LoadWorkflowAsync(spaceName, workflowShortname, ct);
        if (workflow is null)
            return new TransitionResult(false, $"workflow {workflowShortname} not found", null, null, false);

        if (!workflow.RootElement.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Array)
            return new TransitionResult(false, "workflow has no states", null, null, false);

        // Find the current state's transitions
        JsonElement? matchedState = null;
        foreach (var s in states.EnumerateArray())
        {
            if (s.TryGetProperty("state", out var name) && name.GetString() == currentState)
            {
                matchedState = s;
                break;
            }
        }
        if (matchedState is null)
            return new TransitionResult(false, $"current state '{currentState}' not in workflow", null, null, false);

        if (!matchedState.Value.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.Array)
            return new TransitionResult(false, $"state '{currentState}' has no transitions (terminal)", null, null, false);

        foreach (var t in next.EnumerateArray())
        {
            if (!t.TryGetProperty("action", out var act) || act.GetString() != action) continue;
            if (!t.TryGetProperty("to", out var to)) continue;

            // Optional role gate
            if (t.TryGetProperty("roles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
            {
                var allowedRoles = rolesEl.EnumerateArray().Select(r => r.GetString()).ToHashSet();
                if (!actorRoles.Any(r => allowedRoles.Contains(r)) && !actorRoles.Contains("super_admin"))
                    return new TransitionResult(false, $"action '{action}' requires one of: {string.Join(",", allowedRoles)}", null, null, false);
            }

            var newState = to.GetString()!;
            var resolutionRequired = t.TryGetProperty("resolution_required", out var rr) && rr.ValueKind == JsonValueKind.True;
            var isOpen = !IsClosedState(workflow.RootElement, newState);
            return new TransitionResult(true, null, newState, isOpen, resolutionRequired);
        }

        return new TransitionResult(false, $"action '{action}' not allowed from state '{currentState}'", null, null, false);
    }

    private static bool IsClosedState(JsonElement root, string state)
    {
        if (!root.TryGetProperty("closed_states", out var closed) || closed.ValueKind != JsonValueKind.Array) return false;
        foreach (var s in closed.EnumerateArray())
            if (s.ValueKind == JsonValueKind.String && s.GetString() == state) return true;
        return false;
    }

    private async Task<JsonDocument?> LoadWorkflowAsync(string spaceName, string shortname, CancellationToken ct)
    {
        // dmart stores workflows as content entries (or 'workflow' subpath). Try a couple
        // of subpaths since project layouts vary.
        Entry? wf = null;
        foreach (var sub in new[] { "/workflows", "/workflow", "/" })
        {
            wf = await entries.GetAsync(spaceName, sub, shortname, ResourceType.Content, ct);
            if (wf is not null) break;
        }
        if (wf?.Payload?.Body is null)
        {
            log.LogDebug("workflow {Space}/{Shortname} not found", spaceName, shortname);
            return null;
        }

        try
        {
            var json = JsonSerializer.Serialize(wf.Payload.Body!.Value, DmartJsonContext.Default.JsonElement);
            return JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "failed to parse workflow {Space}/{Shortname}", spaceName, shortname);
            return null;
        }
    }
}
