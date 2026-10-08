<script lang="ts">
    import {params} from "@roxi/routify";
    import {Dmart, ResourceType} from "@edraj/tsdmart";
    import EntryRenderer from "@/components/management/renderers/EntryRenderer.svelte";
    import NotFoundState from "@/components/ui/NotFoundState.svelte";
    import {Alert, ListPlaceholder} from "flowbite-svelte";

    // Derive from the two params that matter, as strings: the list below
    // rewrites `page`/`sort`/`search` query params in place, and a promise
    // re-created on every `$params` change re-mounted the whole renderer (and
    // re-fetched the folder) on each page click. String-valued deriveds only
    // re-run their dependents when the value actually changes.
    const spaceName = $derived($params.space_name as string | undefined);
    const routeSubpath = $derived(($params.subpath ?? "") as string);
    let _parent_subpath = $derived(routeSubpath.split("-"))
    let parent_subpath: string = $derived(
        _parent_subpath.slice(0, _parent_subpath.length - 1).join("/") || "__root__"
    );
    let shortname: string =  $derived(_parent_subpath[_parent_subpath.length - 1]);

    let entryPromise = $derived(
        spaceName
            ? Dmart.retrieveEntry({resource_type: ResourceType.folder, space_name: spaceName, subpath: parent_subpath, shortname, retrieve_json_payload: true, retrieve_attachments: true, validate_schema: true})
            : null
    );
</script>

{#if entryPromise}
    {#await entryPromise}
        <div class="flex flex-col w-full">
            <ListPlaceholder class="m-5" size="lg" style="width: 100vw"/>
        </div>
    {:then entry}
        <EntryRenderer
            entry={entry!}
            resource_type={ResourceType.folder}
            space_name={spaceName ?? ""}
            subpath={routeSubpath.replaceAll("-", "/")}
        />
    {:catch error}
            <div class="w-full">
                <Alert color="red" class="text-lg flex h-12 m-6 p-3 flex-row justify-center items-center">
                    {error}
                </Alert>
            </div>
    {/await}
{:else}
    <NotFoundState />
{/if}
