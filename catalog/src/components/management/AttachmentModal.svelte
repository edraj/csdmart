<script lang="ts">
  import { log } from "@/lib/logger";
  import { _ } from "@/i18n";
  import Modal from "@/components/Modal.svelte";
  import { ResourceType } from "@edraj/tsdmart";
  import {
    successToastMessage,
    errorToastMessage,
  } from "@/lib/toasts_messages";
  import {
    UploadOutline,
    CheckCircleSolid,
    CloseCircleSolid,
  } from "flowbite-svelte-icons";
  import { getFileExtension } from "@shared/file-extension";
  import { isImageFile } from "@/lib/fileUtils";
  import { AUTO_UUID_RULE, resolveAutoShortname } from "@/lib/helpers";
  import { errorMessage } from "@/lib/apiError";
  import type { JsonObject } from "@/lib/types";

  interface Translation {
    en: string;
    ar: string;
    ku: string;
  }

  interface FileUploadItem {
    file: File;
    id: string;
    status: "pending" | "uploading" | "success" | "error";
    progress: number;
    errorMessage?: string;
    shortname: string;
    displayname: Translation;
    description: Translation;
    tagsInput: string; // comma-separated
  }

  function emptyTranslation(seed: string = ""): Translation {
    return { en: seed, ar: "", ku: "" };
  }

  function toTranslationPayload(t: Translation): Record<string, string> | null {
    const out: Record<string, string> = {};
    if (t.en?.trim()) out.en = t.en.trim();
    if (t.ar?.trim()) out.ar = t.ar.trim();
    if (t.ku?.trim()) out.ku = t.ku.trim();
    return Object.keys(out).length > 0 ? out : null;
  }

  function stripExt(name: string): string {
    const dot = name.lastIndexOf(".");
    return dot > 0 ? name.slice(0, dot) : name;
  }

  function parseTags(value: string): string[] {
    return value
      .split(",")
      .map((t) => t.trim())
      .filter((t) => t.length > 0);
  }

  let {
    isOpen = $bindable(false),
    space_name,
    subpath,
    parent_shortname,
    onAttachmentCreated,
  }: {
    isOpen: boolean;
    space_name: string;
    subpath: string;
    resource_type: ResourceType;
    parent_shortname: string;
    onAttachmentCreated?: () => void;
  } = $props();

  let isUploading = $state(false);
  let selectedFiles: FileUploadItem[] = $state([]);
  let fileInput: HTMLInputElement | null = $state(null);

  function generateId(): string {
    return Math.random().toString(36).substring(2, 9);
  }

  function resetForm() {
    selectedFiles = [];
    if (fileInput) {
      fileInput.value = "";
    }
  }

  function handleFileSelect(event: Event) {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      const newFiles: FileUploadItem[] = Array.from(input.files).map(
        (file) => ({
          file,
          id: generateId(),
          status: "pending",
          progress: 0,
          shortname: "",
          displayname: emptyTranslation(stripExt(file.name)),
          description: emptyTranslation(""),
          tagsInput: "",
        }),
      );
      selectedFiles = [...selectedFiles, ...newFiles];
    }
    // Reset input so same files can be selected again
    if (fileInput) {
      fileInput.value = "";
    }
  }

  function updateFileShortname(id: string, value: string) {
    selectedFiles = selectedFiles.map((f) =>
      f.id === id ? { ...f, shortname: value } : f,
    );
  }

  function updateFileTagsInput(id: string, value: string) {
    selectedFiles = selectedFiles.map((f) =>
      f.id === id ? { ...f, tagsInput: value } : f,
    );
  }

  function updateFileTranslation(
    id: string,
    field: "displayname" | "description",
    lang: "en" | "ar" | "ku",
    value: string,
  ) {
    selectedFiles = selectedFiles.map((f) =>
      f.id === id
        ? { ...f, [field]: { ...f[field], [lang]: value } }
        : f,
    );
  }

  function removeFile(id: string) {
    selectedFiles = selectedFiles.filter((f) => f.id !== id);
  }

  function getFileThumbnailUrl(file: File): string | null {
    if (isImageFile(file.name)) {
      return URL.createObjectURL(file);
    }
    return null;
  }

  function getFileIcon(file: File): string {
    const ext = getFileExtension(file.name).toLowerCase();
    const iconMap: Record<string, string> = {
      pdf: "📄",
      doc: "📝",
      docx: "📝",
      xls: "📊",
      xlsx: "📊",
      ppt: "📽️",
      pptx: "📽️",
      zip: "📦",
      rar: "📦",
      mp3: "🎵",
      mp4: "🎬",
      avi: "🎬",
      mov: "🎬",
      txt: "📃",
    };
    return iconMap[ext] || "📎";
  }

  async function handleFileUpload() {
    if (selectedFiles.length === 0) {
      errorToastMessage($_("attachment_modal.no_files_selected"));
      return;
    }

    // Validate required props
    if (!space_name || !parent_shortname) {
      errorToastMessage("Missing required parameters for upload");
      return;
    }

    isUploading = true;

    let successCount = 0;
    let errorCount = 0;

    // Clean subpath for upload (remove leading slash)
    let cleanSubpath = subpath
      ? subpath.startsWith("/")
        ? subpath.substring(1)
        : subpath
      : "";
    if (cleanSubpath === "__root__" || cleanSubpath === "/") {
      cleanSubpath = "";
    }
    const targetSubpath = cleanSubpath
      ? `${cleanSubpath}/${parent_shortname}`
      : parent_shortname;

    // Upload files one at a time
    for (let i = 0; i < selectedFiles.length; i++) {
      const fileItem = selectedFiles[i];

      // Skip already uploaded files
      if (fileItem.status === "success") continue;

      // Update status to uploading
      selectedFiles = selectedFiles.map((f) =>
        f.id === fileItem.id ? { ...f, status: "uploading", progress: 0 } : f,
      );

      try {
        // Create a new File object to ensure proper serialization
        const file = fileItem.file;

        // Direct upload using Dmart.uploadWithPayload with proper payload structure
        const { Dmart } = await import("@edraj/tsdmart");
        const tags = parseTags(fileItem.tagsInput);
        const displaynamePayload = toTranslationPayload(fileItem.displayname);
        const descriptionPayload = toTranslationPayload(fileItem.description);
        const attributes: JsonObject = {
          is_active: true,
          ...(displaynamePayload ? { displayname: displaynamePayload } : {}),
          ...(descriptionPayload ? { description: descriptionPayload } : {}),
          ...(tags.length > 0 ? { tags } : {}),
        };
        const trimmedShortname = fileItem.shortname?.trim();
        const attachmentShortname =
          trimmedShortname && trimmedShortname.length > 0
            ? trimmedShortname
            : resolveAutoShortname(AUTO_UUID_RULE, attributes).shortname;
        const response = await Dmart.uploadWithPayload({
          space_name: space_name,
          subpath: targetSubpath,
          shortname: attachmentShortname,
          resource_type: ResourceType.media,
          payload_file: file,
          attributes,
        });

        if (response && response.status === "success") {
          selectedFiles = selectedFiles.map((f) =>
            f.id === fileItem.id
              ? { ...f, status: "success", progress: 100 }
              : f,
          );
          successCount++;
        } else {
          selectedFiles = selectedFiles.map((f) =>
            f.id === fileItem.id
              ? { ...f, status: "error", errorMessage: "Upload failed" }
              : f,
          );
          errorCount++;
        }
      } catch (error) {
        const errorMsg = errorMessage(error, "Upload error");
        selectedFiles = selectedFiles.map((f) =>
          f.id === fileItem.id
            ? { ...f, status: "error", errorMessage: errorMsg }
            : f,
        );
        errorCount++;
        log.error(`Error uploading file ${fileItem.file.name}:`, error);
      }
    }

    isUploading = false;

    // Show summary toast
    if (successCount > 0 && errorCount === 0) {
      successToastMessage(
        $_("attachment_modal.all_upload_success", {
          values: { count: successCount },
        }),
      );
      if (onAttachmentCreated) {
        onAttachmentCreated();
      }
      // Close modal after a delay if all succeeded
      setTimeout(() => {
        resetForm();
        isOpen = false;
      }, 1500);
    } else if (successCount > 0 && errorCount > 0) {
      successToastMessage(
        $_("attachment_modal.partial_upload_success", {
          values: { success: successCount, failed: errorCount },
        }),
      );
      if (onAttachmentCreated) {
        onAttachmentCreated();
      }
    } else if (errorCount > 0) {
      errorToastMessage(
        $_("attachment_modal.all_upload_failed", {
          values: { count: errorCount },
        }),
      );
    }
  }

  function getStatusColor(status: FileUploadItem["status"]): string {
    switch (status) {
      case "success":
        return "border-success bg-success-soft";
      case "error":
        return "border-danger bg-danger-soft";
      case "uploading":
        return "border-info bg-info-soft";
      default:
        return "border-border bg-surface-2";
    }
  }

  $effect(() => {
    // Cleanup object URLs when component unmounts or files change
    return () => {
      selectedFiles.forEach((item) => {
        const url = getFileThumbnailUrl(item.file);
        if (url) URL.revokeObjectURL(url);
      });
    };
  });
</script>

{#if isOpen}
<Modal
  onClose={() => (isOpen = false)}
  title={$_("attachment_modal.title")}
  ariaLabel={$_("attachment_modal.title")}
  size="4xl"
  dismissable={!isUploading}
>
  {#snippet icon()}
    <UploadOutline class="w-6 h-6" />
  {/snippet}
  <div class="space-y-6">
    <!-- File Upload Area -->
    <div class="p-6 bg-surface rounded-xl">
      <div class="text-center mb-6">
        <div
          class="mx-auto w-16 h-16 bg-surface-3 rounded-full flex items-center justify-center mb-4"
        >
          <UploadOutline class="w-8 h-8 text-text-muted" />
        </div>
        <h4 class="text-lg font-semibold text-text">
          {$_("attachment_modal.upload_title")}
        </h4>
        <p class="text-sm text-text-muted mt-1">
          {$_("attachment_modal.upload_description")}
        </p>
      </div>

      <div class="flex justify-center mb-6">
        <input
          bind:this={fileInput}
          type="file"
          id="file-upload"
          multiple
          class="hidden"
          onchange={handleFileSelect}
          disabled={isUploading}
        />
        <label
          for="file-upload"
          class="flex flex-col items-center justify-center w-full max-w-md h-32 border-2 border-border-strong border-dashed rounded-lg cursor-pointer bg-surface-2 hover:bg-surface transition-colors"
        >
          <div class="flex flex-col items-center justify-center pt-5 pb-6">
            <svg
              class="w-8 h-8 mb-4 text-text-faint"
              aria-hidden="true"
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 20 16"
            >
              <path
                stroke="currentColor"
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M13 13h3a3 3 0 0 0 0-6h-.025A5.56 5.56 0 0 0 16 6.5 5.5 5.5 0 0 0 5.207 5.021C5.137 5.017 5.071 5 5 5a4 4 0 0 0 0 8h2.167M10 15V6m0 0L8 8m2-2 2 2"
              />
            </svg>
            <p class="mb-2 text-sm text-text-muted">
              <span class="font-semibold text-text"
                >{$_("attachment_modal.click_to_upload")}</span
              >
              <span class="text-text-muted"
                >{$_("attachment_modal.or_drag_drop")}</span
              >
            </p>
            <p class="text-xs text-text-muted">
              {$_("attachment_modal.supported_formats")}
            </p>
          </div>
        </label>
      </div>

      <!-- Selected Files Cards Grid -->
      {#if selectedFiles.length > 0}
        <div class="mt-6">
          <div class="flex items-center justify-between mb-3">
            <h5 class="text-sm font-medium text-text">
              {$_("attachment_modal.selected_files", {
                values: { count: selectedFiles.length },
              })}
            </h5>
            {#if !isUploading}
              <button
                onclick={() => resetForm()}
                class="text-xs text-danger hover:text-danger font-medium"
              >
                {$_("attachment_modal.clear_all")}
              </button>
            {/if}
          </div>

          <div
            class="flex flex-col gap-4 max-h-[32rem] overflow-y-auto p-1"
          >
            {#each selectedFiles as fileItem (fileItem.id)}
              <div
                class="relative rounded-xl border-2 overflow-hidden transition-all flex items-stretch shrink-0 {getStatusColor(
                  fileItem.status,
                )}"
              >
                <!-- Preview (left) -->
                <div
                  class="relative shrink-0 w-40 min-h-[10rem] bg-surface-3 border-e border-border flex items-center justify-center"
                >
                  {#if isImageFile(fileItem.file.name)}
                    <img loading="lazy" decoding="async"
                      src={getFileThumbnailUrl(fileItem.file)}
                      alt={fileItem.file.name}
                      class="w-full h-full object-cover"
                    />
                  {:else}
                    <div
                      class="w-full h-full flex flex-col items-center justify-center p-2"
                    >
                      <span class="text-4xl">{getFileIcon(fileItem.file)}</span>
                      <span
                        class="text-[10px] font-medium text-text-muted uppercase mt-1"
                      >
                        {getFileExtension(fileItem.file.name)}
                      </span>
                    </div>
                  {/if}
                  {#if fileItem.status === "uploading"}
                    <div
                      class="absolute inset-0 bg-[var(--surface-overlay)] flex items-center justify-center"
                    >
                      <div
                        class="w-8 h-8 border-[3px] border-text-on-primary/40 border-t-transparent rounded-full animate-spin"
                      ></div>
                    </div>
                  {:else if fileItem.status === "success"}
                    <div
                      class="absolute inset-0 bg-success-soft flex items-center justify-center"
                    >
                      <CheckCircleSolid
                        class="w-10 h-10 text-success drop-shadow"
                      />
                    </div>
                  {:else if fileItem.status === "error"}
                    <div
                      class="absolute inset-0 bg-danger-soft flex items-center justify-center"
                    >
                      <CloseCircleSolid
                        class="w-10 h-10 text-danger drop-shadow"
                      />
                    </div>
                  {/if}
                </div>

                <!-- Body (right) -->
                <div class="flex-1 min-w-0 flex flex-col gap-3 p-4 pe-10">
                  <div class="min-w-0">
                    <p
                      class="text-sm font-semibold text-text truncate"
                      title={fileItem.file.name}
                    >
                      {fileItem.file.name}
                    </p>
                    <p class="text-[11px] text-text-muted">
                      {(fileItem.file.size / 1024).toFixed(1)} KB
                    </p>
                    {#if fileItem.status === "error" && fileItem.errorMessage}
                      <p
                        class="text-[11px] text-danger mt-1 truncate"
                        title={fileItem.errorMessage}
                      >
                        {fileItem.errorMessage}
                      </p>
                    {/if}
                  </div>

                  <div>
                    <label
                      for="attach-shortname-{fileItem.id}"
                      class="block text-[11px] font-semibold text-text-muted mb-1"
                    >
                      {$_("create_entry.attachments.shortname_label", {
                        default: "Shortname",
                      })}
                    </label>
                    <input
                      id="attach-shortname-{fileItem.id}"
                      type="text"
                      value={fileItem.shortname}
                      oninput={(e) =>
                        updateFileShortname(
                          fileItem.id,
                          (e.target as HTMLInputElement).value,
                        )}
                      disabled={fileItem.status === "uploading" ||
                        fileItem.status === "success"}
                      placeholder={$_(
                        "create_entry.attachments.shortname_placeholder",
                        { default: "Leave empty to auto-generate" },
                      )}
                      class="w-full rounded-md border border-border bg-surface-2 px-2.5 py-1.5 text-xs focus:ring-2 focus:ring-primary disabled:bg-surface disabled:text-text-muted"
                    />
                  </div>

                  <div>
                    <div class="block text-[11px] font-semibold text-text-muted mb-1">
                      {$_("create_entry.attachments.displayname_label", {
                        default: "Display Name",
                      })}
                    </div>
                    <div class="grid grid-cols-1 sm:grid-cols-3 gap-2">
                      <input
                        type="text"
                        placeholder={$_("english")}
                        value={fileItem.displayname.en}
                        oninput={(e) =>
                          updateFileTranslation(
                            fileItem.id,
                            "displayname",
                            "en",
                            (e.target as HTMLInputElement).value,
                          )}
                        disabled={fileItem.status === "uploading" ||
                          fileItem.status === "success"}
                        class="w-full rounded-md border border-border bg-surface-2 px-2.5 py-1.5 text-xs focus:ring-2 focus:ring-primary disabled:bg-surface disabled:text-text-muted"
                      />
                      <input
                        type="text"
                        placeholder="العربية"
                        dir="rtl"
                        value={fileItem.displayname.ar}
                        oninput={(e) =>
                          updateFileTranslation(
                            fileItem.id,
                            "displayname",
                            "ar",
                            (e.target as HTMLInputElement).value,
                          )}
                        disabled={fileItem.status === "uploading" ||
                          fileItem.status === "success"}
                        class="w-full rounded-md border border-border bg-surface-2 px-2.5 py-1.5 text-xs focus:ring-2 focus:ring-primary disabled:bg-surface disabled:text-text-muted"
                      />
                      <input
                        type="text"
                        placeholder="کوردی"
                        dir="rtl"
                        value={fileItem.displayname.ku}
                        oninput={(e) =>
                          updateFileTranslation(
                            fileItem.id,
                            "displayname",
                            "ku",
                            (e.target as HTMLInputElement).value,
                          )}
                        disabled={fileItem.status === "uploading" ||
                          fileItem.status === "success"}
                        class="w-full rounded-md border border-border bg-surface-2 px-2.5 py-1.5 text-xs focus:ring-2 focus:ring-primary disabled:bg-surface disabled:text-text-muted"
                      />
                    </div>
                  </div>

                  <div>
                    <div class="block text-[11px] font-semibold text-text-muted mb-1">
                      {$_("create_entry.attachments.description_label", {
                        default: "Description",
                      })}
                    </div>
                    <div class="grid grid-cols-1 sm:grid-cols-3 gap-2">
                      <textarea
                        rows="2"
                        placeholder={$_("english")}
                        value={fileItem.description.en}
                        oninput={(e) =>
                          updateFileTranslation(
                            fileItem.id,
                            "description",
                            "en",
                            (e.target as HTMLTextAreaElement).value,
                          )}
                        disabled={fileItem.status === "uploading" ||
                          fileItem.status === "success"}
                        class="w-full rounded-md border border-border bg-surface-2 px-2.5 py-1.5 text-xs focus:ring-2 focus:ring-primary disabled:bg-surface disabled:text-text-muted"
                      ></textarea>
                      <textarea
                        rows="2"
                        placeholder="العربية"
                        dir="rtl"
                        value={fileItem.description.ar}
                        oninput={(e) =>
                          updateFileTranslation(
                            fileItem.id,
                            "description",
                            "ar",
                            (e.target as HTMLTextAreaElement).value,
                          )}
                        disabled={fileItem.status === "uploading" ||
                          fileItem.status === "success"}
                        class="w-full rounded-md border border-border bg-surface-2 px-2.5 py-1.5 text-xs focus:ring-2 focus:ring-primary disabled:bg-surface disabled:text-text-muted"
                      ></textarea>
                      <textarea
                        rows="2"
                        placeholder="کوردی"
                        dir="rtl"
                        value={fileItem.description.ku}
                        oninput={(e) =>
                          updateFileTranslation(
                            fileItem.id,
                            "description",
                            "ku",
                            (e.target as HTMLTextAreaElement).value,
                          )}
                        disabled={fileItem.status === "uploading" ||
                          fileItem.status === "success"}
                        class="w-full rounded-md border border-border bg-surface-2 px-2.5 py-1.5 text-xs focus:ring-2 focus:ring-primary disabled:bg-surface disabled:text-text-muted"
                      ></textarea>
                    </div>
                  </div>

                  <div>
                    <label
                      for="attach-tags-{fileItem.id}"
                      class="block text-[11px] font-semibold text-text-muted mb-1"
                    >
                      Tags
                      <span class="normal-case font-normal text-text-faint">
                        (comma-separated)
                      </span>
                    </label>
                    <input
                      id="attach-tags-{fileItem.id}"
                      type="text"
                      value={fileItem.tagsInput}
                      oninput={(e) =>
                        updateFileTagsInput(
                          fileItem.id,
                          (e.target as HTMLInputElement).value,
                        )}
                      disabled={fileItem.status === "uploading" ||
                        fileItem.status === "success"}
                      placeholder={$_("labels.tags_placeholder")}
                      class="w-full rounded-md border border-border bg-surface-2 px-2.5 py-1.5 text-xs focus:ring-2 focus:ring-primary disabled:bg-surface disabled:text-text-muted"
                    />
                  </div>
                </div>

                <!-- Remove button (absolute top-right of row) -->
                {#if fileItem.status !== "uploading" && fileItem.status !== "success"}
                  <button
                    onclick={() => removeFile(fileItem.id)}
                    class="absolute top-2 end-2 shrink-0 w-7 h-7 rounded-full flex items-center justify-center text-text-faint bg-surface-2/90 hover:text-danger hover:bg-danger-soft shadow-sm"
                    title={$_("attachment_modal.remove_file")}
                    aria-label={$_("attachment_modal.remove_file")}
                  >
                    <svg
                      class="w-4 h-4"
                      fill="none"
                      stroke="currentColor"
                      viewBox="0 0 24 24"
                    >
                      <path
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        stroke-width="2"
                        d="M6 18L18 6M6 6l12 12"
                      />
                    </svg>
                  </button>
                {/if}
              </div>
            {/each}
          </div>
        </div>

      {/if}
    </div>
  </div>

  {#snippet footer()}
    <button
      type="button"
      class="app-btn app-btn-secondary"
      onclick={() => {
        resetForm();
        isOpen = false;
      }}
      disabled={isUploading}
    >
      {$_("common.cancel")}
    </button>
    <button
      type="button"
      class="app-btn app-btn-primary"
      onclick={handleFileUpload}
      disabled={selectedFiles.length === 0 ||
        isUploading ||
        selectedFiles.every((f) => f.status === "success")}
      aria-busy={isUploading}
    >
      {#if isUploading}
        <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
        {$_("common.uploading")}
      {:else}
        <UploadOutline size="sm" aria-hidden="true" />
        {$_("attachment_modal.upload_button")}
        {#if selectedFiles.length > 0}
          <span class="tabular-nums">({selectedFiles.filter((f) => f.status !== "success").length})</span>
        {/if}
      {/if}
    </button>
  {/snippet}
</Modal>
{/if}

<style>
  :global(.attachment-modal) {
    background-color: var(--color-surface) !important;
  }

  :global(.attachment-modal .modal-content) {
    background-color: var(--color-surface) !important;
  }

  :global(.attachment-modal h3) {
    color: var(--color-text) !important;
  }

  .border-3 {
    border-width: 3px;
  }
</style>
