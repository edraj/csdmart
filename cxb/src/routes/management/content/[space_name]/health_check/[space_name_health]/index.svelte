<script lang="ts">
    import { goto, params } from "@roxi/routify";
    import { Button, Modal } from "flowbite-svelte";
    import { Dmart, ResourceType } from "@edraj/tsdmart";
    import { _ } from "@/i18n";
    import BreadCrumbLite from "@/components/management/BreadCrumbLite.svelte";
    import Badge from "@/components/ui/Badge.svelte";
    import Card from "@/components/ui/Card.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import { formatNumber } from "@/utils/format";
    import { normalizeSubpath, toRouteSubpath } from "@/utils/subpath";

    // Route: /management/content/[space_name]/health_check/[space_name_health].
    // `space_name` is the space that holds the health_check folder (always
    // "management"); `space_name_health` is the space the report is ABOUT, and
    // it is also the shortname of the report entry.
    const reportedSpace = $derived($params.space_name_health as string);

    type InvalidEntry = {
        shortname: string;
        resource_type?: ResourceType;
        uuid?: string;
        issues?: string[];
        exception?: string;
    };
    type FolderReport = { valid_entries?: number; invalid_entries?: InvalidEntry[] };
    type ReportBody = {
        invalid_folders?: string[];
        folders_report?: Record<string, FolderReport>;
        invalid_meta_folders?: string[];
    };

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
    async function handleErrorEntryClick(entry: InvalidEntry, subpath: string) {
        modalData = {
            subpath,
            shortname: entry.shortname,
            resource_type: entry.resource_type ?? ResourceType.content,
            uuid: entry.uuid ?? "",
            issues: entry.issues ?? [],
            exception: entry.exception ?? "",
        };

        try {
            // Look the clicked entry up where the report says it lives.
            await Dmart.retrieveEntry({
                resource_type: modalData.resource_type,
                space_name: reportedSpace,
                subpath: normalizeSubpath(modalData.subpath),
                shortname: modalData.shortname,
                retrieve_json_payload: false,
                retrieve_attachments: false,
                validate_schema: false,
            });
            isEntryExist = true;
        } catch {
            isEntryExist = false;
        }

        open = true;
    }

    let attempt = $state(0);
    const reportPromise = $derived(
        attempt >= 0
            ? Dmart.retrieveEntry({
                resource_type: ResourceType.content,
                space_name: "management",
                subpath: "/health_check",
                shortname: reportedSpace,
                retrieve_json_payload: true,
                retrieve_attachments: true,
                validate_schema: true,
            })
            : null,
    );
</script>

<Modal bind:open size="lg" title={modalData.shortname} class="rounded-modal shadow-modal">
    <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
        <dt class="text-text-muted">{$_("uuid")}</dt>
        <dd class="font-mono break-all" dir="ltr">{modalData.uuid || $_("not_applicable")}</dd>
        <dt class="text-text-muted">{$_("space_name")}</dt>
        <dd>{reportedSpace}</dd>
        <dt class="text-text-muted">{$_("subpath")}</dt>
        <dd class="font-mono" dir="ltr">{modalData.subpath}</dd>
        <dt class="text-text-muted">{$_("issues")}</dt>
        <dd>
            {#if modalData.issues.length}
                <ul class="list-disc list-inside">
                    {#each modalData.issues as issue, i (i)}<li>{issue}</li>{/each}
                </ul>
            {:else}
                {$_("not_applicable")}
            {/if}
        </dd>
        <dt class="text-text-muted">{$_("exception")}</dt>
        <dd class="whitespace-pre-wrap break-words font-mono text-xs" dir="ltr">{modalData.exception || $_("not_applicable")}</dd>
    </dl>
    <div class="flex items-center justify-end gap-2 mt-6">
        <Button color="alternative" onclick={() => (open = false)}>{$_("close")}</Button>
        {#if isEntryExist}
            <Button color="primary" onclick={handleEdit}>{$_("edit")}</Button>
        {:else}
            <Badge variant="warning">{$_("entry_does_not_exist")}</Badge>
        {/if}
    </div>
</Modal>

<BreadCrumbLite
    space_name="management"
    subpath="health_check"
    resource_type={ResourceType.content}
    schema_name="health_check"
    shortname={reportedSpace}
/>

<div class="px-4 sm:px-6 pb-6">
    {#if reportPromise}
        {#await reportPromise}
            <LoadingState variant="skeleton" rows={8} />
        {:then response}
            {@const body = (response?.payload?.body ?? {}) as ReportBody}
            {@const folderReports = Object.entries(body.folders_report ?? {})}
            <div class="space-y-4">
                {#if body.invalid_folders}
                    <Card padding="none">
                        <div class="flex items-center justify-between px-4 py-3 border-b border-border">
                            <h2 class="font-semibold text-text">{$_("invalid_folders")}</h2>
                            <Badge variant={body.invalid_folders.length ? "danger" : "success"}>
                                {formatNumber(body.invalid_folders.length)}
                            </Badge>
                        </div>
                        {#if body.invalid_folders.length}
                            <ul class="divide-y divide-border text-sm">
                                {#each body.invalid_folders as entry (entry)}
                                    <li class="px-4 py-2 font-mono" dir="ltr">{entry}</li>
                                {/each}
                            </ul>
                        {/if}
                    </Card>
                {/if}

                <Card padding="none">
                    <div class="px-4 py-3 border-b border-border">
                        <h2 class="font-semibold text-text">{$_("folders_report")}</h2>
                    </div>
                    {#if folderReports.length === 0}
                        <div class="p-4"><EmptyState title={$_("no_records_found")} /></div>
                    {:else}
                        <ul class="divide-y divide-border">
                            {#each folderReports as [folder, report] (folder)}
                                <li class="px-4 py-3">
                                    <div class="flex flex-wrap items-center gap-2">
                                        <span class="font-mono text-sm text-text" dir="ltr">{folder}</span>
                                        <span class="flex items-center gap-1.5 ms-auto">
                                            {#if report.valid_entries}
                                                <Badge variant="success" size="sm">
                                                    {$_("valid_entries", { values: { count: formatNumber(report.valid_entries) } })}
                                                </Badge>
                                            {/if}
                                            {#if report.invalid_entries?.length}
                                                <Badge variant="danger" size="sm">
                                                    {$_("invalid_entries", { values: { count: formatNumber(report.invalid_entries.length) } })}
                                                </Badge>
                                            {/if}
                                        </span>
                                    </div>
                                    {#if report.invalid_entries?.length}
                                        <ul class="mt-2 flex flex-wrap gap-1.5">
                                            {#each report.invalid_entries as invalid (invalid.shortname)}
                                                <li>
                                                    <button
                                                        type="button"
                                                        class="px-2 py-1 rounded-control border border-danger/30 bg-danger-soft text-danger text-xs font-mono hover:bg-danger/10 cursor-pointer"
                                                        onclick={() => handleErrorEntryClick(invalid, folder)}
                                                    >
                                                        {invalid.shortname}
                                                    </button>
                                                </li>
                                            {/each}
                                        </ul>
                                    {/if}
                                </li>
                            {/each}
                        </ul>
                    {/if}
                </Card>

                {#if body.invalid_meta_folders}
                    <Card padding="none">
                        <div class="flex items-center justify-between px-4 py-3 border-b border-border">
                            <h2 class="font-semibold text-text">{$_("invalid_meta_folders")}</h2>
                            <Badge variant={body.invalid_meta_folders.length ? "danger" : "success"}>
                                {formatNumber(body.invalid_meta_folders.length)}
                            </Badge>
                        </div>
                        {#if body.invalid_meta_folders.length}
                            <ul class="divide-y divide-border text-sm">
                                {#each body.invalid_meta_folders as entry (entry)}
                                    <li class="px-4 py-2 font-mono" dir="ltr">{entry}</li>
                                {/each}
                            </ul>
                        {/if}
                    </Card>
                {/if}
            </div>
        {:catch error}
            <ErrorState title={$_("entry_load_failed")} {error} onRetry={() => attempt++} />
        {/await}
    {/if}
</div>
