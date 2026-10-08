<script lang="ts">
    import { Button, Fileupload, Label, Modal, Select, Spinner, Textarea } from "flowbite-svelte";
    import {
        ContentType,
        Dmart,
        QueryType,
        RequestType,
        ResourceAttachmentType,
        ResourceType,
        type ActionRequest,
        type ActionRequestRecord,
    } from "@edraj/tsdmart";
    import LazyJsonEditor from "@/components/ui/LazyJsonEditor.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import HtmlEditor from "@/components/management/editors/HtmlEditor.svelte";
    import MarkdownEditor from "@/components/management/editors/MarkdownEditor.svelte";
    import { Level, showToast } from "@/utils/toast";
    import { jsonEditorContentParser } from "@/utils/jsonEditor";
    import Prism from "@/components/Prism.svelte";
    import { removeEmpty } from "@/utils/compare";
    import MetaForm from "@/components/management/forms/MetaForm.svelte";
    import { untrack } from "svelte";
    import { errorMessage } from "@/utils/errorMessage";
    import { limitJsonForDisplay } from "@/utils/displayJson";
    import { _ } from "@/i18n";

    let {
        meta = $bindable({}),
        payload = $bindable({}),
        isOpen = $bindable(false),
        isUpdateMode = $bindable(false),
        selectedAttachment = $bindable(null),
        space_name = $bindable(""),
        parentResourceType,
        subpath = $bindable(""),
        parent_shortname = $bindable(""),
        refreshEntry,
    }: {
        meta?: any;
        payload?: any;
        isOpen?: boolean;
        isUpdateMode?: boolean;
        selectedAttachment?: any;
        space_name?: string;
        parentResourceType: ResourceType;
        subpath?: string;
        parent_shortname?: string;
        refreshEntry?: () => Promise<void> | void;
    } = $props();

    const uid = $props.id();

    let resourceType = $state<ResourceAttachmentType>(ResourceAttachmentType.media);
    let contentType = $state<ContentType>(ContentType.image);
    let payloadFiles = $state<FileList | null>(null);
    let content: any = $state(payload);
    let selectedSchema = $state("");
    let trueResourceType = $state<ResourceAttachmentType | null>(null);
    let isLoading = $state(false);
    let errorModalMessage = $state<string | null>(null);
    let errorContent = $state<unknown>(null);
    const errorPreview = $derived(errorContent ? limitJsonForDisplay(errorContent) : null);

    $effect(() => {
        if (isOpen && selectedAttachment && isUpdateMode) {
            initializeFormWithAttachment(selectedAttachment);
        } else if (!isOpen) {
            resetModal();
        }
    });

    function initializeFormWithAttachment(attachment: any) {
        if (!attachment) return;

        const _attachment = structuredClone($state.snapshot(attachment));

        meta = {
            shortname: _attachment.shortname,
            is_active: _attachment.attributes.is_active,
            slug: _attachment.attributes.slug,
            displayname: _attachment.attributes.displayname,
            description: _attachment.attributes.description,
        };

        if (
            _attachment.resource_type === ResourceType.json ||
            (_attachment.resource_type === ResourceType.media &&
                [ContentType.text, ContentType.json, ContentType.markdown, ContentType.html].includes(
                    _attachment?.attributes?.payload?.content_type,
                )) ||
            _attachment.resource_type === ResourceType.comment
        ) {
            resourceType = ResourceAttachmentType[_attachment.resource_type as keyof typeof ResourceAttachmentType];
            contentType = _attachment?.attributes?.payload?.content_type;

            if (_attachment.resource_type === ResourceType.json) {
                content = { json: _attachment.attributes.payload.body };
            } else {
                if (typeof _attachment.attributes.payload.body === "string") {
                    content = _attachment.attributes.payload.body;
                } else {
                    content = { body: _attachment.attributes.payload.body };
                }
            }
        } else {
            trueResourceType = ResourceAttachmentType[_attachment.resource_type as keyof typeof ResourceAttachmentType];
            resourceType = trueResourceType!;

            const metaAttachment = structuredClone(_attachment);
            if (metaAttachment?.attributes?.payload?.body) {
                delete metaAttachment.attributes.payload.body;
            }
            content = { json: metaAttachment, text: undefined };
        }
    }

    function resetModal() {
        resourceType = ResourceAttachmentType.media;
        contentType = ContentType.image;
        payloadFiles = null;
        content = {};
        selectedSchema = "";
        errorModalMessage = null;
        errorContent = null;
        meta = {
            shortname: "",
            is_active: true,
            displayname: {
                en: "",
                ar: "",
                ku: "",
            },
            description: {
                en: "",
                ar: "",
                ku: "",
            },
        };
    }

    const attachmentSubpath = $derived(
        parentResourceType === ResourceType.folder ? subpath : `${subpath}/${parent_shortname}`.replaceAll("//", "/"),
    );

    const isFileUpload = $derived(
        [ResourceAttachmentType.csv, ResourceAttachmentType.jsonl, ResourceAttachmentType.sqlite, ResourceAttachmentType.parquet].includes(
            resourceType,
        ) ||
            (resourceType === ResourceAttachmentType.media &&
                [ContentType.image, ContentType.pdf, ContentType.audio, ContentType.video, ContentType.apk, ContentType.python].includes(
                    contentType,
                )),
    );

    async function upload(event: SubmitEvent) {
        event.preventDefault();
        errorModalMessage = null;
        errorContent = null;

        // The meta form is bound but lives in its own <form>, so the outer
        // submit does not run its constraint validation — ask it explicitly.
        if (typeof validateMetaForm === "function" && !validateMetaForm()) {
            errorModalMessage = $_("fill_required_meta");
            return;
        }
        if (isFileUpload && !isUpdateMode && !payloadFiles?.length) {
            errorModalMessage = $_("file_required");
            return;
        }
        isLoading = true;

        try {
            if (isUpdateMode && resourceType === ResourceAttachmentType.json && trueResourceType !== null) {
                await updateMeta();
                return;
            }
            let response;
            if (resourceType == ResourceAttachmentType.comment) {
                response = await Dmart.request({
                    space_name,
                    request_type: isUpdateMode ? RequestType.update : RequestType.create,
                    records: [
                        removeEmpty({
                            resource_type: ResourceType.comment,
                            shortname: meta.shortname,
                            subpath: `${subpath}/${parent_shortname}`.replaceAll("//", "/"),
                            attributes: {
                                slug: meta.slug,
                                displayname: meta.displayname,
                                description: meta.description,
                                is_active: true,
                                payload: {
                                    content_type: ContentType.json,
                                    body: {
                                        state: "commented",
                                        body: isUpdateMode ? content.body : content,
                                    },
                                },
                            },
                        }) as ActionRequestRecord,
                    ],
                });
            } else if (isFileUpload) {
                if (isUpdateMode === false) {
                    const isDataAsset = resourceType !== ResourceAttachmentType.media;
                    response = await Dmart.uploadWithPayload({
                        space_name,
                        subpath: attachmentSubpath,
                        shortname: meta.shortname,
                        resource_type: ResourceType[resourceType as keyof typeof ResourceType],
                        payload_file: payloadFiles![0],
                        attributes: removeEmpty({
                            slug: meta.slug,
                            displayname: meta.displayname,
                            description: meta.description,
                            is_active: true,
                            payload: isDataAsset
                                ? {
                                      content_type: ContentType[resourceType as keyof typeof ContentType],
                                      schema_shortname: selectedSchema,
                                      body: {},
                                  }
                                : { content_type: contentType, body: {} },
                        }) as Record<string, unknown>,
                    });
                } else {
                    // Only the metadata of a binary attachment can change here.
                    response = await Dmart.request({
                        space_name,
                        request_type: RequestType.update,
                        records: [
                            removeEmpty({
                                resource_type: ResourceType[resourceType as keyof typeof ResourceType],
                                shortname: meta.shortname,
                                subpath: `${subpath}/${parent_shortname}`.replaceAll("//", "/"),
                                attributes: {
                                    slug: meta.slug,
                                    displayname: meta.displayname,
                                    description: meta.description,
                                    is_active: true,
                                },
                            }) as ActionRequestRecord,
                        ],
                    });
                }
            } else {
                response = await Dmart.request({
                    space_name,
                    request_type: isUpdateMode ? RequestType.update : RequestType.create,
                    records: [
                        removeEmpty({
                            resource_type: ResourceType[resourceType as keyof typeof ResourceType],
                            shortname: meta.shortname,
                            subpath: attachmentSubpath,
                            attributes: {
                                slug: meta.slug,
                                displayname: meta.displayname,
                                description: meta.description,
                                is_active: true,
                                payload: {
                                    content_type: contentType,
                                    schema_shortname:
                                        resourceType == ResourceAttachmentType.json && selectedSchema ? selectedSchema : null,
                                    body:
                                        resourceType == ResourceAttachmentType.json
                                            ? jsonEditorContentParser($state.snapshot(content))
                                            : content,
                                },
                            },
                        }) as ActionRequestRecord,
                    ],
                });
            }

            if (response.status === "success") {
                showToast(Level.info, isUpdateMode ? $_("attachment_updated") : $_("attachment_uploaded"));
                isOpen = false;
                resetModal();
                await refreshEntry?.();
            } else {
                showToast(Level.warn);
            }
        } catch (e: unknown) {
            const errorData = (e as { response?: { data?: unknown } })?.response?.data ?? errorMessage(e, $_("something_went_wrong"));
            showToast(Level.warn, errorMessage(e, $_("something_went_wrong")));
            errorContent = errorData;
        } finally {
            isLoading = false;
        }
    }

    let validateMetaForm = $state<(() => boolean) | undefined>(undefined);

    async function updateMeta() {
        errorModalMessage = null;
        errorContent = null;
        const _payloadContent = jsonEditorContentParser($state.snapshot(content));

        _payloadContent.subpath = attachmentSubpath;
        _payloadContent.attributes.slug = meta.slug;
        _payloadContent.attributes.displayname = meta.displayname;
        _payloadContent.attributes.description = meta.description;
        const request_dict: ActionRequest = {
            space_name,
            request_type: RequestType.update,
            records: [removeEmpty(_payloadContent) as ActionRequestRecord],
        };

        try {
            const response = await Dmart.request(request_dict);
            if (response.status === "success") {
                showToast(Level.info, $_("attachment_updated"));
                isOpen = false;
                resetModal();
                await refreshEntry?.();
            } else {
                showToast(Level.warn);
            }
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, $_("attachment_update_failed")));
            errorContent = (e as { response?: { data?: unknown } })?.response?.data ?? errorMessage(e, $_("attachment_update_failed"));
        } finally {
            isLoading = false;
        }
    }

    $effect(() => {
        if (!isUpdateMode && resourceType) {
            if (resourceType === ResourceAttachmentType.json) {
                untrack(() => {
                    contentType = ContentType.json;
                    content = { json: {} };
                });
            } else if (resourceType === ResourceAttachmentType.comment) {
                untrack(() => {
                    content = "";
                });
            } else if (resourceType === ResourceAttachmentType.media) {
                untrack(() => {
                    contentType = ContentType.image;
                    content = "";
                });
            } else {
                untrack(() => {
                    content = {};
                });
            }
        }
    });

    // The schema dropdown only needs shortnames.
    const schemaOptions = Dmart.query({
        space_name,
        type: QueryType.search,
        subpath: "/schema",
        search: "",
        retrieve_json_payload: false,
        limit: 99,
    }).then((schemas) => (schemas?.records ?? []).map((e) => e.shortname));

    const fileAccept: Partial<Record<string, string>> = {
        [ContentType.image]: "image/png, image/jpeg, image/webp, image/svg+xml",
        [ContentType.pdf]: "application/pdf",
        [ContentType.apk]: ".apk",
        [ContentType.audio]: "audio/*",
        [ContentType.video]: "video/*",
        [ContentType.python]: ".py",
        [ResourceAttachmentType.csv]: ".csv",
        [ResourceAttachmentType.jsonl]: ".jsonl",
        [ResourceAttachmentType.sqlite]: ".sqlite,.sqlite3,.db,.db3,.s3db,.sl3",
        [ResourceAttachmentType.parquet]: ".parquet",
    };

    const title = $derived(
        isUpdateMode
            ? resourceType === ResourceAttachmentType.json && trueResourceType !== null
                ? $_("edit_attachment_metadata")
                : $_("edit_attachment_content")
            : $_("add_attachment"),
    );
    const submitLabel = $derived(
        isUpdateMode
            ? resourceType === ResourceAttachmentType.json && trueResourceType !== null
                ? $_("update_metadata")
                : $_("update_content")
            : $_("upload"),
    );
</script>

<Modal bind:open={isOpen} size="xl" {title} class="rounded-modal shadow-modal">
    <form onsubmit={upload} class="space-y-4">
        {#if errorModalMessage}
            <ErrorState compact message={errorModalMessage} />
        {/if}

        <MetaForm bind:formData={meta} bind:validateFn={validateMetaForm} isCreate={!isUpdateMode} />

        <div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 space-y-4">
            <div>
                <Label for="{uid}-resourceType" class="mb-1.5">{$_("attachment_type")}</Label>
                <Select id="{uid}-resourceType" bind:value={resourceType} disabled={isUpdateMode}>
                    {#each Object.values(ResourceAttachmentType).filter((type) => type !== ResourceAttachmentType.alteration) as type (type)}
                        <option value={type}>{type}</option>
                    {/each}
                </Select>
            </div>

            {#if resourceType === ResourceAttachmentType.media}
                <div>
                    <Label for="{uid}-contentType" class="mb-1.5">{$_("content_type")}</Label>
                    <Select id="{uid}-contentType" bind:value={contentType} disabled={isUpdateMode}>
                        {#each Object.values(ContentType).filter((c) => ![ContentType.json, ContentType.csv, ContentType.jsonl, ContentType.sqlite, ContentType.parquet].includes(c)) as type (type)}
                            <option value={type}>{type}</option>
                        {/each}
                    </Select>
                </div>

                {#if fileAccept[contentType]}
                    <div>
                        <Label for="{uid}-file" class="mb-1.5">{$_("file")}</Label>
                        <Fileupload id="{uid}-file" accept={fileAccept[contentType]} clearable bind:files={payloadFiles} />
                    </div>
                {:else if contentType === ContentType.markdown}
                    <MarkdownEditor bind:content />
                {:else if contentType === ContentType.html}
                    <HtmlEditor bind:content />
                {:else}
                    <div>
                        <Label for="{uid}-text" class="mb-1.5">{$_("content")}</Label>
                        <Textarea id="{uid}-text" bind:value={content} rows={8} dir="auto" />
                    </div>
                {/if}
            {:else if resourceType === ResourceAttachmentType.json}
                {#if content.json || content.text}
                    <LazyJsonEditor mode="text" bind:content />
                {/if}
            {:else if resourceType === ResourceAttachmentType.comment}
                <div>
                    <Label for="{uid}-comment" class="mb-1.5">{$_("comment")}</Label>
                    {#if isUpdateMode}
                        <Textarea id="{uid}-comment" bind:value={content.body} rows={6} dir="auto" />
                    {:else}
                        <Textarea id="{uid}-comment" bind:value={content} rows={6} dir="auto" />
                    {/if}
                </div>
            {:else if fileAccept[resourceType]}
                <div>
                    <Label for="{uid}-datafile" class="mb-1.5">{$_("file")}</Label>
                    <Fileupload id="{uid}-datafile" accept={fileAccept[resourceType]} clearable bind:files={payloadFiles} />
                </div>
                {#if resourceType === ResourceAttachmentType.csv}
                    <div>
                        <Label for="{uid}-csvSchema" class="mb-1.5">{$_("schema")}</Label>
                        <Select id="{uid}-csvSchema" bind:value={selectedSchema} disabled={isUpdateMode}>
                            <option value="">{$_("none")}</option>
                            {#await schemaOptions then schemas}
                                {#each schemas as schema (schema)}
                                    <option value={schema}>{schema}</option>
                                {/each}
                            {/await}
                        </Select>
                    </div>
                {/if}
            {/if}
        </div>

        {#if errorPreview}
            <ErrorState compact title={$_("something_went_wrong")} message={typeof errorContent === "string" ? errorContent : undefined}>
                {#if typeof errorContent !== "string"}
                    <div class="max-h-60 overflow-auto"><Prism code={errorPreview.value as object | string} language="json" /></div>
                {/if}
            </ErrorState>
        {/if}

        <div class="flex items-center justify-end gap-2 pt-4 border-t border-border">
            <Button color="alternative" onclick={() => (isOpen = false)} disabled={isLoading}>
                {$_("cancel")}
            </Button>
            <Button color="primary" type="submit" disabled={isLoading}>
                {#if isLoading}
                    <Spinner class="me-2" size="4" />
                    {isUpdateMode ? $_("updating") : $_("uploading")}
                {:else}
                    {submitLabel}
                {/if}
            </Button>
        </div>
    </form>
</Modal>
