<script lang="ts">
    import { onMount } from "svelte";
    import FormField from "./FormField.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import { unionKeys, type JsonSchema } from "@/utils/renderer/rendererUtils";
    import { _ } from "@/i18n";

    let {
        content = $bindable({}),
        schema = undefined,
    }: {
        /** The payload body: an object of fields, or a list when the schema is one. */
        content: Record<string, unknown> | unknown[];
        schema?: JsonSchema | null;
    } = $props();

    onMount(() => {
        if (schema && schema.properties) {
            initializeContent(schema.properties);
        }
    });

    // Seed create-time defaults from the schema. Only ever *adds* missing keys,
    // so any prop already present in the data (declared or not) is preserved.
    function initializeContent(properties: Record<string, JsonSchema>) {
        // A list payload has no named props to seed (and a schema with
        // `properties` does not describe one).
        if (Array.isArray(content)) return;
        for (const key in properties) {
            const prop = properties[key];

            if (content[key] !== undefined) continue;

            if (prop.type === "string") {
                content[key] = prop.default || "";
            } else if (prop.type === "number" || prop.type === "integer") {
                content[key] = prop.default !== undefined ? prop.default : null;
            } else if (prop.type === "boolean") {
                content[key] = prop.default || false;
            } else if (prop.type === "array") {
                content[key] = prop.default || [];
            } else if (prop.type === "object" && prop.properties) {
                content[key] = {};
            } else {
                content[key] = null;
            }
        }

        content = { ...content };
    }

    // Render the union of schema-declared props and props actually present in
    // the data, so every prop in the record is shown — even undeclared ones.
    let isArrayContent = $derived(Array.isArray(content));
    let topLevelKeys = $derived(unionKeys(schema, content));
    let hasContent = $derived(
        isArrayContent || (content !== null && typeof content === "object" && topLevelKeys.length > 0),
    );
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    {#if hasContent}
        {#if schema?.title || schema?.description}
            <div class="mb-4">
                {#if schema?.title}
                    <h2 class="text-lg font-semibold text-text">{schema.title}</h2>
                {/if}
                {#if schema?.description}
                    <p class="text-sm text-text-muted mt-1">{schema.description}</p>
                {/if}
            </div>
        {/if}

        <div class="space-y-4">
            {#if isArrayContent}
                <FormField name={schema?.title || $_("items")} bind:value={content} {schema} idPath="root" />
            {:else if !Array.isArray(content)}
                {#each topLevelKeys as key (key)}
                    <FormField
                        name={key}
                        bind:value={content[key]}
                        schema={schema?.properties?.[key]}
                        required={schema?.required?.includes(key) ?? false}
                        declared={!!schema?.properties?.[key]}
                        idPath={key}
                    />
                {/each}
            {/if}
        </div>
    {:else}
        <EmptyState title={$_("no_data_to_display")} />
    {/if}
</div>
