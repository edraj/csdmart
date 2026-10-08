<script lang="ts">
    import { Button, Label, Modal, Select, Spinner } from "flowbite-svelte";
    import { Dmart, RequestType, type ActionRequestRecord, type ResourceType } from "@edraj/tsdmart";
    import { Level, showToast } from "@/utils/toast";
    import { currentListView } from "@/stores/global";
    import { bulkBucket } from "@/stores/management/bulk_bucket";
    import { spaces } from "@/stores/management/spaces";
    import { getChildren, getChildrenAndSubChildren, getSpaces } from "@/lib/dmart_services";
    import { recordSubpath } from "@/utils/subpath";
    import { errorMessage } from "@/utils/errorMessage";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { _ } from "@/i18n";

    let {
        space_name,
        subpath,
        isOpen = $bindable(false),
        actionType = "move", // "move" or "copy"
    }: {
        space_name: string;
        subpath: string;
        isOpen: boolean;
        actionType: "move" | "copy";
    } = $props();

    const uid = $props.id();
    const isMove = $derived(actionType === "move");

    let selectedSpace = $state("");
    let selectedSubpath = $state("/");
    let isActionLoading = $state(false);
    let isLoadingSubpaths = $state(false);
    let subpathOptions = $state([{ name: "/", value: "/" }]);

    $effect(() => {
        if (isOpen) {
            selectedSpace = space_name;
            if ($spaces === null) {
                getSpaces().catch(() => {
                    /* the select stays on the current space */
                });
            }
        }
    });

    $effect(() => {
        if (isOpen && selectedSpace) {
            selectedSubpath = "/";
            fetchSubpaths(selectedSpace);
        }
    });

    async function fetchSubpaths(space: string) {
        if (!space) return;
        isLoadingSubpaths = true;
        try {
            const response = await getChildren(space, "/", 100);
            const options = [{ name: "/", value: "/" }];

            const subpaths: string[] = [];
            await getChildrenAndSubChildren(subpaths, space, "", response);
            subpaths.sort();

            subpaths.forEach((path) => {
                options.push({ name: path, value: path });
            });

            subpathOptions = options;
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, $_("subpaths_load_failed")));
            subpathOptions = [{ name: "/", value: "/" }];
        } finally {
            isLoadingSubpaths = false;
        }
    }

    async function handleBulkAction() {
        if (!$bulkBucket.length) return;

        isActionLoading = true;
        try {
            // A list record names its type as a plain string; the request wants the enum.
            const records: ActionRequestRecord[] = [];

            $bulkBucket.forEach((b) => {
                if (isMove) {
                    // The record's own subpath, not the list's: on a non-exact
                    // list the selected rows may live in several subpaths.
                    const srcSubpath = recordSubpath(b, subpath);

                    const moveAttrb = {
                        src_space_name: space_name,
                        src_subpath: srcSubpath,
                        src_shortname: b.shortname,

                        dest_space_name: selectedSpace,
                        dest_subpath: selectedSubpath,
                        dest_shortname: b.shortname,
                    };

                    records.push({
                        resource_type: b.resource_type as ResourceType,
                        shortname: b.shortname,
                        subpath: srcSubpath,
                        attributes: moveAttrb,
                    });
                } else {
                    const attrs = { ...b.attributes };
                    if ("uuid" in attrs) delete attrs.uuid;

                    records.push({
                        resource_type: b.resource_type as ResourceType,
                        shortname: b.shortname,
                        subpath: selectedSubpath,
                        attributes: attrs,
                    });
                }
            });

            const requestType = isMove ? RequestType.move : RequestType.create;
            const targetSpace = isMove ? space_name : selectedSpace;

            const response = await Dmart.request({
                space_name: targetSpace,
                request_type: requestType,
                records: records,
            });

            if (response?.status === "success") {
                showToast(Level.info, isMove ? $_("entries_moved") : $_("entries_copied"));
                await $currentListView?.fetchPageRecords();
                bulkBucket.set([]);
                isOpen = false;
            } else {
                showToast(Level.warn, isMove ? $_("entries_move_failed") : $_("entries_copy_failed"));
            }
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, isMove ? $_("entries_move_failed") : $_("entries_copy_failed")));
        } finally {
            isActionLoading = false;
        }
    }

    const spaceOptions = $derived($spaces?.map((s) => ({ name: s.shortname, value: s.shortname })) ?? []);
</script>

<Modal
    bind:open={isOpen}
    size="md"
    title={isMove
        ? $_("bulk_move_n", { values: { count: $bulkBucket.length } })
        : $_("bulk_copy_n", { values: { count: $bulkBucket.length } })}
    class="rounded-modal shadow-modal"
>
    <div class="space-y-4">
        <div>
            <Label for="{uid}-space" class="mb-1.5">{$_("destination_space")}</Label>
            <Select id="{uid}-space" items={spaceOptions} bind:value={selectedSpace} />
        </div>

        <div>
            <Label for="{uid}-subpath" class="mb-1.5">{$_("destination_subpath")}</Label>
            {#if isLoadingSubpaths}
                <LoadingState variant="skeleton" rows={1} />
            {:else}
                <Select id="{uid}-subpath" items={subpathOptions} bind:value={selectedSubpath} />
            {/if}
        </div>
    </div>

    <div class="flex items-center justify-end gap-2 mt-6">
        <Button color="alternative" onclick={() => (isOpen = false)} disabled={isActionLoading}>{$_("cancel")}</Button>
        <Button color="primary" onclick={handleBulkAction} disabled={isActionLoading || isLoadingSubpaths}>
            {#if isActionLoading}
                <Spinner size="4" class="me-2" />
                {isMove ? $_("moving") : $_("copying")}
            {:else}
                {isMove ? $_("move") : $_("copy")}
            {/if}
        </Button>
    </div>
</Modal>
