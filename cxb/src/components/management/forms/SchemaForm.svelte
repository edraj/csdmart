<script lang="ts">
    import { Accordion, AccordionItem, Button, Checkbox, Input, Label, Select } from "flowbite-svelte";
    import { PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";
    import { transformFormToJson, transformJsonToForm } from "@/utils/editors/schemaEditorUtils";
    import {
        addArrayItem,
        addProperty,
        createDefaultSchemaContent,
        removeProperty,
        schemaTypes,
        toggleRequired,
        type SchemaFormNode,
    } from "@/utils/schemaFormUtils";
    import type { JsonSchema } from "@/utils/renderer/rendererUtils";
    import Badge from "@/components/ui/Badge.svelte";
    import IconButton from "@/components/ui/IconButton.svelte";
    import { _ } from "@/i18n";

    let {
        content = $bindable({}),
    }: {
        /** The stored schema (properties keyed by name); written back on every edit. */
        content: JsonSchema;
    } = $props();

    const uid = $props.id();

    if (!content || Object.keys(content).length === 0) {
        content = createDefaultSchemaContent();
    }

    // The converter walks arbitrary JSON; what it makes of a schema is a form node.
    let formContent = $state(transformJsonToForm($state.snapshot(content)) as SchemaFormNode);

    function handleAddProperty(parentPath = "") {
        formContent = addProperty(formContent, parentPath);
    }

    function handleAddArrayItem(parentPath: string) {
        formContent = addArrayItem(formContent, parentPath);
    }

    function handleRemoveProperty(path: string, index: number) {
        formContent = removeProperty(formContent, path, index);
    }

    // A property row always has a name (the converter gives every one a string),
    // but the node type leaves it optional for `items` nodes.
    function handleToggleRequired(propertyName: string | undefined) {
        formContent = toggleRequired(formContent, propertyName ?? "");
    }

    function isRequired(propertyName: string | undefined): boolean {
        return !!formContent.required?.includes(propertyName ?? "");
    }

    $effect(() => {
        const schemaContent = transformFormToJson(structuredClone($state.snapshot(formContent)));
        content = schemaContent as JsonSchema;
    });

    const section = "rounded-card border border-border bg-surface p-3 space-y-3";
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    <h2 class="text-lg font-semibold text-text mb-4">{$_("schema_editor")}</h2>

    <div class="space-y-6">
        <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
                <Label for="{uid}-schema-title" class="mb-1.5">{$_("schema_title")}</Label>
                <Input id="{uid}-schema-title" placeholder={$_("schema_title")} bind:value={formContent.title} />
            </div>
            <div>
                <Label for="{uid}-schema-description" class="mb-1.5">{$_("schema_description")}</Label>
                <Input id="{uid}-schema-description" placeholder={$_("schema_description")} bind:value={formContent.description} />
            </div>
        </div>

        <div class="rounded-card border border-border p-4">
            <div class="flex justify-between items-center mb-4 gap-2">
                <h3 class="font-semibold text-text">{$_("properties")}</h3>
                <Button size="xs" color="primary" onclick={() => handleAddProperty()}>
                    <PlusOutline size="xs" class="me-1" aria-hidden="true" />
                    {$_("add_property")}
                </Button>
            </div>

            {#if formContent.properties && formContent.properties.length > 0}
                <Accordion flush>
                    {#each formContent.properties as property, index (property.id ?? index)}
                        <AccordionItem>
                            {#snippet header()}
                                <span class="inline-flex items-center gap-2">
                                    <span class="font-medium">{property.name || $_("new_property")}</span>
                                    {#if property.type}
                                        <Badge variant="info" size="sm">{property.type}</Badge>
                                    {/if}
                                    {#if isRequired(property.name)}
                                        <Badge variant="danger" size="sm">{$_("required")}</Badge>
                                    {/if}
                                </span>
                            {/snippet}

                            <div class="py-2 space-y-4">
                                <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                                    <div>
                                        <Label for="{uid}-property-name-{index}" class="mb-1.5">{$_("property_name")}</Label>
                                        <Input id="{uid}-property-name-{index}" placeholder={$_("property_name")} bind:value={property.name} />
                                    </div>
                                    <div>
                                        <Label for="{uid}-property-type-{index}" class="mb-1.5">{$_("property_type")}</Label>
                                        <Select id="{uid}-property-type-{index}" bind:value={property.type}>
                                            {#each schemaTypes as type (type.value)}
                                                <option value={type.value}>{type.value}</option>
                                            {/each}
                                        </Select>
                                    </div>
                                    <div>
                                        <Label for="{uid}-property-title-{index}" class="mb-1.5">{$_("title")}</Label>
                                        <Input id="{uid}-property-title-{index}" placeholder={$_("title")} bind:value={property.title} />
                                    </div>
                                    <div>
                                        <Label for="{uid}-property-description-{index}" class="mb-1.5">{$_("description")}</Label>
                                        <Input id="{uid}-property-description-{index}" placeholder={$_("description")} bind:value={property.description} />
                                    </div>
                                </div>

                                {#if property.type === "string"}
                                    <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                                        <div>
                                            <Label for="{uid}-property-minLength-{index}" class="mb-1.5">{$_("min_length")}</Label>
                                            <Input id="{uid}-property-minLength-{index}" type="number" bind:value={property.minLength} />
                                        </div>
                                        <div>
                                            <Label for="{uid}-property-maxLength-{index}" class="mb-1.5">{$_("max_length")}</Label>
                                            <Input id="{uid}-property-maxLength-{index}" type="number" bind:value={property.maxLength} />
                                        </div>
                                        <div>
                                            <Label for="{uid}-property-pattern-{index}" class="mb-1.5">{$_("pattern_regex")}</Label>
                                            <Input id="{uid}-property-pattern-{index}" dir="ltr" bind:value={property.pattern} />
                                        </div>
                                        <div>
                                            <Label for="{uid}-property-format-{index}" class="mb-1.5">{$_("format")}</Label>
                                            <Select id="{uid}-property-format-{index}" bind:value={property.format}>
                                                <option value="">{$_("none")}</option>
                                                <option value="date-time">date-time</option>
                                                <option value="date">date</option>
                                                <option value="time">time</option>
                                                <option value="email">email</option>
                                                <option value="uri">uri</option>
                                            </Select>
                                        </div>
                                    </div>
                                {:else if property.type === "number" || property.type === "integer"}
                                    <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                                        <div>
                                            <Label for="{uid}-property-minimum-{index}" class="mb-1.5">{$_("minimum")}</Label>
                                            <Input id="{uid}-property-minimum-{index}" type="number" bind:value={property.minimum} />
                                        </div>
                                        <div>
                                            <Label for="{uid}-property-maximum-{index}" class="mb-1.5">{$_("maximum")}</Label>
                                            <Input id="{uid}-property-maximum-{index}" type="number" bind:value={property.maximum} />
                                        </div>
                                        <div>
                                            <Label for="{uid}-property-multipleOf-{index}" class="mb-1.5">{$_("multiple_of")}</Label>
                                            <Input id="{uid}-property-multipleOf-{index}" type="number" bind:value={property.multipleOf} />
                                        </div>
                                    </div>
                                {:else if property.type === "array"}
                                    <div class={section}>
                                        <div class="flex justify-between items-center gap-2">
                                            <h4 class="font-medium text-text">{$_("array_items")}</h4>
                                            <Button size="xs" color="alternative" onclick={() => handleAddArrayItem(`properties.${index}`)}>{$_("configure_items")}</Button>
                                        </div>

                                        {#if property.items}
                                            <div class="space-y-3">
                                                <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                                                    <div>
                                                        <Label for="{uid}-items-type-{index}" class="mb-1.5">{$_("items_type")}</Label>
                                                        <Select id="{uid}-items-type-{index}" bind:value={property.items.type}>
                                                            {#each schemaTypes as type (type.value)}
                                                                <option value={type.value}>{type.value}</option>
                                                            {/each}
                                                        </Select>
                                                    </div>

                                                    {#if property.items.type === "object" && property.items.properties}
                                                        <div class="md:col-span-2">
                                                            <p class="text-sm font-medium text-text mb-1.5">{$_("object_properties")}</p>
                                                            <div class="rounded-card border border-border p-2 space-y-2">
                                                                <Button size="xs" color="alternative" onclick={() => handleAddProperty(`properties.${index}.items`)}>
                                                                    <PlusOutline size="xs" class="me-1" aria-hidden="true" />
                                                                    {$_("add_object_property")}
                                                                </Button>

                                                                {#each property.items.properties as itemProperty, itemIndex (itemProperty.id ?? itemIndex)}
                                                                    <div class="p-2 rounded-card bg-surface-2 border border-border">
                                                                        <div class="grid grid-cols-1 md:grid-cols-2 gap-2">
                                                                            <div>
                                                                                <Label for="{uid}-item-property-name-{index}-{itemIndex}" class="mb-1.5">{$_("name")}</Label>
                                                                                <Input id="{uid}-item-property-name-{index}-{itemIndex}" placeholder={$_("property_name")} bind:value={itemProperty.name} />
                                                                            </div>
                                                                            <div>
                                                                                <Label for="{uid}-item-property-type-{index}-{itemIndex}" class="mb-1.5">{$_("type")}</Label>
                                                                                <Select id="{uid}-item-property-type-{index}-{itemIndex}" bind:value={itemProperty.type}>
                                                                                    {#each schemaTypes as type (type.value)}
                                                                                        <option value={type.value}>{type.value}</option>
                                                                                    {/each}
                                                                                </Select>
                                                                            </div>
                                                                        </div>
                                                                        <div class="mt-2 flex justify-end">
                                                                            <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: itemProperty.name || String(itemIndex + 1) } })} onclick={() => handleRemoveProperty(`properties.${index}.items.properties`, itemIndex)}>
                                                                                <TrashBinOutline size="sm" />
                                                                            </IconButton>
                                                                        </div>
                                                                    </div>
                                                                {/each}
                                                            </div>
                                                        </div>
                                                    {/if}
                                                </div>

                                                <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                                                    <div>
                                                        <Label for="{uid}-array-minItems-{index}" class="mb-1.5">{$_("min_items")}</Label>
                                                        <Input id="{uid}-array-minItems-{index}" type="number" bind:value={property.minItems} />
                                                    </div>
                                                    <div>
                                                        <Label for="{uid}-array-maxItems-{index}" class="mb-1.5">{$_("max_items")}</Label>
                                                        <Input id="{uid}-array-maxItems-{index}" type="number" bind:value={property.maxItems} />
                                                    </div>
                                                </div>
                                            </div>
                                        {/if}
                                    </div>
                                {:else if property.type === "object"}
                                    <div class={section}>
                                        <div class="flex justify-between items-center gap-2">
                                            <h4 class="font-medium text-text">{$_("object_properties")}</h4>
                                            <Button size="xs" color="alternative" onclick={() => handleAddProperty(`properties.${index}`)}>
                                                <PlusOutline size="xs" class="me-1" aria-hidden="true" />
                                                {$_("add_object_property")}
                                            </Button>
                                        </div>

                                        {#if property.properties && property.properties.length > 0}
                                            {#each property.properties as nestedProperty, nestedIndex (nestedProperty.id ?? nestedIndex)}
                                                <div class="p-2 rounded-card bg-surface-2 border border-border">
                                                    <div class="grid grid-cols-1 md:grid-cols-2 gap-2">
                                                        <div>
                                                            <Label for="{uid}-nested-property-name-{index}-{nestedIndex}" class="mb-1.5">{$_("name")}</Label>
                                                            <Input id="{uid}-nested-property-name-{index}-{nestedIndex}" placeholder={$_("property_name")} bind:value={nestedProperty.name} />
                                                        </div>
                                                        <div>
                                                            <Label for="{uid}-nested-property-type-{index}-{nestedIndex}" class="mb-1.5">{$_("type")}</Label>
                                                            <Select id="{uid}-nested-property-type-{index}-{nestedIndex}" bind:value={nestedProperty.type}>
                                                                {#each schemaTypes as type (type.value)}
                                                                    <option value={type.value}>{type.value}</option>
                                                                {/each}
                                                            </Select>
                                                        </div>
                                                    </div>
                                                    <div class="mt-2 flex justify-end">
                                                        <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: nestedProperty.name || String(nestedIndex + 1) } })} onclick={() => handleRemoveProperty(`properties.${index}.properties`, nestedIndex)}>
                                                            <TrashBinOutline size="sm" />
                                                        </IconButton>
                                                    </div>
                                                </div>
                                            {/each}
                                        {/if}
                                    </div>
                                {/if}

                                <div class="flex items-center justify-between gap-2 pt-2">
                                    <div class="flex items-center gap-2">
                                        <Checkbox id="{uid}-property-required-{index}" checked={isRequired(property.name)} onchange={() => handleToggleRequired(property.name)} />
                                        <Label for="{uid}-property-required-{index}" class="mb-0 font-normal">{$_("required")}</Label>
                                    </div>
                                    <Button size="xs" color="red" outline onclick={() => handleRemoveProperty("properties", index)}>
                                        <TrashBinOutline size="xs" class="me-1" aria-hidden="true" />
                                        {$_("remove_property")}
                                    </Button>
                                </div>
                            </div>
                        </AccordionItem>
                    {/each}
                </Accordion>
            {:else}
                <p class="text-sm text-text-muted text-center py-4">{$_("no_properties_hint")}</p>
            {/if}
        </div>
    </div>
</div>
