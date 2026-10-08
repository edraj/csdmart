<script lang="ts">
    import { ContentType, Dmart, RequestType, ResourceType } from "@edraj/tsdmart";
    import { Level, showToast } from "@/utils/toast";
    import { Button, Dropdown, DropdownItem, Modal } from "flowbite-svelte";
    import {
        DotsHorizontalOutline,
        EyeOutline,
        FileCsvOutline,
        FileImageOutline,
        FileLinesOutline,
        FileLinesSolid,
        FileMusicSolid,
        FileOutline,
        FileVideoSolid,
        ListOutline,
        PenOutline,
        TrashBinOutline,
        UploadOutline,
    } from "flowbite-svelte-icons";
    import ModalViewAttachments from "@/components/management/Modals/ModalViewAttachments.svelte";
    import { getFileExtension } from "@/utils/getFileExtension";
    import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import IconButton from "@/components/ui/IconButton.svelte";
    import Lazy from "@/components/ui/Lazy.svelte";
    import LazyJsonEditor from "@/components/ui/LazyJsonEditor.svelte";
    import { formatDate } from "@/utils/format";
    import { localizedText } from "@/utils/localized";
    import { _ } from "@/i18n";

    let {
        attachments = {},
        resource_type,
        space_name,
        subpath,
        parent_shortname,
        refreshEntry,
    }: {
        attachments: Record<string, unknown>;
        resource_type: ResourceType;
        space_name: string;
        subpath: string;
        parent_shortname: string;
        refreshEntry: () => Promise<void> | void;
    } = $props();

    // Element ids for the per-card menus must be unique per instance.
    const uid = $props.id();

    // ── Grouping by content type ────────────────────────────────────────────
    const allAttachments = $derived((Object.values(attachments ?? {}) as any[][]).flat(1));
    const contentTypeGroups = $derived.by(() => {
        const groups: Record<string, any[]> = {};
        for (const attachment of allAttachments) {
            let contentType = "other";
            if (attachment.resource_type === ResourceType.media && attachment.attributes?.payload?.content_type) {
                contentType = attachment.attributes.payload.content_type;
            } else if (attachment.resource_type === ResourceType.csv) {
                contentType = "csv";
            } else if (attachment.resource_type === ResourceType.json) {
                contentType = "json";
            } else if (attachment.resource_type === ResourceType.comment) {
                contentType = "comment";
            }
            (groups[contentType] ??= []).push(attachment);
        }
        return groups;
    });
    let selectedFilter = $state("all");
    const filteredAttachments = $derived(
        selectedFilter === "all" ? allAttachments : (contentTypeGroups[selectedFilter] ?? allAttachments),
    );

    function attachmentKey(attachment: any): string {
        return attachment.uuid ?? `${attachment.resource_type}:${attachment.shortname}`;
    }

    // ── View meta ───────────────────────────────────────────────────────────
    let openViewAttachmentModal = $state(false);
    let metaContent = $state<{ json: unknown; text?: undefined }>({ json: {} });
    function viewMeta(attachment: any) {
        selectedAttachment = attachment;
        metaContent = { json: attachment, text: undefined };
        openViewAttachmentModal = true;
    }

    // ── View content ────────────────────────────────────────────────────────
    let openViewContentModal = $state(false);
    function viewContent(attachment: any) {
        selectedAttachment = attachment;
        openViewContentModal = true;
    }

    // ── Create / edit ───────────────────────────────────────────────────────
    let isModalInUpdateMode = $state(false);
    let openCreateAttachmentModal = $state(false);
    let selectedAttachment: any = $state(null);
    let createMetaContent = $state({});
    let createPayloadContent = $state({});

    function editAttachment(attachment: any) {
        selectedAttachment = attachment;
        isModalInUpdateMode = true;
        openCreateAttachmentModal = true;
    }

    function addAttachment() {
        isModalInUpdateMode = false;
        selectedAttachment = null;
        createMetaContent = {};
        createPayloadContent = {};
        openCreateAttachmentModal = true;
    }

    // ── Delete ──────────────────────────────────────────────────────────────
    let openDeleteModal = $state(false);
    let isDeleteLoading = $state(false);
    let deleteError: unknown = $state(null);

    function confirmDelete(attachment: any) {
        selectedAttachment = attachment;
        deleteError = null;
        openDeleteModal = true;
    }

    async function handleDelete() {
        const item = selectedAttachment;
        if (!item) return;
        const request_dict = {
            space_name,
            request_type: RequestType.delete,
            records: [
                {
                    resource_type: item.resource_type as ResourceType,
                    shortname: item.shortname,
                    subpath: `${item.subpath}/${parent_shortname}`,
                    attributes: {},
                },
            ],
        };
        try {
            isDeleteLoading = true;
            deleteError = null;
            const response = await Dmart.request(request_dict);
            if (response.status === "success") {
                showToast(Level.info, $_("attachment_deleted", { values: { shortname: item.shortname } }));
                await refreshEntry();
                openDeleteModal = false;
            } else {
                showToast(Level.warn);
                deleteError = response;
            }
        } catch (error: unknown) {
            deleteError = error;
            showToast(Level.warn);
        } finally {
            isDeleteLoading = false;
        }
    }

    function handleRenderMenu(items: any[]) {
        items = items.filter((item) => !["tree", "text", "table"].includes(item.text));
        const itemsWithoutSpace = items.slice(0, items.length - 2);
        return itemsWithoutSpace.concat([{ separator: true }, { space: true }]);
    }

    const chipBase =
        "inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs font-medium cursor-pointer transition-colors whitespace-nowrap";
    const chipOn = "border-primary bg-primary text-text-on-primary";
    const chipOff = "border-border bg-surface-2 text-text-muted hover:bg-surface-3 hover:text-text";
</script>

<Modal bind:open={openViewAttachmentModal} size="lg" title={$_("attachment_metadata")} class="rounded-modal shadow-modal">
    <LazyJsonEditor onRenderMenu={handleRenderMenu} mode="text" content={metaContent} readOnly={true} />
</Modal>

<div class="flex flex-col gap-4 w-full">
    <div class="flex flex-wrap items-center justify-between gap-3">
        <div class="flex flex-wrap items-center gap-2" role="group" aria-label={$_("filter_by_type")}>
            <button
                type="button"
                class="{chipBase} {selectedFilter === 'all' ? chipOn : chipOff}"
                aria-pressed={selectedFilter === "all"}
                onclick={() => (selectedFilter = "all")}
            >
                <ListOutline size="sm" aria-hidden="true" />
                {$_("all")}
                <span class="tabular-nums opacity-80">({allAttachments.length})</span>
            </button>

            {#each Object.keys(contentTypeGroups) as contentType (contentType)}
                <button
                    type="button"
                    class="{chipBase} {selectedFilter === contentType ? chipOn : chipOff}"
                    aria-pressed={selectedFilter === contentType}
                    onclick={() => (selectedFilter = contentType)}
                >
                    {#if contentType === ContentType.image}
                        <FileImageOutline size="sm" aria-hidden="true" />
                    {:else if contentType === ContentType.audio}
                        <FileMusicSolid size="sm" aria-hidden="true" />
                    {:else if contentType === ContentType.video}
                        <FileVideoSolid size="sm" aria-hidden="true" />
                    {:else if contentType === ContentType.text || contentType === ContentType.markdown || contentType === ContentType.html}
                        <FileLinesSolid size="sm" aria-hidden="true" />
                    {:else if contentType === "csv"}
                        <FileCsvOutline size="sm" aria-hidden="true" />
                    {:else}
                        <FileOutline size="sm" aria-hidden="true" />
                    {/if}
                    {contentType}
                    <span class="tabular-nums opacity-80">({contentTypeGroups[contentType]?.length || 0})</span>
                </button>
            {/each}
        </div>

        <Button size="sm" color="primary" onclick={addAttachment}>
            <UploadOutline size="sm" class="me-1.5" aria-hidden="true" />
            {$_("upload")}
        </Button>
    </div>

    {#if filteredAttachments.length === 0}
        <EmptyState title={$_("no_attachments")} hint={$_("no_attachments_hint")}>
            <Button size="sm" color="alternative" onclick={addAttachment}>
                <UploadOutline size="sm" class="me-1.5" aria-hidden="true" />
                {$_("upload")}
            </Button>
        </EmptyState>
    {:else}
        <ul class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4 w-full" role="list">
            {#each filteredAttachments as attachment, index (attachmentKey(attachment))}
                {@const name = localizedText(attachment.attributes?.displayname, attachment.shortname)}
                {@const description = localizedText(attachment.attributes?.description, "")}
                {@const contentType = attachment.attributes?.payload?.content_type}
                <li class="relative flex flex-col rounded-card border border-border bg-surface-2 shadow-card p-4">
                    <div class="flex items-start justify-between gap-2">
                        <span
                            class="inline-flex items-center justify-center w-10 h-10 rounded-control bg-primary-soft text-primary"
                            aria-hidden="true"
                        >
                            {#if attachment.resource_type === ResourceType.media}
                                {#if contentType === ContentType.image}
                                    <FileImageOutline size="lg" />
                                {:else if contentType === ContentType.audio}
                                    <FileMusicSolid size="lg" />
                                {:else if contentType === ContentType.video}
                                    <FileVideoSolid size="lg" />
                                {:else if [ContentType.text, ContentType.markdown, ContentType.html].includes(contentType)}
                                    <FileLinesSolid size="lg" />
                                {:else}
                                    <FileOutline size="lg" />
                                {/if}
                            {:else if attachment.resource_type === ResourceType.csv}
                                <FileCsvOutline size="lg" />
                            {:else if [ResourceType.comment, ResourceType.json].includes(attachment.resource_type)}
                                <FileLinesOutline size="lg" />
                            {:else}
                                <FileOutline size="lg" />
                            {/if}
                        </span>

                        <!-- The menu trigger and the dropdown are siblings, never
                             one interactive element inside another. -->
                        <div class="flex items-center gap-1">
                            {#if attachment.resource_type !== ResourceType.reaction}
                                <IconButton
                                    label={$_("view_content")}
                                    variant="outline"
                                    size="sm"
                                    onclick={() => viewContent(attachment)}
                                >
                                    <EyeOutline size="sm" />
                                </IconButton>
                            {/if}
                            <IconButton
                                id="{uid}-menu-{index}"
                                label={$_("attachment_actions", { values: { name } })}
                                variant="outline"
                                size="sm"
                            >
                                <DotsHorizontalOutline size="sm" />
                            </IconButton>
                            <Dropdown simple triggeredBy="#{uid}-menu-{index}" class="min-w-44">
                                <DropdownItem onclick={() => viewMeta(attachment)}>
                                    <span class="flex items-center gap-2">
                                        <EyeOutline size="sm" aria-hidden="true" /> {$_("view_metadata")}
                                    </span>
                                </DropdownItem>
                                <DropdownItem onclick={() => editAttachment(attachment)}>
                                    <span class="flex items-center gap-2">
                                        <PenOutline size="sm" aria-hidden="true" /> {$_("edit")}
                                    </span>
                                </DropdownItem>
                                <DropdownItem onclick={() => confirmDelete(attachment)}>
                                    <span class="flex items-center gap-2 text-danger">
                                        <TrashBinOutline size="sm" aria-hidden="true" /> {$_("delete")}
                                    </span>
                                </DropdownItem>
                            </Dropdown>
                        </div>
                    </div>

                    <div class="mt-3 min-w-0">
                        {#if attachment.resource_type === ResourceType.media}
                            <a
                                class="font-semibold text-base text-primary hover:underline break-words"
                                href={Dmart.getAttachmentUrl({
                                    resource_type: attachment.resource_type,
                                    space_name,
                                    subpath,
                                    parent_shortname: resource_type === ResourceType.folder ? "" : parent_shortname,
                                    shortname: attachment.shortname,
                                    ext: getFileExtension(attachment.attributes?.payload?.body),
                                })}
                                target="_blank"
                                rel="noopener noreferrer"
                            >
                                {name}
                            </a>
                        {:else}
                            <p class="font-semibold text-base text-text break-words">{name}</p>
                        {/if}
                        {#if description}
                            <p class="mt-1 text-sm text-text-muted line-clamp-3">{description}</p>
                        {/if}
                    </div>

                    <dl class="mt-auto pt-3 text-xs text-text-faint space-y-0.5">
                        <div class="flex gap-1">
                            <dt>{$_("type")}:</dt>
                            <dd class="text-text-muted">{attachment.resource_type} ({contentType ?? $_("not_applicable")})</dd>
                        </div>
                        <div class="flex gap-1 tabular-nums">
                            <dt>{$_("updated")}:</dt>
                            <dd class="text-text-muted">{formatDate(attachment?.attributes?.updated_at, "date") || $_("not_applicable")}</dd>
                        </div>
                    </dl>
                </li>
            {/each}
        </ul>
    {/if}
</div>

<ModalViewAttachments
    bind:openViewContentModal
    {selectedAttachment}
    {space_name}
    {subpath}
    parent_resource_type={resource_type}
    {parent_shortname}
/>

{#if openCreateAttachmentModal}
    <Lazy load={() => import("@/components/management/Modals/ModalCreateAttachments.svelte")} pending="none">
        {#snippet children(ModalCreateAttachments)}
            <ModalCreateAttachments
                bind:isOpen={openCreateAttachmentModal}
                isUpdateMode={isModalInUpdateMode}
                {selectedAttachment}
                parentResourceType={resource_type}
                {space_name}
                {subpath}
                {parent_shortname}
                bind:meta={createMetaContent}
                bind:payload={createPayloadContent}
                {refreshEntry}
            />
        {/snippet}
    </Lazy>
{/if}

<ConfirmDialog
    bind:open={openDeleteModal}
    variant="danger"
    title={$_("delete_attachment")}
    body={`${$_("confirm_deleting_attachment", { values: { shortname: selectedAttachment?.shortname ?? "" } })}\n${$_("cannot_be_undone")}`}
    loading={isDeleteLoading}
    loadingLabel={$_("deleting")}
    error={deleteError}
    onConfirm={handleDelete}
/>
