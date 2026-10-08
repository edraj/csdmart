<script lang="ts">
    import { Input, Label } from "flowbite-svelte";
    import { CheckOutline, CloseOutline, SearchOutline } from "flowbite-svelte-icons";
    import { onMount } from "svelte";
    import { Dmart, QueryType } from "@edraj/tsdmart";
    import IconButton from "@/components/ui/IconButton.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { _ } from "@/i18n";

    // One multi-select for shortnames out of a management folder: roles,
    // groups and permissions all used to carry their own copy of this widget.
    // Only shortnames are needed, so the query asks for no payload.
    let {
        selected = $bindable([]),
        subpath = "/roles",
        label,
        required = false,
        emptyText,
    }: {
        selected: string[];
        /** Management-space folder the options come from. */
        subpath?: string;
        label: string;
        required?: boolean;
        /** Shown under the box when nothing is selected. */
        emptyText?: string;
    } = $props();

    const uid = $props.id();
    const inputId = `${uid}-search`;
    const listId = `${uid}-options`;

    let available: string[] = $state([]);
    let loading = $state(true);
    let searchTerm = $state("");
    let showDropdown = $state(false);
    let wrapperRef: HTMLElement | null = $state(null);

    const filtered = $derived(
        available.filter((name) => name.toLowerCase().includes(searchTerm.toLowerCase())),
    );

    async function load() {
        try {
            const response = await Dmart.query({
                space_name: "management",
                subpath,
                type: QueryType.search,
                search: "",
                retrieve_json_payload: false,
                limit: 100,
            });
            available = (response?.records ?? []).map((record) => record.shortname);
        } catch (error) {
            console.error(`Failed to load ${subpath}:`, error);
        } finally {
            loading = false;
        }
    }

    onMount(() => {
        load();

        const handleClickOutside = (event: MouseEvent) => {
            if (wrapperRef && !wrapperRef.contains(event.target as Node)) {
                showDropdown = false;
            }
        };
        document.addEventListener("click", handleClickOutside);
        return () => {
            document.removeEventListener("click", handleClickOutside);
        };
    });

    function toggle(name: string) {
        selected = selected.includes(name) ? selected.filter((r) => r !== name) : [...selected, name];
    }

    function remove(name: string) {
        selected = selected.filter((r) => r !== name);
    }
</script>

<div class="w-full">
    <Label for={inputId} class="mb-1.5 text-text">
        {#if required}<span class="text-danger" aria-hidden="true">*</span>{/if}
        {label}
    </Label>

    {#if loading}
        <LoadingState variant="skeleton" rows={1} />
    {:else}
        <div bind:this={wrapperRef} class="relative">
            <div class="relative">
                <SearchOutline
                    size="sm"
                    class="absolute start-3 top-1/2 -translate-y-1/2 text-text-faint pointer-events-none"
                    aria-hidden="true"
                />
                <Input
                    id={inputId}
                    class="ps-9"
                    placeholder={$_("search")}
                    autocomplete="off"
                    aria-expanded={showDropdown}
                    aria-controls={listId}
                    bind:value={searchTerm}
                    onfocus={() => (showDropdown = true)}
                />
            </div>

            {#if showDropdown}
                <div
                    id={listId}
                    class="absolute start-0 end-0 mt-1 rounded-card border border-border bg-surface-2 shadow-modal z-20 max-h-60 overflow-auto"
                >
                    {#if filtered.length === 0}
                        <p class="px-4 py-2 text-sm text-text-muted">{$_("no_records_found")}</p>
                    {/if}
                    {#each filtered as name (name)}
                        {@const isSelected = selected.includes(name)}
                        <button
                            type="button"
                            class="w-full px-4 py-2 flex items-center justify-between gap-2 text-start text-sm text-text hover:bg-surface-3 cursor-pointer"
                            aria-pressed={isSelected}
                            onclick={() => toggle(name)}
                        >
                            <span class="truncate">{name}</span>
                            {#if isSelected}
                                <CheckOutline size="sm" class="text-primary shrink-0" aria-hidden="true" />
                            {/if}
                        </button>
                    {/each}
                </div>
            {/if}
        </div>
    {/if}

    {#if selected.length > 0}
        <ul class="mt-3 flex flex-wrap gap-2" aria-label={label}>
            {#each selected as name (name)}
                <li class="inline-flex items-center gap-1 rounded-full bg-primary-soft text-primary ps-3 pe-1 py-0.5 text-sm">
                    <span>{name}</span>
                    <IconButton size="sm" label={$_("remove_item", { values: { name } })} onclick={() => remove(name)} class="text-primary hover:bg-primary/10">
                        <CloseOutline size="xs" />
                    </IconButton>
                </li>
            {/each}
        </ul>
    {:else if emptyText}
        <p class="mt-3 p-3 rounded-card border border-dashed border-border text-center text-sm text-text-muted">{emptyText}</p>
    {/if}
</div>
