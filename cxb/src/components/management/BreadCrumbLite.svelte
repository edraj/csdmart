<script lang="ts">
    import { ResourceType } from "@edraj/tsdmart";
    import { url } from "@roxi/routify";
    import { Breadcrumb, BreadcrumbItem } from "flowbite-svelte";
    import { ChevronRightOutline, CodeForkSolid } from "flowbite-svelte-icons";
    import { subpathSegments } from "@/utils/subpath";
    import { _ } from "@/i18n";

    let {
        space_name,
        subpath,
        shortname,
        resource_type,
        schema_name,
        payloadContentType,
    }: {
        space_name: string;
        subpath: string;
        shortname?: string;
        resource_type: ResourceType;
        schema_name?: string;
        payloadContentType?: string;
    } = $props();

    // Derived from the props, not built once in onMount: the component is
    // reused across navigations, so the crumbs must follow `subpath` as it
    // changes. Each crumb links to the folder made of the segments up to it.
    const crumbs = $derived.by(() => {
        const segments = subpathSegments(subpath);
        return segments.map((segment, index) => ({
            text: segment,
            routeSubpath: segments.slice(0, index + 1).join("-"),
        }));
    });

    const isEntry = $derived(![ResourceType.folder, ResourceType.space].includes(resource_type));
</script>

<Breadcrumb aria-label={$_("breadcrumb")} class="px-4 sm:px-6 py-3 text-sm" olClass="inline-flex items-center flex-wrap gap-y-1">
    <BreadcrumbItem href={$url(`/management/content/${space_name}`)} home linkClass="text-text-muted hover:text-primary" homeClass="inline-flex items-center gap-1.5 text-text-muted hover:text-primary">
        {#snippet icon()}
            <CodeForkSolid size="sm" class="text-text-faint" aria-hidden="true" />
        {/snippet}
        {space_name}
    </BreadcrumbItem>

    <!-- Keyed by position: a path such as /docs/docs repeats a segment, and a
         key on the text would throw each_key_duplicate. -->
    {#each crumbs as crumb, index (index)}
        <BreadcrumbItem
            href={$url("/management/content/[space_name]/[subpath]", { space_name, subpath: crumb.routeSubpath })}
            linkClass="text-text-muted hover:text-primary"
        >
            {#snippet icon()}
                <ChevronRightOutline size="sm" class="mx-1 text-text-faint rtl:rotate-180" aria-hidden="true" />
            {/snippet}
            {crumb.text}
        </BreadcrumbItem>
    {/each}

    {#if isEntry}
        <BreadcrumbItem spanClass="inline-flex items-center gap-2 text-text" aria-current="page">
            {#snippet icon()}
                <ChevronRightOutline size="sm" class="mx-1 text-text-faint rtl:rotate-180" aria-hidden="true" />
            {/snippet}
            <strong class="font-semibold">{shortname}</strong>
            <span class="text-xs text-text-muted font-mono">
                {resource_type}{#if payloadContentType}&nbsp;|&nbsp;{payloadContentType}{/if}{#if schema_name}:{schema_name}{/if}
            </span>
        </BreadcrumbItem>
    {/if}
</Breadcrumb>
