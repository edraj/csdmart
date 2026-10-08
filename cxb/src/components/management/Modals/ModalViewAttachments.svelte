<script lang="ts">
    import { Modal } from "flowbite-svelte";
    import Media from "@/components/management/renderers/Media.svelte";
    import { Dmart, ResourceType } from "@edraj/tsdmart";
    import { getFileExtension } from "@/utils/getFileExtension";
    import { localizedText } from "@/utils/localized";
    import { _ } from "@/i18n";

    let {
        space_name,
        subpath,
        parent_resource_type,
        parent_shortname,
        openViewContentModal = $bindable(),
        selectedAttachment,
    }: {
        space_name: string;
        subpath: string;
        parent_resource_type: string;
        parent_shortname: string;
        openViewContentModal: boolean;
        selectedAttachment: any;
    } = $props();

    function getModalSize(contentType: string | undefined): "md" | "lg" | "xl" {
        if (!contentType) return "md";
        if (contentType.includes("image") || contentType.includes("video") || contentType.includes("pdf")) return "xl";
        if (contentType === "markdown" || contentType === "html") return "lg";
        return "md";
    }

    const contentType = $derived<string | undefined>(selectedAttachment?.attributes?.payload?.content_type);
    const modalSize = $derived(getModalSize(contentType));
    const isPdf = $derived(contentType?.includes("pdf") ?? false);
    const title = $derived(
        localizedText(selectedAttachment?.attributes?.displayname, selectedAttachment?.shortname ?? $_("attachment_content")),
    );
    const mediaUrl = $derived(
        selectedAttachment
            ? Dmart.getAttachmentUrl({
                  resource_type: selectedAttachment.resource_type,
                  space_name,
                  subpath,
                  parent_shortname: parent_resource_type === ResourceType.folder ? "" : parent_shortname,
                  shortname: selectedAttachment.shortname,
                  ext: getFileExtension(selectedAttachment.attributes?.payload?.body),
              })
            : "",
    );
</script>

<Modal
    size={modalSize}
    {title}
    classes={{ body: isPdf ? "p-0 h-auto" : "p-4 overflow-auto" }}
    bind:open={openViewContentModal}
    autoclose={false}
    placement="center"
    class="rounded-modal shadow-modal"
>
    {#if selectedAttachment}
        <div class={isPdf ? "pdf-container" : "media-container"}>
            <Media
                resource_type={ResourceType[selectedAttachment.resource_type as keyof typeof ResourceType]}
                attributes={selectedAttachment.attributes}
                displayname={selectedAttachment.shortname}
                url={mediaUrl}
            />
        </div>
    {/if}
</Modal>

<style>
    .media-container {
        min-height: 200px;
        width: 100%;
        display: flex;
        justify-content: center;
        overflow: auto;
        max-height: 80vh;
    }

    .pdf-container {
        width: 100%;
        display: flex;
        justify-content: center;
        padding: 0;
        height: 80vh;
    }
</style>
