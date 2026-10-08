<script lang="ts">
    import { Button, Checkbox, Input, Label, Select } from "flowbite-svelte";
    import { CloseOutline, PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";
    import { Dmart, QueryType, ResourceType } from "@edraj/tsdmart";
    import IconButton from "@/components/ui/IconButton.svelte";
    import { isRecord } from "@/utils/compare";
    import type { FolderFlag, FolderRendering } from "@/utils/entryShapes";
    import { _ } from "@/i18n";

    let {
        content = $bindable({}),
    }: {
        /** The folder's `folder_rendering` payload body, normalised below. */
        content: FolderRendering;
    } = $props();

    const uid = $props.id();

    content = {
        icon: content.icon || "",
        icon_closed: content.icon_closed || "",
        icon_opened: content.icon_opened || "",
        shortname_title: content.shortname_title || "",

        index_attributes: content.index_attributes || [],

        query: content.query || {
            type: "",
            search: "",
            filter_types: [],
        },

        search_columns: content.search_columns || [],
        csv_columns: content.csv_columns || [],

        sort_by: content.sort_by || "",
        sort_type: content.sort_type || "",

        content_resource_types: content.content_resource_types || [],
        content_schema_shortnames: content.content_schema_shortnames || [],
        workflow_shortnames: content.workflow_shortnames || [],
        enable_pdf_schema_shortnames: content.enable_pdf_schema_shortnames || [],

        allow_view: content.allow_view || true,
        allow_create: content.allow_create || true,
        allow_update: content.allow_update || true,
        allow_delete: content.allow_delete || false,
        allow_create_category: content.allow_create_category || false,
        allow_csv: content.allow_csv || false,
        allow_upload_csv: content.allow_upload_csv || false,
        use_media: content.use_media || false,
        stream: content.stream || false,
        expand_children: content.expand_children || false,
        disable_filter: content.disable_filter || false,

        ...content,
    };

    if (!content.query) content.query = {};

    // `path` is dotted ("query.filter_types"); the walk creates missing objects.
    function addItem(path: string, template: unknown = {}) {
        let target: Record<string, unknown> = content;
        const parts = path.split(".");

        for (let i = 0; i < parts.length - 1; i++) {
            const existing = target[parts[i]];
            const next: Record<string, unknown> = isRecord(existing) ? existing : {};
            if (next !== existing) target[parts[i]] = next;
            target = next;
        }

        const lastPart = parts[parts.length - 1];
        const existing = target[lastPart];
        target[lastPart] = [...(Array.isArray(existing) ? existing : []), structuredClone(template)];
        content = { ...content };
    }

    function removeItem(path: string, index: number) {
        let target: Record<string, unknown> = content;
        const parts = path.split(".");

        for (let i = 0; i < parts.length - 1; i++) {
            const next = target[parts[i]];
            if (!isRecord(next)) return;
            target = next;
        }

        const lastPart = parts[parts.length - 1];
        const existing = target[lastPart];
        if (!Array.isArray(existing)) return;

        target[lastPart] = existing.filter((_, i) => i !== index);
        content = { ...content };
    }

    function addShortname(listName: "content_schema_shortnames" | "workflow_shortnames", event: Event) {
        const target = event.target as HTMLSelectElement;
        const current = content[listName] ?? [];
        if (target.value && !current.includes(target.value)) {
            content[listName] = [...current, target.value];
        }
        target.value = "";
    }

    function removeShortname(listName: "content_schema_shortnames" | "workflow_shortnames" | "content_resource_types", value: string) {
        content[listName] = (content[listName] ?? []).filter((v) => v !== value);
    }

    // Dropdowns only need shortnames, so neither query asks for a payload.
    const schemaOptions = Dmart.query({
        space_name: "management",
        type: QueryType.search,
        subpath: "/schema",
        search: "",
        retrieve_json_payload: false,
        limit: 99,
    }).then((result) => (result?.records ?? []).map((e) => e.shortname));
    const workflowOptions = Dmart.query({
        space_name: "management",
        type: QueryType.search,
        subpath: "/workflow",
        search: "",
        retrieve_json_payload: false,
        limit: 99,
    }).then((result) => (result?.records ?? []).map((e) => e.shortname));

    const options: { key: FolderFlag; label: string }[] = [
        { key: "allow_view", label: "allow_resource_view" },
        { key: "allow_create", label: "allow_resource_creation" },
        { key: "allow_update", label: "allow_resource_update" },
        { key: "allow_delete", label: "allow_resource_delete" },
        { key: "allow_create_category", label: "allow_folder_creation" },
        { key: "allow_csv", label: "allow_csv_download" },
        { key: "allow_upload_csv", label: "allow_csv_upload" },
        { key: "use_media", label: "content_has_media" },
        { key: "stream", label: "enable_websocket_stream" },
        { key: "expand_children", label: "expand_children" },
        { key: "disable_filter", label: "disable_filter" },
    ];

    const help = "mt-1 text-xs text-text-muted";
    const section = "rounded-card border border-border p-4 space-y-3";
    const chip = "inline-flex items-center gap-1 rounded-full bg-primary-soft text-primary ps-3 pe-1 py-0.5 text-sm";
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    <h2 class="text-lg font-semibold text-text mb-4">{$_("folder_settings")}</h2>

    <div class="space-y-6">
        <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
                <Label for="{uid}-icon" class="mb-1.5">{$_("folder_icon")}</Label>
                <Input id="{uid}-icon" placeholder={$_("icon_name")} bind:value={content.icon} />
                <p class={help}>{$_("folder_icon_help")}</p>
            </div>

            <div>
                <Label for="{uid}-icon_closed" class="mb-1.5">{$_("folder_closed_icon")}</Label>
                <Input id="{uid}-icon_closed" placeholder={$_("icon_name")} bind:value={content.icon_closed} />
                <p class={help}>{$_("folder_closed_icon_help")}</p>
            </div>

            <div>
                <Label for="{uid}-icon_opened" class="mb-1.5">{$_("folder_opened_icon")}</Label>
                <Input id="{uid}-icon_opened" placeholder={$_("icon_name")} bind:value={content.icon_opened} />
                <p class={help}>{$_("folder_opened_icon_help")}</p>
            </div>

            <div>
                <Label for="{uid}-shortname_title" class="mb-1.5">{$_("shortname_field_title")}</Label>
                <Input id="{uid}-shortname_title" placeholder={$_("shortname_field_title")} bind:value={content.shortname_title} />
            </div>
        </div>

        <!-- `query` is always seeded above; the guard is what lets the binds below type-check. -->
        {#if content.query}
        <section class={section}>
            <h3 class="font-semibold text-text">{$_("query_settings")}</h3>

            <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                    <Label for="{uid}-query_type" class="mb-1.5">{$_("query_type")}</Label>
                    <Select id="{uid}-query_type" bind:value={content.query.type}>
                        <option value="subpath">{$_("subpath")}</option>
                        <option value="search">{$_("search")}</option>
                    </Select>
                </div>

                <div>
                    <Label for="{uid}-query_search" class="mb-1.5">{$_("search_query")}</Label>
                    <Input id="{uid}-query_search" placeholder={$_("search_query")} bind:value={content.query.search} />
                </div>
            </div>

            <div>
                <p class="text-sm font-medium text-text mb-1.5">{$_("filter_types")}</p>
                {#if content.query.filter_types?.length}
                    {#each content.query.filter_types as _filterType, index (index)}
                        <div class="flex items-center gap-2 mt-1.5">
                            <Input bind:value={content.query.filter_types[index]} placeholder={$_("filter_type")} aria-label="{$_('filter_type')} {index + 1}" />
                            <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: String(index + 1) } })} onclick={() => removeItem("query.filter_types", index)}>
                                <TrashBinOutline size="sm" />
                            </IconButton>
                        </div>
                    {/each}
                {/if}
                <Button size="xs" color="alternative" class="mt-2" onclick={() => addItem("query.filter_types", "")}>
                    <PlusOutline size="xs" class="me-1" aria-hidden="true" />
                    {$_("add_filter_type")}
                </Button>
            </div>
        </section>
        {/if}

        <section class={section}>
            <h3 class="font-semibold text-text">{$_("index_attributes")}</h3>
            <p class="text-xs text-text-muted">{$_("index_attributes_help")}</p>

            {#if content.index_attributes?.length}
                {#each content.index_attributes as attribute, index (index)}
                    <div class="flex items-center gap-2 p-2 rounded-card bg-surface border border-border">
                        <Input class="grow" bind:value={attribute.key} placeholder={$_("key")} aria-label="{$_('key')} {index + 1}" />
                        <Input class="grow" bind:value={attribute.name} placeholder={$_("name")} aria-label="{$_('name')} {index + 1}" />
                        <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: String(index + 1) } })} onclick={() => removeItem("index_attributes", index)}>
                            <TrashBinOutline size="sm" />
                        </IconButton>
                    </div>
                {/each}
            {/if}
            <Button size="xs" color="alternative" onclick={() => addItem("index_attributes", { key: "", name: "" })}>
                <PlusOutline size="xs" class="me-1" aria-hidden="true" />
                {$_("add_index_attribute")}
            </Button>
        </section>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
            <section class={section}>
                <h3 class="font-semibold text-text">{$_("search_columns")}</h3>

                {#if content.search_columns?.length}
                    {#each content.search_columns as column, index (index)}
                        <div class="flex items-center gap-2">
                            <Input class="grow" bind:value={column.key} placeholder={$_("key")} size="sm" aria-label="{$_('key')} {index + 1}" />
                            <Input class="grow" bind:value={column.name} placeholder={$_("name")} size="sm" aria-label="{$_('name')} {index + 1}" />
                            <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: String(index + 1) } })} onclick={() => removeItem("search_columns", index)}>
                                <TrashBinOutline size="sm" />
                            </IconButton>
                        </div>
                    {/each}
                {/if}
                <Button size="xs" color="alternative" onclick={() => addItem("search_columns", { key: "", name: "" })}>
                    <PlusOutline size="xs" class="me-1" aria-hidden="true" />
                    {$_("add_column")}
                </Button>
            </section>

            <section class={section}>
                <h3 class="font-semibold text-text">{$_("csv_columns")}</h3>

                {#if content.csv_columns?.length}
                    {#each content.csv_columns as column, index (index)}
                        <div class="flex items-center gap-2">
                            <Input class="grow" bind:value={column.key} placeholder={$_("key")} size="sm" aria-label="{$_('key')} {index + 1}" />
                            <Input class="grow" bind:value={column.name} placeholder={$_("name")} size="sm" aria-label="{$_('name')} {index + 1}" />
                            <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: String(index + 1) } })} onclick={() => removeItem("csv_columns", index)}>
                                <TrashBinOutline size="sm" />
                            </IconButton>
                        </div>
                    {/each}
                {/if}
                <Button size="xs" color="alternative" onclick={() => addItem("csv_columns", { key: "", name: "" })}>
                    <PlusOutline size="xs" class="me-1" aria-hidden="true" />
                    {$_("add_column")}
                </Button>
            </section>
        </div>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
                <Label for="{uid}-sort_by" class="mb-1.5">{$_("sort_by")}</Label>
                <Input id="{uid}-sort_by" placeholder={$_("sort_by_help")} bind:value={content.sort_by} />
            </div>

            <div>
                <Label for="{uid}-sort_type" class="mb-1.5">{$_("sort_order")}</Label>
                <Select id="{uid}-sort_type" bind:value={content.sort_type}>
                    <option value="ascending">{$_("ascending")}</option>
                    <option value="descending">{$_("descending")}</option>
                </Select>
            </div>
        </div>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
            <section class={section}>
                <h3 class="font-semibold text-text">{$_("content_resource_types")}</h3>

                {#if content.content_resource_types?.length}
                    <ul class="flex flex-wrap gap-1.5" aria-label={$_("content_resource_types")}>
                        {#each content.content_resource_types as type (type)}
                            <li class={chip}>
                                {type}
                                <IconButton size="sm" class="text-primary hover:bg-primary/10" label={$_("remove_item", { values: { name: type } })} onclick={() => removeShortname("content_resource_types", type)}>
                                    <CloseOutline size="xs" />
                                </IconButton>
                            </li>
                        {/each}
                    </ul>
                {/if}

                <div class="rounded-card border border-border bg-surface p-2 max-h-48 overflow-y-auto">
                    {#each Object.values(ResourceType) as type (type)}
                        <div class="flex items-center gap-2 mb-2">
                            <Checkbox
                                id="{uid}-resource-type-{type}"
                                checked={content.content_resource_types?.includes(type) ?? false}
                                onchange={() => {
                                    const current = content.content_resource_types ?? [];
                                    if (current.includes(type)) {
                                        content.content_resource_types = current.filter((t) => t !== type);
                                    } else {
                                        content.content_resource_types = [...current, type];
                                    }
                                }}
                            />
                            <Label for="{uid}-resource-type-{type}" class="mb-0 font-normal">{type}</Label>
                        </div>
                    {/each}
                </div>
            </section>

            <section class={section}>
                <h3 class="font-semibold text-text">{$_("schema_shortnames")}</h3>

                {#if content.content_schema_shortnames?.length}
                    <ul class="flex flex-wrap gap-1.5" aria-label={$_("schema_shortnames")}>
                        {#each content.content_schema_shortnames as schema (schema)}
                            <li class={chip}>
                                {schema}
                                <IconButton size="sm" class="text-primary hover:bg-primary/10" label={$_("remove_item", { values: { name: schema } })} onclick={() => removeShortname("content_schema_shortnames", schema)}>
                                    <CloseOutline size="xs" />
                                </IconButton>
                            </li>
                        {/each}
                    </ul>
                {/if}

                <Label for="{uid}-add-schema" class="sr-only">{$_("schema_shortnames")}</Label>
                <Select id="{uid}-add-schema" onchange={(e) => addShortname("content_schema_shortnames", e)}>
                    <option value="">{$_("select_schema")}</option>
                    {#await schemaOptions then schemas}
                        {#each schemas as schema (schema)}
                            <option value={schema}>{schema}</option>
                        {/each}
                    {/await}
                </Select>

                <h3 class="font-semibold text-text pt-2 border-t border-border">{$_("workflow_shortnames")}</h3>

                {#if content.workflow_shortnames?.length}
                    <ul class="flex flex-wrap gap-1.5" aria-label={$_("workflow_shortnames")}>
                        {#each content.workflow_shortnames as workflow (workflow)}
                            <li class={chip}>
                                {workflow}
                                <IconButton size="sm" class="text-primary hover:bg-primary/10" label={$_("remove_item", { values: { name: workflow } })} onclick={() => removeShortname("workflow_shortnames", workflow)}>
                                    <CloseOutline size="xs" />
                                </IconButton>
                            </li>
                        {/each}
                    </ul>
                {/if}

                <Label for="{uid}-add-workflow" class="sr-only">{$_("workflow_shortnames")}</Label>
                <Select id="{uid}-add-workflow" onchange={(e) => addShortname("workflow_shortnames", e)}>
                    <option value="">{$_("select_workflow")}</option>
                    {#await workflowOptions then workflows}
                        {#each workflows as workflow (workflow)}
                            <option value={workflow}>{workflow}</option>
                        {/each}
                    {/await}
                </Select>
            </section>
        </div>

        <section class={section}>
            <h3 class="font-semibold text-text">{$_("pdf_schema_shortnames")}</h3>

            {#if content.enable_pdf_schema_shortnames?.length}
                {#each content.enable_pdf_schema_shortnames as _shortname, index (index)}
                    <div class="flex items-center gap-2">
                        <Input class="grow" bind:value={content.enable_pdf_schema_shortnames[index]} placeholder={$_("schema_shortname")} aria-label="{$_('schema_shortname')} {index + 1}" />
                        <IconButton size="sm" variant="danger" label={$_("remove_item", { values: { name: String(index + 1) } })} onclick={() => removeItem("enable_pdf_schema_shortnames", index)}>
                            <TrashBinOutline size="sm" />
                        </IconButton>
                    </div>
                {/each}
            {/if}
            <Button size="xs" color="alternative" onclick={() => addItem("enable_pdf_schema_shortnames", "")}>
                <PlusOutline size="xs" class="me-1" aria-hidden="true" />
                {$_("add_pdf_schema_shortname")}
            </Button>
        </section>

        <section class={section}>
            <h3 class="font-semibold text-text">{$_("folder_options")}</h3>

            <div class="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-3">
                {#each options as option (option.key)}
                    <div class="flex items-center gap-2">
                        <Checkbox id="{uid}-{option.key}" bind:checked={content[option.key]} />
                        <Label for="{uid}-{option.key}" class="mb-0 font-normal">{$_(option.label)}</Label>
                    </div>
                {/each}
            </div>
        </section>
    </div>
</div>
