<script lang="ts">
    import {ResourceType} from "@edraj/tsdmart";
    import {goto} from "@roxi/routify";
    import {Breadcrumb, BreadcrumbItem} from "flowbite-svelte";
    import {ChevronDoubleRightOutline, CodeForkSolid} from "flowbite-svelte-icons";
    import {subpathSegments} from "@/utils/subpath";

    $goto

    let {
        space_name,
        subpath,
        shortname,
        resource_type,
        schema_name,
        payloadContentType,
    } : {
        space_name: string,
        subpath: string,
        shortname?: string,
        resource_type: ResourceType,
        schema_name?: string,
        payloadContentType?: string,
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

    function openCrumb(routeSubpath: string) {
        $goto("/management/content/[space_name]/[subpath]", {
            space_name,
            subpath: routeSubpath,
        });
    }
</script>

<Breadcrumb aria-label="Breadcrumb" class="px-5 py-3 dark:bg-gray-900">
    <BreadcrumbItem href={`/management/content/${space_name}`} home>
        {#snippet icon()}
            <CodeForkSolid size="md" class="text-gray-500" style="transform: rotate(180deg);" />
        {/snippet} {space_name}
    </BreadcrumbItem>

    <!-- Keyed by position: a path such as /docs/docs repeats a segment, and a
         key on the text would throw each_key_duplicate. -->
    {#each crumbs as crumb, index (index)}
        <BreadcrumbItem onclick={() => openCrumb(crumb.routeSubpath)} class="cursor-pointer">
            {#snippet icon()}
                <ChevronDoubleRightOutline class="rtl:rotate-180 dark:text-white" />
            {/snippet}
            {crumb.text}
        </BreadcrumbItem>
    {/each}

    {#if ![ResourceType.folder, ResourceType.space].includes(resource_type)}
        <BreadcrumbItem>
            <strong>{shortname} </strong>&nbsp;(&nbsp;{resource_type} {#if payloadContentType} {`| ${payloadContentType}`}{/if}{#if schema_name}:{schema_name}{/if}&nbsp;)
        </BreadcrumbItem>
    {/if}
</Breadcrumb>
