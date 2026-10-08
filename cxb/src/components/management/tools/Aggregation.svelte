<script lang="ts">
    import { Button, Input, Label, Select } from "flowbite-svelte";
    import { MinusOutline, PlusOutline } from "flowbite-svelte-icons";
    import { _ } from "@/i18n";
    import IconButton from "@/components/ui/IconButton.svelte";

    // The three lists of an aggregation query: fields to load, fields to group
    // by, and reducers (name/alias/args). One section is edited at a time.
    type Reducer = { name: string; alias: string; args: string };
    type Section = "load" | "group_by" | "reducers";
    type AggregationData = { load: string[]; group_by: string[]; reducers: Reducer[] };

    let {
        aggregation_data = $bindable({ load: [], group_by: [], reducers: [] }),
    }: { aggregation_data: AggregationData } = $props();

    let currentSection: Section = $state("load");

    function addInput() {
        if (currentSection === "reducers") {
            aggregation_data.reducers = [...aggregation_data.reducers, { name: "", alias: "", args: "" }];
        } else {
            aggregation_data[currentSection] = [...aggregation_data[currentSection], ""];
        }
    }

    function deleteInput(index: number) {
        if (currentSection === "reducers") {
            aggregation_data.reducers = aggregation_data.reducers.filter((_, i) => i !== index);
        } else {
            aggregation_data[currentSection] = aggregation_data[currentSection].filter((_, i) => i !== index);
        }
    }
</script>

<div class="space-y-4">
    <div class="flex items-center gap-2">
        <Label for="aggregation_section" class="sr-only">{$_("aggregation_section")}</Label>
        <Select id="aggregation_section" bind:value={currentSection} class="w-full">
            <option value="load">{$_("aggregation_load")}</option>
            <option value="group_by">{$_("aggregation_group_by")}</option>
            <option value="reducers">{$_("aggregation_reducers")}</option>
        </Select>
        <Button color="primary" onclick={addInput} class="shrink-0">
            <PlusOutline size="sm" class="me-1" aria-hidden="true" />
            {$_("add")}
        </Button>
    </div>

    <div class="space-y-3">
        {#if currentSection === "reducers"}
            {#each aggregation_data.reducers as reducer, index (index)}
                <div class="grid grid-cols-1 sm:grid-cols-3 gap-3 items-end">
                    <div>
                        <Label for="reducer_name_{index}" class="mb-1 text-xs">{$_("name")}</Label>
                        <Input id="reducer_name_{index}" type="text" bind:value={reducer.name} />
                    </div>
                    <div>
                        <Label for="reducer_alias_{index}" class="mb-1 text-xs">{$_("alias")}</Label>
                        <Input id="reducer_alias_{index}" type="text" bind:value={reducer.alias} />
                    </div>
                    <div class="flex items-end gap-2">
                        <div class="grow">
                            <Label for="reducer_args_{index}" class="mb-1 text-xs">{$_("args")}</Label>
                            <Input id="reducer_args_{index}" type="text" bind:value={reducer.args} />
                        </div>
                        <IconButton label={$_("remove")} variant="danger" onclick={() => deleteInput(index)}>
                            <MinusOutline size="sm" />
                        </IconButton>
                    </div>
                </div>
            {/each}
        {:else}
            {#each aggregation_data[currentSection] as _value, index (index)}
                <div class="flex items-center gap-2">
                    <Input
                        type="text"
                        class="grow"
                        aria-label={$_(currentSection === "load" ? "aggregation_load" : "aggregation_group_by")}
                        bind:value={aggregation_data[currentSection][index]}
                    />
                    <IconButton label={$_("remove")} variant="danger" onclick={() => deleteInput(index)}>
                        <MinusOutline size="sm" />
                    </IconButton>
                </div>
            {/each}
        {/if}
    </div>
</div>
