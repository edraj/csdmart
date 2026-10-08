<script lang="ts">
  import { _ } from "@/i18n";
    import { asSchemaNode, resolveSchemaDef } from "@/lib/jsonSchema";
    import type { JsonSchemaNode } from "@/lib/types";

    /** The schema body: a JSON string, or the parsed document. */
    let { content = {} }: { content?: unknown } = $props();

    // Normalise: content can be a JSON string or an object
    let schema: JsonSchemaNode = $derived.by((): JsonSchemaNode => {
        if (typeof content === "string") {
            try {
                return asSchemaNode(JSON.parse(content)) ?? {};
            } catch {
                return {};
            }
        }
        return asSchemaNode(content) ?? {};
    });

    /** One row of the properties table. */
    interface PropertyRow {
        name: string;
        type: string;
        title?: string;
        description?: string;
        required: boolean;
        constraints: string[];
        properties?: Record<string, JsonSchemaNode>;
        /** The item type of an array property, when it declares one. */
        itemsType?: string;
    }

    interface Variant {
        title: string;
        description?: string;
        properties: PropertyRow[];
    }

    /** A node's `type` for display: "any" when unset, "a,b" for a list. */
    function typeLabel(node: JsonSchemaNode | null | undefined): string {
        const type = node?.type;
        if (type === undefined) return "any";
        return Array.isArray(type) ? type.join(",") : type;
    }

    const typeColors: Record<string, string> = {
        string: "bg-success-soft text-success",
        number: "bg-info-soft text-info",
        integer: "bg-info-soft text-info",
        boolean: "bg-primary-soft text-primary",
        object: "bg-warning-soft text-warning",
        array: "bg-warning-soft text-warning",
        null: "bg-surface-3 text-text-muted",
    };

    function typeColor(type: string) {
        return typeColors[type] ?? "bg-surface-3 text-text-muted";
    }

    function getProperties(s: JsonSchemaNode | null | undefined, root: JsonSchemaNode): PropertyRow[] {
        if (!s?.properties) return [];
        const required: string[] = s.required ?? [];
        return Object.entries(s.properties).map(([name, raw]) => {
            const def = resolveSchemaDef(root, raw) ?? {};
            const items = asSchemaNode(def.items);
            const constraints: string[] = [];
            if (def.minLength != null)
                constraints.push(`minLength: ${def.minLength}`);
            if (def.maxLength != null)
                constraints.push(`maxLength: ${def.maxLength}`);
            if (def.minimum != null) constraints.push(`min: ${def.minimum}`);
            if (def.maximum != null) constraints.push(`max: ${def.maximum}`);
            if (def.pattern) constraints.push(`pattern: ${def.pattern}`);
            if (def.format) constraints.push(`format: ${def.format}`);
            if (def.minItems != null)
                constraints.push(`minItems: ${def.minItems}`);
            if (def.maxItems != null)
                constraints.push(`maxItems: ${def.maxItems}`);
            if (Array.isArray(def.enum))
                constraints.push(`enum: ${def.enum.join(", ")}`);
            return {
                name,
                type: typeLabel(def),
                title: def.title,
                description: def.description,
                required: required.includes(name),
                constraints,
                properties: def.properties,
                itemsType: items?.type !== undefined ? typeLabel(items) : undefined,
            };
        });
    }

    // Some schemas (e.g. discriminated unions like "subaccount") have no
    // top-level `properties` at all — instead each `oneOf`/`anyOf` branch is
    // its own object schema with its own `properties`. Surface each branch
    // as its own variant rather than showing an empty schema.
    function getVariants(s: JsonSchemaNode, root: JsonSchemaNode): Variant[] {
        if (s.properties) {
            return [
                {
                    title: s.title ?? "",
                    description: s.description,
                    properties: getProperties(s, root),
                },
            ];
        }

        const branches = Array.isArray(s.oneOf)
            ? s.oneOf
            : Array.isArray(s.anyOf)
                ? s.anyOf
                : undefined;

        if (!branches || branches.length === 0) return [];

        return branches.map((raw, i) => {
            const branch = asSchemaNode(raw);
            return {
                title: branch?.title || branch?.description || `Option ${i + 1}`,
                description: branch?.title ? branch?.description : undefined,
                properties: getProperties(branch, root),
            };
        });
    }

    let variants: Variant[] = $derived(getVariants(schema, schema));
</script>

<div class="schema-viewer">
    <!-- Header -->
    <div class="viewer-header">
        <div class="header-meta">
            {#if schema.title}
                <h4 class="schema-title">{schema.title}</h4>
            {/if}
            {#if schema.description}
                <p class="schema-description">{schema.description}</p>
            {/if}
        </div>
        <span class="schema-badge">JSON Schema</span>
    </div>

    <!-- Properties table -->
    {#snippet propsTable(props: PropertyRow[])}
        <div class="props-container">
            <table class="props-table">
                <thead>
                    <tr>
                        <th>Field</th>
                        <th>Type</th>
                        <th>Required</th>
                        <th>Details</th>
                    </tr>
                </thead>
                <tbody>
                    {#each props as prop (prop.name)}
                        <tr class="prop-row">
                            <td class="prop-name-cell">
                                <span class="prop-name">{prop.name}</span>
                                {#if prop.title && prop.title !== prop.name}
                                    <span class="prop-title">{prop.title}</span>
                                {/if}
                            </td>
                            <td>
                                <span class="type-badge {typeColor(prop.type)}"
                                    >{prop.type}</span
                                >
                                {#if prop.type === "array" && prop.itemsType}
                                    <span class="items-type"
                                        >of {prop.itemsType}</span
                                    >
                                {/if}
                            </td>
                            <td>
                                {#if prop.required}
                                    <span class="required-badge">Required</span>
                                {:else}
                                    <span class="optional-badge">Optional</span>
                                {/if}
                            </td>
                            <td class="details-cell">
                                {#if prop.description}
                                    <p class="prop-desc">{prop.description}</p>
                                {/if}
                                {#if prop.constraints.length > 0}
                                    <div class="constraints">
                                        {#each prop.constraints as c (c)}
                                            <code class="constraint">{c}</code>
                                        {/each}
                                    </div>
                                {/if}
                                {#if prop.type === "object" && prop.properties}
                                    <details class="nested-schema">
                                        <summary>{$_("schema_editor.nested_properties")}</summary>
                                        <div class="nested-list">
                                            {#each Object.entries(prop.properties) as [subName, subDef] (subName)}
                                                <div class="nested-row">
                                                    <span class="prop-name"
                                                        >{subName}</span
                                                    >
                                                    <span
                                                        class="type-badge {typeColor(typeLabel(subDef))}"
                                                        >{typeLabel(subDef)}</span
                                                    >
                                                </div>
                                            {/each}
                                        </div>
                                    </details>
                                {/if}
                            </td>
                        </tr>
                    {/each}
                </tbody>
            </table>
        </div>
    {/snippet}

    {#snippet emptyState()}
        <div class="empty-state">
            <svg
                class="empty-icon"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
            >
                <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="1.5"
                    d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
                />
            </svg>
            <p>No properties defined in this schema.</p>
        </div>
    {/snippet}

    {#if variants.length === 0}
        {@render emptyState()}
    {:else if variants.length === 1}
        {#if variants[0].properties.length > 0}
            {@render propsTable(variants[0].properties)}
        {:else}
            {@render emptyState()}
        {/if}
    {:else}
        <div class="variants-container">
            {#each variants as variant, i (i)}
                <details class="variant-block" open={i === 0}>
                    <summary class="variant-summary">
                        <span class="variant-title">{variant.title}</span>
                        {#if variant.properties.length > 0}
                            <span class="variant-count"
                                >{variant.properties.length} field{variant
                                    .properties.length === 1
                                    ? ""
                                    : "s"}</span
                            >
                        {/if}
                    </summary>
                    {#if variant.description}
                        <p class="variant-description">{variant.description}</p>
                    {/if}
                    {#if variant.properties.length > 0}
                        {@render propsTable(variant.properties)}
                    {:else}
                        <p class="variant-empty">No properties for this option.</p>
                    {/if}
                </details>
            {/each}
        </div>
    {/if}
</div>

<style>
    .schema-viewer {
        border-radius: 12px;
        border: 1px solid var(--color-border);
        overflow: hidden;
        background: var(--color-surface);
    }

    .viewer-header {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: 1rem;
        padding: 1rem 1.25rem;
        background: var(--color-surface);
        border-bottom: 1px solid var(--color-border);
    }

    .schema-title {
        font-size: 1rem;
        font-weight: 600;
        color: var(--color-text);
        margin: 0 0 0.25rem;
    }

    .schema-description {
        font-size: 0.8125rem;
        color: var(--color-text-muted);
        margin: 0;
    }

    .schema-badge {
        flex-shrink: 0;
        display: inline-flex;
        align-items: center;
        padding: 0.25rem 0.75rem;
        background: var(--color-info-soft);
        color: var(--color-info);
        border-radius: 9999px;
        font-size: 0.75rem;
        font-weight: 600;
        letter-spacing: 0.025em;
    }

    .props-container {
        overflow-x: auto;
    }

    .props-table {
        width: 100%;
        border-collapse: collapse;
        font-size: 0.875rem;
    }

    .props-table thead tr {
        background: var(--color-surface-3);
    }

    .props-table th {
        padding: 0.6rem 1rem;
        text-align: start;
        font-size: 0.75rem;
        font-weight: 600;
        color: var(--color-text-muted);
        text-transform: uppercase;
        letter-spacing: 0.05em;
        border-bottom: 1px solid var(--color-border);
    }

    .prop-row {
        border-bottom: 1px solid var(--color-surface-3);
        transition: background 0.15s;
    }

    .prop-row:last-child {
        border-bottom: none;
    }

    .prop-row:hover {
        background: var(--color-surface);
    }

    .props-table td {
        padding: 0.75rem 1rem;
        vertical-align: top;
    }

    .prop-name-cell {
        min-width: 140px;
    }

    .prop-name {
        display: block;
        font-weight: 600;
        color: var(--color-text);
        font-family: ui-monospace, "Cascadia Code", "Source Code Pro", Menlo,
            monospace;
        font-size: 0.8125rem;
    }

    .prop-title {
        display: block;
        font-size: 0.75rem;
        color: var(--color-text-muted);
        margin-top: 2px;
    }

    .type-badge {
        display: inline-flex;
        align-items: center;
        padding: 0.15rem 0.5rem;
        border-radius: 6px;
        font-size: 0.75rem;
        font-weight: 600;
        font-family: ui-monospace, monospace;
    }

    .items-type {
        font-size: 0.75rem;
        color: var(--color-text-faint);
        margin-inline-start: 0.25rem;
    }

    .required-badge {
        display: inline-flex;
        align-items: center;
        padding: 0.15rem 0.5rem;
        border-radius: 6px;
        font-size: 0.75rem;
        font-weight: 500;
        background: var(--color-danger-soft);
        color: var(--color-danger);
    }

    .optional-badge {
        display: inline-flex;
        align-items: center;
        padding: 0.15rem 0.5rem;
        border-radius: 6px;
        font-size: 0.75rem;
        font-weight: 500;
        background: var(--color-surface-3);
        color: var(--color-text-muted);
    }

    .details-cell {
        min-width: 200px;
    }

    .prop-desc {
        margin: 0 0 0.4rem;
        color: var(--color-text-muted);
        font-size: 0.8125rem;
        line-height: 1.4;
    }

    .constraints {
        display: flex;
        flex-wrap: wrap;
        gap: 0.25rem;
    }

    .constraint {
        display: inline-block;
        padding: 0.1rem 0.4rem;
        background: var(--color-surface-3);
        border: 1px solid var(--color-border);
        border-radius: 4px;
        font-size: 0.72rem;
        color: var(--color-text-muted);
    }

    .nested-schema {
        margin-top: 0.5rem;
    }

    .nested-schema summary {
        font-size: 0.75rem;
        color: var(--color-text-muted);
        cursor: pointer;
        user-select: none;
    }

    .nested-list {
        margin-top: 0.4rem;
        padding: 0.5rem;
        background: var(--color-surface);
        border-radius: 6px;
        display: flex;
        flex-direction: column;
        gap: 0.4rem;
    }

    .nested-row {
        display: flex;
        align-items: center;
        gap: 0.5rem;
    }

    .empty-state {
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        padding: 2.5rem 1rem;
        color: var(--color-text-faint);
        gap: 0.75rem;
        text-align: center;
    }

    .empty-icon {
        width: 2.5rem;
        height: 2.5rem;
        color: var(--color-border-strong);
    }

    .empty-state p {
        margin: 0;
        font-size: 0.875rem;
    }

    .variants-container {
        display: flex;
        flex-direction: column;
    }

    .variant-block {
        border-bottom: 1px solid var(--color-border);
    }

    .variant-block:last-child {
        border-bottom: none;
    }

    .variant-summary {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 0.75rem;
        padding: 0.75rem 1.25rem;
        cursor: pointer;
        user-select: none;
        background: var(--color-surface);
    }

    .variant-title {
        font-size: 0.875rem;
        font-weight: 600;
        color: var(--color-text);
    }

    .variant-count {
        font-size: 0.75rem;
        color: var(--color-text-muted);
    }

    .variant-description {
        margin: 0;
        padding: 0 1.25rem 0.5rem;
        font-size: 0.8125rem;
        color: var(--color-text-muted);
    }

    .variant-empty {
        margin: 0;
        padding: 0.75rem 1.25rem 1rem;
        font-size: 0.8125rem;
        color: var(--color-text-faint);
    }
</style>
