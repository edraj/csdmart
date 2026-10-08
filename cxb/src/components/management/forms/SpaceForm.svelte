<script lang="ts">
    import { onMount } from "svelte";
    import { Checkbox, Input, Label, Select } from "flowbite-svelte";
    import { CloseOutline } from "flowbite-svelte-icons";
    import { Dmart, QueryType, ResourceType } from "@edraj/tsdmart";
    import IconButton from "@/components/ui/IconButton.svelte";
    import type { SpaceSettings } from "@/utils/entryShapes";
    import { _ } from "@/i18n";

    let {
        formData = $bindable(),
        spaceName,
    }: {
        /** The space entry: its settings, with the rest of the record riding along. */
        formData: SpaceSettings & Record<string, unknown>;
        spaceName: string;
    } = $props();

    const uid = $props.id();

    let plugins: string[] = $state([]);
    let availableFolders: string[] = $state([]);

    formData = {
        ...formData,
        hide_folders: formData.hide_folders || [],
        hide_space: formData.hide_space ?? false,
        active_plugins: formData.active_plugins || [],
        ordinal: formData.ordinal ?? 0,
    };

    onMount(async () => {
        try {
            const manifest = await Dmart.getManifest();
            if (manifest?.status === "success") {
                plugins = manifest.attributes?.plugins ?? [];
            }
        } catch (e) {
            console.error("Failed to fetch manifest", e);
        }

        // Top folders of the managed space; only shortnames are needed.
        try {
            const foldersFull = await Dmart.query({
                type: QueryType.search,
                space_name: spaceName,
                subpath: "/",
                exact_subpath: true,
                filter_types: [ResourceType.folder],
                retrieve_json_payload: false,
                limit: 100,
                search: "",
            });
            availableFolders = (foldersFull?.records || []).map((r) => r.shortname);
        } catch (e) {
            console.error("Failed to fetch folders", e);
        }
    });

    // Both lists are seeded above; the `?? []` only satisfies the optional types.
    function addItem(listName: "hide_folders" | "active_plugins", value: string) {
        const current = formData[listName] ?? [];
        if (value && !current.includes(value)) {
            formData[listName] = [...current, value];
        }
    }

    function removeItem(listName: "hide_folders" | "active_plugins", value: string) {
        formData[listName] = (formData[listName] ?? []).filter((v) => v !== value);
    }

    const help = "mt-1 text-xs text-text-muted";
    const chip = "inline-flex items-center gap-1 rounded-full ps-3 pe-1 py-0.5 text-sm";
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    <div class="space-y-6">
        <h2 class="text-lg font-semibold text-text">{$_("space_configuration")}</h2>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
                <Label for="{uid}-ordinal" class="mb-1.5">{$_("ordinal")}</Label>
                <Input id="{uid}-ordinal" type="number" bind:value={formData.ordinal} />
                <p class={help}>{$_("ordinal_help")}</p>
            </div>

            <div class="flex flex-col justify-center">
                <div class="flex items-center gap-2">
                    <Checkbox id="{uid}-hide_space" bind:checked={formData.hide_space} />
                    <Label for="{uid}-hide_space" class="mb-0 font-normal">{$_("hide_space")}</Label>
                </div>
                <p class={help}>{$_("hide_space_help")}</p>
            </div>
        </div>

        <div class="rounded-card border border-border bg-surface p-4">
            <Label for="{uid}-hide_folders" class="mb-2 font-semibold">{$_("hide_folders")}</Label>
            <ul class="flex flex-wrap gap-2 mb-3" aria-label={$_("hide_folders")}>
                {#each formData.hide_folders ?? [] as folder (folder)}
                    <li class="{chip} bg-primary-soft text-primary">
                        {folder}
                        <IconButton size="sm" label={$_("remove_item", { values: { name: folder } })} class="text-primary hover:bg-primary/10" onclick={() => removeItem("hide_folders", folder)}>
                            <CloseOutline size="xs" />
                        </IconButton>
                    </li>
                {/each}
                {#if !formData.hide_folders?.length}
                    <li class="text-sm text-text-faint italic">{$_("no_folders_hidden")}</li>
                {/if}
            </ul>
            <Select
                id="{uid}-hide_folders"
                items={availableFolders.filter((f) => !formData.hide_folders?.includes(f)).map((f) => ({ name: f, value: f }))}
                placeholder={$_("select_folder_to_hide")}
                onchange={(e) => {
                    const target = e.target as HTMLSelectElement;
                    addItem("hide_folders", target.value);
                    target.value = "";
                }}
            />
            <p class={help}>{$_("hide_folders_help")}</p>
        </div>

        <div class="rounded-card border border-border bg-surface p-4">
            <Label for="{uid}-active_plugins" class="mb-2 font-semibold">{$_("active_plugins")}</Label>
            <ul class="flex flex-wrap gap-2 mb-3" aria-label={$_("active_plugins")}>
                {#each formData.active_plugins ?? [] as plugin (plugin)}
                    <li class="{chip} bg-success-soft text-success">
                        {plugin}
                        <IconButton size="sm" label={$_("remove_item", { values: { name: plugin } })} class="text-success hover:bg-success/10" onclick={() => removeItem("active_plugins", plugin)}>
                            <CloseOutline size="xs" />
                        </IconButton>
                    </li>
                {/each}
                {#if !formData.active_plugins?.length}
                    <li class="text-sm text-text-faint italic">{$_("no_active_plugins")}</li>
                {/if}
            </ul>
            <Select
                id="{uid}-active_plugins"
                items={plugins.filter((p) => !formData.active_plugins?.includes(p)).map((p) => ({ name: p, value: p }))}
                placeholder={$_("select_plugin_to_activate")}
                onchange={(e) => {
                    const target = e.target as HTMLSelectElement;
                    addItem("active_plugins", target.value);
                    target.value = "";
                }}
            />
            <p class={help}>{$_("active_plugins_help")}</p>
        </div>
    </div>
</div>
