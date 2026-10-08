<script lang="ts">
    import { params } from "@roxi/routify";
    import { Dmart, ResourceType } from "@edraj/tsdmart";
    import EntryRenderer from "@/components/management/renderers/EntryRenderer.svelte";
    import NotFoundState from "@/components/ui/NotFoundState.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { _ } from "@/i18n";

    // String-valued deriveds: query-param churn (page/sort/search rewritten by
    // the list inside the renderer) must not re-create the promise.
    const spaceName = $derived($params.space_name as string | undefined);
    const routeSubpath = $derived($params.subpath as string | undefined);
    const shortname = $derived($params.shortname as string | undefined);
    const resourceTypeName = $derived($params.resource_type as string | undefined);
    const validateSchema = $derived($params.validate_schema !== "false");

    const resourceType = $derived(
        resourceTypeName && resourceTypeName in ResourceType
            ? ResourceType[resourceTypeName as keyof typeof ResourceType]
            : undefined,
    );

    // Bumped by "Retry": a new attempt re-creates the promise.
    let attempt = $state(0);
    const entryPromise = $derived(
        spaceName && routeSubpath && shortname && resourceType && attempt >= 0
            ? Dmart.retrieveEntry({
                resource_type: resourceType,
                space_name: spaceName,
                subpath: routeSubpath.replaceAll("-", "/"),
                shortname,
                retrieve_json_payload: true,
                retrieve_attachments: true,
                validate_schema: validateSchema,
            })
            : null,
    );
</script>

{#if entryPromise && resourceType}
    {#await entryPromise}
        <div class="p-4 sm:p-6"><LoadingState variant="skeleton" rows={8} /></div>
    {:then entry}
        <EntryRenderer
            entry={entry!}
            resource_type={resourceType}
            space_name={spaceName ?? ""}
            subpath={routeSubpath?.replaceAll("-", "/") ?? "/"}
        />
    {:catch error}
        <div class="p-4 sm:p-6">
            <ErrorState title={$_("entry_load_failed")} {error} onRetry={() => attempt++} />
        </div>
    {/await}
{:else}
    <NotFoundState />
{/if}
