<script lang="ts">
    import { Accordion, AccordionItem, Button, Input, Label } from "flowbite-svelte";
    import { PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";
    import IconButton from "@/components/ui/IconButton.svelte";
    import ShortnamePicker from "./ShortnamePicker.svelte";
    import type { WorkflowData } from "@/utils/renderer/workflowRendererUtils";
    import { _ } from "@/i18n";

    let {
        content = $bindable({ name: "" }),
    }: {
        /** The workflow payload body, normalised below so every list is present. */
        content: WorkflowData;
    } = $props();

    const uid = $props.id();

    content = {
        name: content.name || "",
        states: (content.states || []).map((state) => ({
            ...state,
            next: (state.next || []).map((t) => ({ ...t, roles: t.roles || [] })),
            resolutions: state.resolutions || [],
        })),
        illustration: content.illustration || "",
        initial_state: (content.initial_state || []).map((is) => ({
            ...is,
            roles: is.roles || [],
        })),
    };

    // The lists are seeded above; the `?? []` fallbacks only satisfy the
    // optional types, which are optional because the server strips empty lists.
    function addState() {
        content.states = [
            ...(content.states ?? []),
            {
                name: "",
                state: "",
                next: [],
                resolutions: [],
            },
        ];
    }

    function removeState(index: number) {
        content.states = (content.states ?? []).filter((_, i) => i !== index);
    }

    function addNextTransition(stateIndex: number) {
        const state = content.states?.[stateIndex];
        if (!state) return;
        state.next = [...(state.next ?? []), { roles: [], state: "", action: "" }];
    }

    function removeNextTransition(stateIndex: number, transitionIndex: number) {
        const state = content.states?.[stateIndex];
        if (!state) return;
        state.next = (state.next ?? []).filter((_, i) => i !== transitionIndex);
    }

    function addResolution(stateIndex: number) {
        const state = content.states?.[stateIndex];
        if (!state) return;
        state.resolutions = [...(state.resolutions ?? []), { ar: "", en: "", ku: "", key: "" }];
    }

    function removeResolution(stateIndex: number, resolutionIndex: number) {
        const state = content.states?.[stateIndex];
        if (!state) return;
        state.resolutions = (state.resolutions ?? []).filter((_, i) => i !== resolutionIndex);
    }

    function addInitialState() {
        content.initial_state = [...(content.initial_state ?? []), { name: "", roles: [] }];
    }

    function removeInitialState(index: number) {
        content.initial_state = (content.initial_state ?? []).filter((_, i) => i !== index);
    }

    const card = "rounded-card border border-border bg-surface-2 shadow-card p-4";
    const inner = "rounded-card border border-border bg-surface p-3 space-y-3";
</script>

<div class="w-full max-w-4xl mx-auto {card} sm:p-5 my-2 space-y-6">
    <h2 class="text-lg font-semibold text-text">{$_("workflow_form")}</h2>

    <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
            <Label for="{uid}-workflowName" class="mb-1.5">{$_("workflow_name")}</Label>
            <Input id="{uid}-workflowName" bind:value={content.name} placeholder={$_("workflow_name")} required />
        </div>

        <div>
            <Label for="{uid}-illustration" class="mb-1.5">{$_("illustration")}</Label>
            <Input id="{uid}-illustration" bind:value={content.illustration} placeholder={$_("illustration_help")} />
        </div>
    </div>

    <section class="rounded-card border border-border p-4 space-y-4">
        <h3 class="text-base font-semibold text-text">{$_("initial_states")}</h3>
        {#each content.initial_state ?? [] as initialState, index (index)}
            <div class={inner}>
                <div class="flex justify-between items-center gap-2">
                    <h4 class="font-medium text-text">{$_("initial_state_n", { values: { n: index + 1 } })}</h4>
                    <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: initialState.name || String(index + 1) } })} onclick={() => removeInitialState(index)}>
                        <TrashBinOutline size="sm" />
                    </IconButton>
                </div>

                <div>
                    <Label for="{uid}-initialState{index}Name" class="mb-1.5">{$_("name")}</Label>
                    <Input id="{uid}-initialState{index}Name" bind:value={initialState.name} placeholder={$_("initial_state_name")} />
                </div>

                <ShortnamePicker bind:selected={initialState.roles} subpath="/roles" label={$_("roles")} />
            </div>
        {/each}

        <Button size="sm" color="alternative" onclick={addInitialState}>
            <PlusOutline size="sm" class="me-1.5" aria-hidden="true" />
            {$_("add_initial_state")}
        </Button>
    </section>

    <section class="rounded-card border border-border p-4 space-y-4">
        <h3 class="text-base font-semibold text-text">{$_("states")}</h3>
        {#each content.states ?? [] as state, stateIndex (stateIndex)}
            <Accordion flush>
                <AccordionItem>
                    {#snippet header()}
                        <span>{state.name || $_("state_n", { values: { n: stateIndex + 1 } })}</span>
                    {/snippet}

                    <div class="py-2 space-y-4">
                        <!-- Remove lives in the panel, not inside the accordion's own button. -->
                        <div class="flex justify-end">
                            <Button size="xs" color="red" outline onclick={() => removeState(stateIndex)}>
                                <TrashBinOutline size="xs" class="me-1" aria-hidden="true" />
                                {$_("remove_state")}
                            </Button>
                        </div>

                        <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                            <div>
                                <Label for="{uid}-state{stateIndex}Name" class="mb-1.5">{$_("name")}</Label>
                                <Input id="{uid}-state{stateIndex}Name" bind:value={state.name} placeholder={$_("state_name_help")} />
                            </div>
                            <div>
                                <Label for="{uid}-state{stateIndex}Id" class="mb-1.5">{$_("state_id")}</Label>
                                <Input id="{uid}-state{stateIndex}Id" bind:value={state.state} placeholder={$_("state_id_help")} dir="ltr" />
                            </div>
                        </div>

                        <div class="space-y-3">
                            <h4 class="font-medium text-text">{$_("next_transitions")}</h4>
                            {#each state.next ?? [] as transition, transitionIndex (transitionIndex)}
                                <div class={inner}>
                                    <div class="flex justify-between items-center gap-2">
                                        <h5 class="font-medium text-text">{$_("transition_n", { values: { n: transitionIndex + 1 } })}</h5>
                                        <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: String(transitionIndex + 1) } })} onclick={() => removeNextTransition(stateIndex, transitionIndex)}>
                                            <TrashBinOutline size="sm" />
                                        </IconButton>
                                    </div>

                                    <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                        <div>
                                            <Label for="{uid}-s{stateIndex}-t{transitionIndex}-state" class="mb-1.5">{$_("next_state")}</Label>
                                            <Input id="{uid}-s{stateIndex}-t{transitionIndex}-state" bind:value={transition.state} placeholder={$_("state_id_help")} dir="ltr" />
                                        </div>
                                        <div>
                                            <Label for="{uid}-s{stateIndex}-t{transitionIndex}-action" class="mb-1.5">{$_("action")}</Label>
                                            <Input id="{uid}-s{stateIndex}-t{transitionIndex}-action" bind:value={transition.action} placeholder={$_("action_name")} dir="ltr" />
                                        </div>
                                    </div>

                                    <ShortnamePicker bind:selected={transition.roles} subpath="/roles" label={$_("roles")} />
                                </div>
                            {/each}
                            <Button size="sm" color="alternative" onclick={() => addNextTransition(stateIndex)}>
                                <PlusOutline size="sm" class="me-1.5" aria-hidden="true" />
                                {$_("add_transition")}
                            </Button>
                        </div>

                        <div class="space-y-3">
                            <h4 class="font-medium text-text">{$_("resolutions")}</h4>
                            {#each state.resolutions ?? [] as resolution, resolutionIndex (resolutionIndex)}
                                <div class={inner}>
                                    <div class="flex justify-between items-center gap-2">
                                        <h5 class="font-medium text-text">{$_("resolution_n", { values: { n: resolutionIndex + 1 } })}</h5>
                                        <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: resolution.key || String(resolutionIndex + 1) } })} onclick={() => removeResolution(stateIndex, resolutionIndex)}>
                                            <TrashBinOutline size="sm" />
                                        </IconButton>
                                    </div>

                                    <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                        <div>
                                            <Label for="{uid}-s{stateIndex}-r{resolutionIndex}-key" class="mb-1.5">{$_("key")}</Label>
                                            <Input id="{uid}-s{stateIndex}-r{resolutionIndex}-key" bind:value={resolution.key} placeholder={$_("resolution_key")} dir="ltr" />
                                        </div>
                                        <div>
                                            <Label for="{uid}-s{stateIndex}-r{resolutionIndex}-en" class="mb-1.5">{$_("english")}</Label>
                                            <Input id="{uid}-s{stateIndex}-r{resolutionIndex}-en" bind:value={resolution.en} />
                                        </div>
                                        <div>
                                            <Label for="{uid}-s{stateIndex}-r{resolutionIndex}-ar" class="mb-1.5">{$_("arabic")}</Label>
                                            <Input id="{uid}-s{stateIndex}-r{resolutionIndex}-ar" bind:value={resolution.ar} dir="auto" />
                                        </div>
                                        <div>
                                            <Label for="{uid}-s{stateIndex}-r{resolutionIndex}-ku" class="mb-1.5">{$_("kurdish")}</Label>
                                            <Input id="{uid}-s{stateIndex}-r{resolutionIndex}-ku" bind:value={resolution.ku} dir="auto" />
                                        </div>
                                    </div>
                                </div>
                            {/each}
                            <Button size="sm" color="alternative" onclick={() => addResolution(stateIndex)}>
                                <PlusOutline size="sm" class="me-1.5" aria-hidden="true" />
                                {$_("add_resolution")}
                            </Button>
                        </div>
                    </div>
                </AccordionItem>
            </Accordion>
        {/each}

        <Button size="sm" color="alternative" onclick={addState}>
            <PlusOutline size="sm" class="me-1.5" aria-hidden="true" />
            {$_("add_state")}
        </Button>
    </section>
</div>
