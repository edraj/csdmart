<script lang="ts">
    import { Button, Input, Label, Select } from "flowbite-svelte";
    import { Dmart, ResourceType } from "@edraj/tsdmart";
    import { Level, showToast } from "@/utils/toast";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { errorMessage as describeError } from "@/utils/errorMessage";
    import { localizedText } from "@/utils/localized";
    import { _ } from "@/i18n";

    // Self-contained: the form progresses the ticket itself through
    // Dmart.progressTicket, so it has no state to hand back to its parent.
    let {
        space_name,
        subpath,
        shortname,
        meta,
    }: {
        space_name: string;
        subpath: string;
        shortname: string;
        meta: { workflow_shortname?: string; state?: string; is_open?: boolean };
    } = $props();

    const uid = $props.id();

    let userRoles: string[] = [];
    try {
        userRoles = JSON.parse(localStorage.getItem("roles") || "[]") || [];
    } catch {
        // Corrupted localStorage data
    }

    let ticket_status: string | null = $state(null);
    let resolution: string | null = $state(null);
    let comment = $state("");

    let ticketPayload: any = $state(null);
    let ticketStates: any[] = $state([]);
    let ticketResolutions: any[] = $state([]);
    let errorMessage = $state("");
    // The action belongs to whichever state is selected; nothing else sets it.
    const ticket_action: string | null = $derived(
        ticketStates?.filter((e) => e.state === ticket_status)[0]?.action || null,
    );

    async function get_ticket_payload() {
        const response = await Dmart.retrieveEntry({
            resource_type: ResourceType.content,
            space_name,
            subpath: "workflows",
            shortname: meta.workflow_shortname ?? "",
            retrieve_json_payload: true,
            retrieve_attachments: false,
            validate_schema: true,
        });
        const payload = response?.payload?.body ?? null;
        ticketPayload = payload;
        if (payload) {
            ticketStates = payload.states.filter((e: any) => e.state === meta.state)[0]?.next || [];
        }
    }

    // Store the promise once at initialization to avoid re-calling on re-renders
    const ticketPromise = get_ticket_payload();

    $effect(() => {
        if (ticketStates.length) {
            ticketResolutions =
                ticketPayload?.states?.filter((e: any) => e.state === ticket_status)[0]?.resolutions || [];
        }
    });

    /**
     * Progresses a ticket with the given data
     */
    async function progressTicket(e: SubmitEvent): Promise<{ success: boolean; errorMessage?: string }> {
        e.preventDefault();
        errorMessage = "";
        try {
            await Dmart.progressTicket({
                space_name,
                subpath,
                shortname,
                action: ticket_action ?? "",
                resolution: resolution ?? undefined,
                comment,
            });
            showToast(Level.info, $_("ticket_updated"));
            return { success: true };
        } catch (error: unknown) {
            showToast(Level.warn, $_("ticket_update_failed"));
            errorMessage = describeError(error, $_("ticket_update_failed"));
            return { success: false, errorMessage };
        }
    }

    function roleAllowed(e: { roles?: string[] }): boolean {
        return !e.roles || e.roles.some((el) => userRoles.includes(el));
    }
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    <h2 class="text-lg font-semibold text-text mb-4">{$_("ticket_form")}</h2>

    {#if meta.is_open}
        <form class="flex flex-col space-y-4" onsubmit={progressTicket}>
            {#await ticketPromise}
                <LoadingState variant="skeleton" rows={3} />
            {:then _ready}
                {#if ticketStates.length}
                    <div>
                        <Label for="{uid}-status" class="mb-1.5">{$_("state")}</Label>
                        <Select id="{uid}-status" bind:value={ticket_status}>
                            <option value={null}>{$_("select_an_action")}</option>
                            {#each ticketStates as e (e.state)}
                                <option value={e.state} disabled={!roleAllowed(e)}>
                                    {e.state}
                                    {roleAllowed(e) ? "" : `(${e.roles})`}
                                </option>
                            {/each}
                        </Select>
                    </div>
                {/if}

                {#key ticket_status}
                    {#if ticketResolutions.length !== 0}
                        <div>
                            <Label for="{uid}-resolution" class="mb-1.5">{$_("resolution")}</Label>
                            <Select id="{uid}-resolution" bind:value={resolution}>
                                <option value={null}>{$_("select_resolution")}</option>
                                {#each ticketResolutions as res (typeof res === "string" ? res : res.key)}
                                    {#if typeof res === "string"}
                                        <option value={res}>{res}</option>
                                    {:else}
                                        <option value={res.key}>{localizedText(res, res.key)}</option>
                                    {/if}
                                {/each}
                            </Select>
                        </div>
                    {/if}
                {/key}

                {#if ticket_status && !!ticketPayload?.states?.filter((e: any) => e.state === ticket_status)[0]?.next === false}
                    <div>
                        <Label for="{uid}-comment" class="mb-1.5">{$_("comment")}</Label>
                        <Input id="{uid}-comment" type="text" placeholder={$_("comment")} bind:value={comment} />
                    </div>
                {/if}

                <div class="flex flex-col items-end gap-2">
                    <Button type="submit" color="primary" size="sm">{$_("submit")}</Button>
                    {#if errorMessage}
                        <p class="text-danger text-sm font-medium" role="alert">{errorMessage}</p>
                    {/if}
                </div>
            {:catch error}
                <p class="text-danger text-sm" role="alert">{describeError(error, $_("entry_load_failed"))}</p>
            {/await}
        </form>
    {:else}
        <p class="rounded-card border border-info/30 bg-info-soft text-text px-4 py-3 text-sm" role="status">
            {$_("ticket_closed")}
        </p>
    {/if}
</div>
