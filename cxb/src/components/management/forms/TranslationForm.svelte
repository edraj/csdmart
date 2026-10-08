<script lang="ts">
    import { Input } from "flowbite-svelte";
    import { PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";
    import IconButton from "@/components/ui/IconButton.svelte";
    import { _ } from "@/i18n";

    let {
        entries = $bindable(),
        columns,
    }: {
        entries: { items?: Record<string, string>[] };
        columns: string[];
    } = $props();

    const uid = $props.id();

    entries = {
        items: entries?.items || [],
    };

    let tempNew: Record<string, string> = $state({});
    function appendEntry() {
        entries.items = [...(entries.items ?? []), tempNew];
        tempNew = {};
    }

    function removeEntry(index: number) {
        entries.items = (entries.items ?? []).filter((_, i) => i !== index);
    }
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    <h2 class="text-lg font-semibold text-text mb-4">{$_("translation_form")}</h2>
    <div class="overflow-x-auto">
        <table class="w-full text-sm text-start border-collapse">
            <thead class="text-xs font-semibold text-text-muted">
                <tr>
                    {#each columns as key (key)}
                        <th scope="col" class="p-2 text-start">{key}</th>
                    {/each}
                    <th scope="col" class="p-2 text-end w-12"><span class="sr-only">{$_("actions")}</span></th>
                </tr>
            </thead>
            <tbody>
                {#each entries.items ?? [] as entry, index (index)}
                    <tr class="border-t border-border">
                        {#each columns as key (key)}
                            <td class="p-2">
                                <Input bind:value={entry[key]} aria-label="{key} {index + 1}" dir="auto" />
                            </td>
                        {/each}
                        <td class="p-2 text-end">
                            <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: String(index + 1) } })} onclick={() => removeEntry(index)}>
                                <TrashBinOutline size="sm" />
                            </IconButton>
                        </td>
                    </tr>
                {/each}
                <tr class="border-t border-border bg-surface">
                    {#each columns as key (key)}
                        <td class="p-2">
                            <Input id="{uid}-new-{key}" bind:value={tempNew[key]} placeholder={$_("new_value_for", { values: { name: key } })} aria-label={$_("new_value_for", { values: { name: key } })} dir="auto" />
                        </td>
                    {/each}
                    <td class="p-2 text-end">
                        <IconButton size="sm" variant="outline" label={$_("add")} onclick={appendEntry}>
                            <PlusOutline size="sm" />
                        </IconButton>
                    </td>
                </tr>
            </tbody>
        </table>
    </div>
</div>
