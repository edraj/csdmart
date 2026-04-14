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

    // Mirrors Python's transite() from ticket_sys_utils.py.
    // Workflow next-state entries use "state" (not "to") for the target state name.
    // Open/closed is determined by whether the target state has a "next" array
    // (Python's check_open_state), not by a separate "closed_states" list.
    public async Task<TransitionResult> EvaluateAsync(
        string spaceName, string workflowShortname, string currentState, string action,
        IReadOnlyCollection<string> actorRoles, CancellationToken ct = default)
    {
        var workflow = await LoadWorkflowAsync(spaceName, workflowShortname, ct);
        if (workflow is null)
            return new TransitionResult(false, $"workflow {workflowShortname} not found", null, null, false);

        if (!workflow.RootElement.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Array)
            return new TransitionResult(false, "workflow has no states", null, null, false);

        // Find the current state's definition
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

            // Python's workflow format uses "state" for the target state name.
            // Also support "to" for backwards compat with some workflow definitions.
            string? newState = null;
            if (t.TryGetProperty("state", out var stateEl))
                newState = stateEl.GetString()?.Trim();
            else if (t.TryGetProperty("to", out var toEl))
                newState = toEl.GetString()?.Trim();
            if (string.IsNullOrEmpty(newState)) continue;

            // Role gate — mirrors Python's transite(): if the transition has "roles",
            // the actor MUST have at least one matching role.
            if (t.TryGetProperty("roles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
            {
                var allowedRoles = rolesEl.EnumerateArray().Select(r => r.GetString()).ToHashSet();
                if (!actorRoles.Any(r => allowedRoles.Contains(r)))
                    return new TransitionResult(false,
                        $"You don't have the permission to progress this ticket with action {action}",
                        null, null, false);
            }

            // Python's check_open_state: a state is "open" if it has a "next" array.
            var isOpen = CheckOpenState(states, newState);
            var resolutionRequired = t.TryGetProperty("resolution_required", out var rr)
                                     && rr.ValueKind == JsonValueKind.True;
            return new TransitionResult(true, null, newState, isOpen, resolutionRequired);
        }

        return new TransitionResult(false,
            $"You can't progress from {currentState} using {action}", null, null, false);
    }

    /// <summary>
    /// Mirrors Python's set_init_state_from_record: loads the workflow entry and
    /// returns its initial_state string. Returns null if the workflow or field is missing.
    /// </summary>
    public async Task<string?> GetInitialStateAsync(string spaceName, string workflowShortname, CancellationToken ct = default)
    {
        var workflow = await LoadWorkflowAsync(spaceName, workflowShortname, ct);
        if (workflow is null) return null;

        if (workflow.RootElement.TryGetProperty("initial_state", out var init))
        {
            if (init.ValueKind == JsonValueKind.String)
                return init.GetString();
            if (init.ValueKind == JsonValueKind.Array)
            {
                string? fallback = null;
                foreach (var item in init.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null) continue;
                    fallback ??= name;
                    if (item.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in roles.EnumerateArray())
                            if (r.GetString() == "default") return name;
                    }
                }
                return fallback;
            }
        }
        return null;
    }

    // Mirrors Python's check_open_state: a state is "open" if it has a "next" field.
    // States without "next" are terminal (closed).
    private static bool CheckOpenState(JsonElement statesArray, string stateName)
    {
        foreach (var s in statesArray.EnumerateArray())
        {
            if (s.TryGetProperty("state", out var name) && name.GetString() == stateName)
                return s.TryGetProperty("next", out _);
        }
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
