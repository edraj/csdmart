<script lang="ts">
    import {goto, params} from "@roxi/routify";
    import {Button, List, ListgroupItem, ListPlaceholder, Modal,} from "flowbite-svelte";
    import {Dmart, ResourceType} from "@edraj/tsdmart";
    import {_} from "@/i18n";
    import BreadCrumbLite from "@/components/management/BreadCrumbLite.svelte";
    import {normalizeSubpath, toRouteSubpath} from "@/utils/subpath";

    $goto

    // Route: /management/content/[space_name]/health_check/[space_name_health].
    // `space_name` is the space that holds the health_check folder (always
    // "management"); `space_name_health` is the space the report is ABOUT, and
    // it is also the shortname of the report entry. The old code read
    // `$params.space_name` ("management") for the report, and `$params.shortname`
    // / `$params.subpath`, which do not exist on this route — so every click said
    // "Entry does not exist".
    const reportedSpace = $derived($params.space_name_health as string);

    type ModalData = {
        subpath: string;
        shortname: string;
        resource_type: ResourceType;
        uuid: string;
        issues: string[];
        exception: string;
    };
    let modalData: ModalData = $state({
        subpath: "",
        shortname: "",
        resource_type: ResourceType.content,
        uuid: "",
        issues: [],
        exception: "",
    });

    function handleEdit() {
        $goto(
            `/management/content/[space_name]/[subpath]/[shortname]/[resource_type]`,
            {
                space_name: reportedSpace,
                subpath: toRouteSubpath(modalData.subpath),
                shortname: modalData.shortname,
                resource_type: modalData.resource_type,
                validate_schema: "false",
            }
        );
    }

    let open = $state(false);
    let isEntryExist = $state(false);
    async function handleErrorEntryClick(err_entry: Partial<ModalData>, extra: Partial<ModalData>) {
        modalData = { ...modalData, ...err_entry, ...extra };

        try {
            // Look the clicked entry up where the report says it lives.
            await Dmart.retrieveEntry({
                resource_type: modalData.resource_type ?? ResourceType.content,
                space_name: reportedSpace,
                subpath: normalizeSubpath(modalData.subpath),
                shortname: modalData.shortname,
                retrieve_json_payload: false,
                retrieve_attachments: false,
                validate_schema: false
            })
            isEntryExist = true
        } catch (error) {
            isEntryExist = false
        }

        open = true;
    }

    const reportPromise = $derived(Dmart.retrieveEntry({
        resource_type: ResourceType.content,
        space_name: "management",
        subpath: "/health_check",
        shortname: reportedSpace,
        retrieve_json_payload: true,
        retrieve_attachments: true,
        validate_schema: true,
    }));
</script>

<Modal bind:open={open} size="lg" class="w-full" title={modalData.shortname}>
    <div class="space-y-2">
        <p><b>UUID:</b> {modalData.uuid}</p>
        <p><b>{$_("space_name")}:</b> {reportedSpace}</p>
        <p><b>{$_("subpath")}:</b> {modalData.subpath}</p>
        <p><b>Issues:</b> {modalData.issues}</p>
        <p><b>Exception:</b><br /> {modalData.exception}</p>
    </div>
    <div class="flex justify-between mt-4">
        <Button color="alternative" onclick={() => (open = false)}>{$_("close")}</Button>
        {#if isEntryExist}
            <Button color="primary" onclick={handleEdit}>{$_("edit")}</Button>
        {:else}
            <Button color="yellow" disabled>{$_("entry_does_not_exist")}</Button>
        {/if}
    </div>
</Modal>

<BreadCrumbLite
    space_name={"management"}
    subpath={"health_check"}
    resource_type={ResourceType.content}
    schema_name={"health_check"}
    shortname={reportedSpace}
/>

<div class="mx-2 mt-3 mb-3"></div>

{#await reportPromise}
    <div class="flex flex-col w-full">
        <ListPlaceholder class="m-5" size="lg" style="width: 100%"/>
    </div>
{:then response}
    {@const body = response?.payload?.body}
    <List class="w-full px-5">
        {#if body?.["invalid_folders"]}
            <ListgroupItem class="bg-blue-500 text-white">
                {`Invalid folders (${body["invalid_folders"].length} invalid entries)`}
            </ListgroupItem>
            {#if body["invalid_folders"].length}
                {#each body["invalid_folders"] as entry}
                    <ListgroupItem class="bg-gray-200">{entry}</ListgroupItem>
                {/each}
            {/if}
        {/if}

        <ListgroupItem class="bg-blue-500 text-white">
            {`Folders report`}
        </ListgroupItem>
        {#if body?.["folders_report"]}
            {#each Object.keys(body["folders_report"]) as key_entry}
                <ListgroupItem class="bg-gray-200">{key_entry}</ListgroupItem>
                {#if body["folders_report"][key_entry].valid_entries}
                    <ListgroupItem class="bg-green-500 text-white">
                        {`Valid entries ${body["folders_report"][key_entry].valid_entries}`}
                    </ListgroupItem>
                {/if}
                {#if body["folders_report"][key_entry].invalid_entries}
                    <ListgroupItem class="bg-red-500 text-white">
                        {`Invalid entries ${body["folders_report"][key_entry].invalid_entries.length}`}
                    </ListgroupItem>
                    {#each body["folders_report"][key_entry].invalid_entries as err_entry}
                        <button
                            type="button"
                            class="w-full text-start"
                            onclick={() => { handleErrorEntryClick(err_entry, { subpath: key_entry }); }}
                        >
                            <ListgroupItem class="cursor-pointer hover:bg-gray-100 border">{err_entry.shortname}</ListgroupItem>
                        </button>
                    {/each}
                {/if}
            {/each}
        {/if}
        {#if body?.["invalid_meta_folders"]}
            <ListgroupItem class="bg-blue-500 text-white">
                {`Invalid meta folders (${body["invalid_meta_folders"].length} invalid entries)`}
            </ListgroupItem>
            {#if body["invalid_meta_folders"].length}
                {#each body["invalid_meta_folders"] as entry}
                    <ListgroupItem class="bg-gray-200">{entry}</ListgroupItem>
                {/each}
            {/if}
        {/if}
    </List>
{:catch error}
    <p class="text-red-500 mx-5" role="alert">{error?.message ?? $_("failed")}</p>
{/await}
