<script lang="ts">
    import { params } from "@roxi/routify";
    import { Dmart, ResourceType } from "@edraj/tsdmart";
    import EntryRenderer from "@/components/management/renderers/EntryRenderer.svelte";
    import NotFoundState from "@/components/ui/NotFoundState.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { _ } from "@/i18n";

    // Derive from the one param that matters, not from `$params` as a whole:
    // the list below rewrites `page`/`sort`/`search` query params in place, and
    // a promise re-created on every `$params` change re-mounted the whole
    // renderer (and re-fetched the entry) on each page click.
    const spaceName = $derived($params.space_name as string | undefined);
    // Bumped by "Retry": a new attempt re-creates the promise.
    let attempt = $state(0);
    const entryPromise = $derived(
        spaceName && attempt >= 0
            ? Dmart.retrieveEntry({
                resource_type: ResourceType.space, space_name: spaceName, subpath: "__root__", shortname: spaceName, retrieve_json_payload: true, retrieve_attachments: true, validate_schema: true
            })
            : null
    );
</script>

{#if entryPromise}
    {#await entryPromise}
        <div class="p-4 sm:p-6"><LoadingState variant="skeleton" rows={8} /></div>
    {:then entry}
        <EntryRenderer
            entry={entry!}
            resource_type={ResourceType.space}
            space_name={spaceName ?? ""}
            subpath="/"
        />
    {:catch error}
        <div class="p-4 sm:p-6">
            <ErrorState title={$_("entry_load_failed")} {error} onRetry={() => attempt++} />
        </div>
    {/await}
{:else}
    <NotFoundState />
{/if}
