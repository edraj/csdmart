<script lang="ts">
    import { onMount, untrack } from "svelte";
    import { Dmart, headers, QueryType, type ApiResponseRecord, type QueryRequest } from "@edraj/tsdmart";
    import { Button, Label, Select, Spinner } from "flowbite-svelte";
    import { FileExportOutline } from "flowbite-svelte-icons";
    import downloadFile from "@/utils/downloadFile";
    import { Level, showToast } from "@/utils/toast";
    import { errorMessage } from "@/utils/errorMessage";
    import { getChildren, getChildrenAndSubChildren } from "@/lib/dmart_services";
    import { createBaseQuery } from "@/utils/routes/queryHelpers";
    import { _ } from "@/i18n";
    import Prism from "@/components/Prism.svelte";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import TransferLog, { type TransferEvent } from "@/components/management/tools/TransferLog.svelte";

    let spaces: ApiResponseRecord[] = $state([]);
    let spacesError: unknown = $state(null);
    let space_name: string = $state("");
    let subpath: string = $state("/");

    let response: unknown = $state(null);
    let previewError: unknown = $state(null);
    let isPreviewing: boolean = $state(false);
    let isExporting: boolean = $state(false);
    let exportEvents: TransferEvent[] = $state([]);

    let subpaths: string[] = $state([]);

    async function loadSpaces() {
        spacesError = null;
        try {
            spaces = (await Dmart.getSpaces())?.records ?? [];
        } catch (e: unknown) {
            spacesError = e;
            showToast(Level.warn, errorMessage(e, $_("spaces_load_failed")));
        }
    }

    onMount(loadSpaces);

    // Monotonic id so a slow walk of the previous space cannot land after a
    // fast switch to the next one and populate the dropdown with its folders.
    let subpathsSeq = 0;
    async function loadSubpaths(target: string) {
        const seq = ++subpathsSeq;
        subpaths = [];
        try {
            const roots = await getChildren(target, "/", 100);
            const collected: string[] = [];
            // Walks with the FULL path of each folder — the old copy here passed
            // only the shortname, so anything two levels deep resolved wrongly.
            await getChildrenAndSubChildren(collected, target, "", roots);
            if (seq !== subpathsSeq) return;
            subpaths = collected.sort();
        } catch (e: unknown) {
            if (seq !== subpathsSeq) return;
            showToast(Level.warn, errorMessage(e, $_("subpaths_load_failed")));
        }
    }

    function buildQuery(): QueryRequest {
        return {
            type: QueryType.search,
            exact_subpath: false,
            ...createBaseQuery({
                space_name,
                subpath,
                search: "",
                offset: 0,
                limit: 1_000_000,
                retrieve_json_payload: true,
                retrieve_attachments: true,
            }),
        } as QueryRequest;
    }

    async function handlePreview() {
        if (!space_name || !subpath) return;
        isPreviewing = true;
        previewError = null;
        try {
            response = await Dmart.query(buildQuery());
        } catch (e: unknown) {
            previewError = e;
            // Keep the server's envelope in the result pane as well.
            response = (e as { response?: { data?: unknown } })?.response?.data ?? null;
        } finally {
            isPreviewing = false;
        }
    }

    function record(status: TransferEvent["status"], filename: string, bytes: number | null, startedAt: number) {
        exportEvents = [
            { id: crypto.randomUUID(), at: new Date(), status, filename, bytes, durationMs: Date.now() - startedAt },
            ...exportEvents,
        ];
    }

    async function handleDownload() {
        if (!space_name || !subpath) return;
        isExporting = true;
        const fileName = `${space_name}/${subpath}.zip`;
        const startTime = Date.now();

        try {
            const res = await Dmart.axiosDmartInstance.post("managed/export", buildQuery(), {
                headers,
                responseType: "arraybuffer",
            });
            if (res.status !== 200) {
                showToast(Level.warn, $_("export_failed"));
                record("error", fileName, null, startTime);
            } else {
                const bytes = (res.data as ArrayBuffer).byteLength;
                downloadFile(res.data, fileName, "application/zip");
                record("success", fileName, bytes, startTime);
            }
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, $_("export_failed")));
            record("error", fileName, null, startTime);
        } finally {
            isExporting = false;
        }
    }

    $effect(() => {
        const target = space_name;
        if (!target) return;
        untrack(() => {
            void loadSubpaths(target);
        });
    });
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title={$_("export")}
        description={$_("export_description")}
        icon={FileExportOutline}
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    />

    <div class="space-y-4">
        {#if spacesError}
            <ErrorState compact title={$_("spaces_load_failed")} error={spacesError} onRetry={loadSpaces} />
        {/if}

        <Card>
            <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                    <Label for="space_name" class="mb-2">{$_("space_name")}</Label>
                    <Select id="space_name" bind:value={space_name} disabled={isExporting}>
                        <option value="" disabled>{$_("select_space")}</option>
                        {#each spaces as space (space.shortname)}
                            <option value={space.shortname}>{space.shortname}</option>
                        {/each}
                    </Select>
                </div>
                <div>
                    <Label for="subpath" class="mb-2">{$_("subpath")}</Label>
                    <Select id="subpath" bind:value={subpath} disabled={isExporting}>
                        <option value="/">/</option>
                        {#each subpaths as path (path)}
                            <option value={path}>{path}</option>
                        {/each}
                    </Select>
                </div>
            </div>
            <div class="flex flex-wrap items-center justify-end gap-2 mt-5">
                <Button color="alternative" onclick={handlePreview} disabled={isExporting || isPreviewing || !space_name}>
                    {#if isPreviewing}<Spinner class="me-2" size="4" />{/if}
                    {$_("preview")}
                </Button>
                <Button color="primary" onclick={handleDownload} disabled={isExporting || !space_name}>
                    {#if isExporting}
                        <Spinner class="me-2" size="4" />
                        {$_("exporting")}
                    {:else}
                        {$_("download_zip")}
                    {/if}
                </Button>
            </div>
        </Card>

        <TransferLog title={$_("export_log")} events={exportEvents} />

        {#if previewError}
            <ErrorState compact title={$_("query_failed")} error={previewError} />
        {/if}
        {#if response === null}
            <p class="text-sm text-text-muted text-center py-6">{$_("no_response_yet")}</p>
        {:else}
            <Prism code={response as object} />
        {/if}
    </div>
</div>
