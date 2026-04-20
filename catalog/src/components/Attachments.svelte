<script lang="ts">
  import { Dmart, RequestType, ResourceType } from "@edraj/tsdmart";
  import Media from "./Media.svelte";
  import { successToastMessage } from "@/lib/toasts_messages";
  import {
    CloseOutline,
    DownloadOutline,
    EyeOutline,
    TrashBinSolid,
  } from "flowbite-svelte-icons";
  import { _, locale } from "@/i18n";
  import { get } from "svelte/store";
  import {
    getFileExtension,
    getFileTypeIcon,
    isAudioFile,
    isImageFile,
    isPdfFile,
    isVideoFile,
    removeFileExtension,
  } from "../lib/fileUtils";
  import type { Attachment } from "../lib/types";
  import { log } from "../lib/logger";

  function pickTranslation(
    value: any,
    activeLocale: string,
  ): string {
    if (!value) return "";
    if (typeof value === "string") return value;
    if (typeof value !== "object") return String(value ?? "");
    const langs = [activeLocale, "en", "ar", "ku"];
    for (const lang of langs) {
      const v = value[lang];
      if (typeof v === "string" && v.trim().length > 0) return v;
    }
    for (const v of Object.values(value)) {
      if (typeof v === "string" && v.trim().length > 0) return v;
    }
    return "";
  }

  function getDisplayName(attachment: any, activeLocale: string): string {
    const fromAttr = pickTranslation(
      attachment?.attributes?.displayname,
      activeLocale,
    );
    return fromAttr || attachment?.shortname || "";
  }

  function getDescription(attachment: any, activeLocale: string): string {
    return pickTranslation(
      attachment?.attributes?.description,
      activeLocale,
    );
  }

  function getTags(attachment: any): string[] {
    const raw = attachment?.attributes?.tags;
    return Array.isArray(raw) ? raw.filter((t) => typeof t === "string" && t.length > 0) : [];
  }

  let {
    attachments = [],
    space_name,
    subpath,
    parent_shortname,
    isOwner = false,
  }: {
    attachments: any[];
    resource_type: ResourceType;
    space_name: string;
    subpath: string;
    parent_shortname: string;
    isOwner: boolean;
  } = $props();

  let previewModal = $state(false);
  let currentPreview: any = $state(null);
  let previewBlobUrl: string | null = $state(null);
  let previewLoading = $state(false);

  // Editable metadata state (bound to preview modal inputs)
  interface EditTranslation { en: string; ar: string; ku: string }
  let editDisplayname: EditTranslation = $state({ en: "", ar: "", ku: "" });
  let editDescription: EditTranslation = $state({ en: "", ar: "", ku: "" });
  let editTagsInput = $state("");
  let isSavingMeta = $state(false);

  function coerceTranslation(value: any): EditTranslation {
    if (!value || typeof value !== "object") {
      return { en: typeof value === "string" ? value : "", ar: "", ku: "" };
    }
    return {
      en: typeof value.en === "string" ? value.en : "",
      ar: typeof value.ar === "string" ? value.ar : "",
      ku: typeof value.ku === "string" ? value.ku : "",
    };
  }

  function translationPayload(t: EditTranslation): Record<string, string> | null {
    const out: Record<string, string> = {};
    if (t.en?.trim()) out.en = t.en.trim();
    if (t.ar?.trim()) out.ar = t.ar.trim();
    if (t.ku?.trim()) out.ku = t.ku.trim();
    return Object.keys(out).length > 0 ? out : null;
  }

  function parseTagsInput(value: string): string[] {
    return value
      .split(",")
      .map((t) => t.trim())
      .filter((t) => t.length > 0);
  }

  function getAttachmentApiUrl(attachment: any): string {
    const filename = attachment?.attributes?.payload?.body ?? "";
    return Dmart.getAttachmentUrl({
      resource_type: attachment.resource_type as ResourceType,
      space_name,
      subpath,
      parent_shortname,
      shortname: removeFileExtension(attachment.shortname),
      ext: getFileExtension(filename) ?? "",
    });
  }

  async function fetchWithAuth(url: string): Promise<Blob | null> {
    const token = localStorage.getItem("authToken");
    const headers: Record<string, string> = {};
    if (token) headers["Authorization"] = `Bearer ${token}`;
    const res = await fetch(url, { headers, credentials: "include" });
    if (res.ok) return res.blob();
    return null;
  }

  async function openPreview(attachment: any) {
    const filename = attachment?.attributes?.payload?.body ?? "";

    if (
      isImageFile(filename) ||
      isVideoFile(filename) ||
      isPdfFile(filename) ||
      isAudioFile(filename)
    ) {
      let type = "file";
      if (isImageFile(filename)) type = "image";
      else if (isVideoFile(filename)) type = "video";
      else if (isPdfFile(filename)) type = "pdf";
      else if (isAudioFile(filename)) type = "audio";

      const apiUrl = getAttachmentApiUrl(attachment);
      currentPreview = { ...attachment, url: apiUrl, type, filename };

      // Seed editable fields from the attachment's attributes
      editDisplayname = coerceTranslation(attachment?.attributes?.displayname);
      editDescription = coerceTranslation(attachment?.attributes?.description);
      editTagsInput = Array.isArray(attachment?.attributes?.tags)
        ? attachment.attributes.tags.join(", ")
        : "";

      previewModal = true;
      previewLoading = true;

      const blob = await fetchWithAuth(apiUrl);
      if (blob) {
        previewBlobUrl = URL.createObjectURL(blob);
      }
      previewLoading = false;
    }
  }

  async function handleSaveMeta() {
    if (!currentPreview) return;
    isSavingMeta = true;
    try {
      const displaynamePayload = translationPayload(editDisplayname);
      const descriptionPayload = translationPayload(editDescription);
      const tags = parseTagsInput(editTagsInput);

      const attributes: Record<string, any> = {
        displayname: displaynamePayload ?? {},
        description: descriptionPayload ?? {},
        tags,
      };

      const response = await Dmart.request({
        space_name,
        request_type: RequestType.update,
        records: [
          {
            resource_type: currentPreview.resource_type,
            shortname: currentPreview.shortname,
            subpath: `${currentPreview.subpath}/${parent_shortname}`,
            attributes,
          },
        ],
      });

      if (response?.status === "success") {
        // Patch the local list so the card reflects the change without a reload
        attachments = attachments.map((a: any) =>
          a.shortname === currentPreview.shortname
            ? {
                ...a,
                attributes: {
                  ...(a.attributes ?? {}),
                  ...attributes,
                },
              }
            : a,
        );
        successToastMessage("Attachment metadata updated");
        closePreview();
      } else {
        log.error(
          "Failed to update attachment metadata:",
          (response as any)?.error,
        );
      }
    } catch (err) {
      log.error("Error updating attachment metadata:", err);
    } finally {
      isSavingMeta = false;
    }
  }

  function closePreview() {
    previewModal = false;
    currentPreview = null;
    if (previewBlobUrl) {
      URL.revokeObjectURL(previewBlobUrl);
      previewBlobUrl = null;
    }
  }

  async function downloadFile(attachment: any) {
    const apiUrl = getAttachmentApiUrl(attachment);
    const blob = await fetchWithAuth(apiUrl);
    if (!blob) return;

    const blobUrl = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = blobUrl;
    link.download = attachment.attributes?.payload?.body || attachment.shortname;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(blobUrl);
  }

  async function handleDelete(attachment: any) {
    if (
      confirm(
        `Are you sure want to delete ${attachment.shortname} attachment`
      ) === false
    ) {
      return;
    }

    const request_dict = {
      space_name,
      request_type: RequestType.delete,
      records: [
        {
          resource_type: attachment.resource_type,
          shortname: attachment.shortname,
          subpath: `${attachment.subpath}/${parent_shortname}`,
          attributes: {},
        },
      ],
    };
    const response = await Dmart.request(request_dict as any);
    if (response.status === "success") {
      attachments = attachments.filter(
        (e: { shortname: string }) => e.shortname !== attachment.shortname
      );
      successToastMessage(`Attachment deleted successfully.`);
    } else {
      successToastMessage(`Attachment deletion failed.`);
    }
  }
</script>

<div class="attachments-container">
  {#if attachments.length === 0}
    <div class="no-attachments">
      <div class="no-attachments-icon">📎</div>
      <p class="no-attachments-text">{$_("NoAttachments")}</p>
      <p class="no-attachments-subtitle">Files and media will appear here</p>
    </div>
  {:else}
    <div class="attachments-grid">
      {#each attachments as attachment}
        <div class="attachment-card">
          <!-- Card Header with Actions -->
          <div class="attachment-header">
            <div class="file-type-badge">
              <span class="file-icon"
                >{getFileTypeIcon(attachment.attributes?.payload?.body)}</span
              >
              <span class="file-ext"
                >{getFileExtension(attachment.attributes?.payload?.body) ||
                  "Unknown"}</span
              >
            </div>

            <div class="attachment-actions">
              {#if isImageFile(attachment.attributes?.payload?.body) || isVideoFile(attachment.attributes?.payload?.body) || isPdfFile(attachment.attributes?.payload?.body) || isAudioFile(attachment.attributes?.payload?.body)}
                <button
                  aria-label={`Preview ${attachment.shortname}`}
                  class="action-button preview-button"
                  onclick={() => openPreview(attachment)}
                  title="Preview"
                >
                  <EyeOutline class="w-4 h-4" />
                </button>
              {/if}

              <button
                aria-label={`Download ${attachment.shortname}`}
                class="action-button download-button"
                onclick={() => downloadFile(attachment)}
                title="Download"
              >
                <DownloadOutline class="w-4 h-4" />
              </button>

              {#if isOwner}
                <button
                  aria-label={`Delete ${attachment.shortname}`}
                  class="action-button delete-button"
                  onclick={() => handleDelete(attachment)}
                  title="Delete"
                >
                  <TrashBinSolid class="w-4 h-4" />
                </button>
              {/if}
            </div>
          </div>

          <!-- Media Preview -->
          <div class="attachment-preview">
            {#if attachment && [ResourceType.media, ResourceType.comment].includes(attachment.resource_type)}
              <div class="media-wrapper">
                <Media
                  resource_type={attachment.resource_type}
                  attributes={attachment.attributes}
                  displayname={attachment.shortname}
                  url={Dmart.getAttachmentUrl({
                    resource_type: attachment.resource_type as ResourceType,
                    space_name,
                    subpath,
                    parent_shortname,
                    shortname: removeFileExtension(attachment.shortname),
                    ext: getFileExtension(attachment.attributes?.payload?.body) ?? "",
                  })}
                />
                <div class="media-overlay">
                  <button
                    aria-label={`Preview ${attachment.shortname}`}
                    class="preview-overlay-button"
                    onclick={() => openPreview(attachment)}
                  >
                    <EyeOutline class="w-6 h-6" />
                    <span>Preview</span>
                  </button>
                </div>
              </div>
            {:else}
              <div class="unsupported-file">
                <div class="unsupported-icon">
                  {getFileTypeIcon(attachment.attributes?.payload?.body)}
                </div>
                <span class="unsupported-text">{$_("Unsupportedformat")}</span>
              </div>
            {/if}
          </div>

          <!-- File Info -->
          <div class="attachment-info">
            <h4 class="attachment-name" title={attachment.shortname}>
              {getDisplayName(attachment, $locale ?? "en")}
            </h4>
            <p class="attachment-shortname" title={attachment.shortname}>
              {attachment.shortname}
            </p>
            {#if getDescription(attachment, $locale ?? "en")}
              <p
                class="attachment-description"
                title={getDescription(attachment, $locale ?? "en")}
              >
                {getDescription(attachment, $locale ?? "en")}
              </p>
            {/if}
            {#if getTags(attachment).length > 0}
              <div class="attachment-tags">
                {#each getTags(attachment) as tag}
                  <span class="attachment-tag">{tag}</span>
                {/each}
              </div>
            {/if}
          </div>
        </div>
      {/each}
    </div>
  {/if}
</div>

<!-- Preview Modal -->
{#if previewModal && currentPreview}
  <div
    class="modal-overlay"
    role="button"
    tabindex="0"
    onclick={closePreview}
    onkeydown={(e) => {
      if (e.key === "Enter" || e.key === " ") closePreview();
    }}
    aria-label="Close preview modal"
  >
    <div
      class="modal-content"
      role="dialog"
      aria-modal="true"
      onclick={(e) => e.stopPropagation()}
      tabindex="0"
      onkeydown={(e) => {
        if (e.key === "Escape") closePreview();
      }}
    >
      <div class="modal-header">
        <div class="modal-title-wrap">
          <h3 class="modal-title">
            {getDisplayName(currentPreview, $locale ?? "en")}
          </h3>
          <p class="modal-shortname">{currentPreview.shortname}</p>
        </div>
        <button
          class="modal-close"
          onclick={closePreview}
          aria-label="Close modal"
        >
          <CloseOutline class="w-6 h-6" />
        </button>
      </div>

      <div class="modal-edit">
        <div class="edit-field">
          <div class="edit-label">Display name</div>
          <div class="edit-translations">
            <input
              type="text"
              placeholder="English"
              bind:value={editDisplayname.en}
              disabled={isSavingMeta}
              class="edit-input"
            />
            <input
              type="text"
              placeholder="Arabic"
              bind:value={editDisplayname.ar}
              disabled={isSavingMeta}
              class="edit-input"
            />
            <input
              type="text"
              placeholder="Kurdish"
              bind:value={editDisplayname.ku}
              disabled={isSavingMeta}
              class="edit-input"
            />
          </div>
        </div>

        <div class="edit-field">
          <div class="edit-label">Description</div>
          <div class="edit-translations">
            <textarea
              rows="2"
              placeholder="English"
              bind:value={editDescription.en}
              disabled={isSavingMeta}
              class="edit-input"
            ></textarea>
            <textarea
              rows="2"
              placeholder="Arabic"
              bind:value={editDescription.ar}
              disabled={isSavingMeta}
              class="edit-input"
            ></textarea>
            <textarea
              rows="2"
              placeholder="Kurdish"
              bind:value={editDescription.ku}
              disabled={isSavingMeta}
              class="edit-input"
            ></textarea>
          </div>
        </div>

        <div class="edit-field">
          <div class="edit-label">
            Tags
            <span class="edit-label-hint">(comma-separated)</span>
          </div>
          <input
            type="text"
            placeholder="tag1, tag2"
            bind:value={editTagsInput}
            disabled={isSavingMeta}
            class="edit-input"
          />
        </div>
      </div>

      <div class="modal-body">
        {#if previewLoading}
          <div class="modal-loading"><div class="spinner spinner-lg"></div></div>
        {:else if previewBlobUrl}
          {#if currentPreview.type === "image"}
            <img
              src={previewBlobUrl}
              alt={currentPreview.shortname || "no-image"}
              class="modal-image"
            />
          {:else if currentPreview.type === "video"}
            <video src={previewBlobUrl} controls class="modal-video">
              <track
                kind="captions"
                label="English captions"
                src=""
                srclang="en"
                default
              />
              Your browser doesn't support video playback.
            </video>
          {:else if currentPreview.type === "audio"}
            <div class="audio-container">
              <div class="audio-icon">🎵</div>
              <h4 class="audio-title">{currentPreview.shortname}</h4>
              <audio src={previewBlobUrl} controls class="modal-audio">
                Your browser doesn't support audio playback.
              </audio>
            </div>
          {:else if currentPreview.type === "pdf"}
            <iframe
              src={previewBlobUrl}
              class="modal-pdf"
              title={currentPreview.shortname}
            >
              Your browser doesn't support PDF viewing.
            </iframe>
          {/if}
        {:else}
          <div class="modal-loading" style="color: var(--color-gray-400); font-size: 0.875rem;">Failed to load preview</div>
        {/if}
      </div>

      <div class="modal-footer">
        <button
          aria-label="Cancel"
          class="modal-button cancel"
          onclick={closePreview}
          disabled={isSavingMeta}
        >
          {$_("cancel") || "Cancel"}
        </button>
        <button
          aria-label="Save"
          class="modal-button save"
          onclick={handleSaveMeta}
          disabled={isSavingMeta}
        >
          {#if isSavingMeta}
            <span class="save-spinner"></span>
            Saving…
          {:else}
            Save
          {/if}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .modal-pdf {
    width: 100%;
    height: 40vh;
    min-height: 300px;
    border: none;
    border-radius: 8px;
    background: #f8f9fa;
    box-shadow: 0 2px 10px rgba(0, 0, 0, 0.1);
  }

  @media (max-width: 768px) {
    .modal-pdf {
      height: 35vh;
      min-height: 260px;
    }
  }

  @media (max-width: 480px) {
    .modal-pdf {
      height: 30vh;
      min-height: 220px;
    }
  }

  .modal-pdf[src=""]:before {
    content: "Loading PDF...";
    display: flex;
    align-items: center;
    justify-content: center;
    height: 100%;
    font-size: 16px;
    color: #6b7280;
  }
  .attachments-container {
    width: 100%;
  }
  .audio-container {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1.5rem;
    padding: 2rem;
    background: linear-gradient(135deg, #f8fafc 0%, #f1f5f9 100%);
    border-radius: 16px;
    min-width: 400px;
  }

  .audio-icon {
    font-size: 4rem;
    opacity: 0.7;
  }

  .audio-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: #1e293b;
    text-align: center;
    margin: 0;
    word-break: break-word;
  }

  .modal-audio {
    width: 100%;
    max-width: 400px;
    height: 40px;
    border-radius: 8px;
    background: white;
    box-shadow: 0 2px 10px rgba(0, 0, 0, 0.1);
  }

  .no-attachments {
    text-align: center;
    padding: 3rem 1rem;
    background: var(--color-gray-50);
    border-radius: var(--radius-xl);
    border: 2px dashed var(--color-gray-300);
  }

  .no-attachments-icon {
    font-size: 3rem;
    margin-bottom: 1rem;
    opacity: 0.5;
  }

  .no-attachments-text {
    color: var(--color-gray-600);
    font-size: 1.125rem;
    font-weight: 600;
    margin-bottom: 0.5rem;
  }

  .no-attachments-subtitle {
    color: var(--color-gray-400);
    font-size: 0.875rem;
  }

  .attachments-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
    gap: 1.5rem;
    margin-top: 1rem;
  }

  @media (min-width: 1200px) {
    .attachments-grid {
      /* grid-template-columns: repeat(3, 1fr); */
      grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
    }
  }

  @media (max-width: 768px) {
    .attachments-grid {
      grid-template-columns: 1fr;
      gap: 1rem;
    }
  }

  .attachment-card {
    background: var(--surface-card);
    border-radius: var(--radius-xl);
    overflow: hidden;
    box-shadow: var(--shadow-sm);
    transition: all var(--duration-normal) var(--ease-out);
    border: 1px solid var(--color-gray-200);
  }

  .attachment-card:hover {
    transform: translateY(-3px);
    box-shadow: var(--shadow-lg);
    border-color: var(--color-gray-300);
  }

  .attachment-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 1rem;
    background: var(--color-gray-50);
    border-bottom: 1px solid var(--color-gray-200);
  }

  .file-type-badge {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0.75rem;
    background: var(--surface-card);
    border-radius: var(--radius-md);
    border: 1px solid var(--color-gray-200);
  }

  .file-icon {
    font-size: 1.25rem;
  }

  .file-ext {
    font-size: 0.75rem;
    font-weight: 600;
    color: #64748b;
    text-transform: uppercase;
    letter-spacing: 0.5px;
  }

  .attachment-actions {
    display: flex;
    gap: 0.5rem;
  }

  .action-button {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 2rem;
    height: 2rem;
    border-radius: var(--radius-md);
    border: 1px solid var(--color-gray-200);
    background: var(--surface-card);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
  }

  .preview-button:hover {
    background: #dbeafe;
    border-color: #3b82f6;
    color: #3b82f6;
  }

  .download-button:hover {
    background: #f0fdf4;
    border-color: #22c55e;
    color: #22c55e;
  }

  .delete-button:hover {
    background: #fef2f2;
    border-color: #ef4444;
    color: #ef4444;
  }

  .attachment-preview {
    position: relative;
    height: 200px;
    overflow: hidden;
    background: #f9fafb;
  }

  .media-wrapper {
    position: relative;
    width: 100%;
    height: 100%;
  }

  .media-overlay {
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: center;
    justify-content: center;
    opacity: 0;
    transition: opacity 0.3s ease;
    backdrop-filter: blur(2px);
  }

  .attachment-card:hover .media-overlay {
    opacity: 1;
  }

  .preview-overlay-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    background: rgba(255, 255, 255, 0.9);
    border: none;
    border-radius: 8px;
    color: #374151;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s ease;
    backdrop-filter: blur(8px);
  }

  .preview-overlay-button:hover {
    background: white;
    transform: scale(1.05);
  }

  .unsupported-file {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100%;
    color: #64748b;
  }

  .unsupported-icon {
    font-size: 2rem;
    margin-bottom: 0.5rem;
    opacity: 0.7;
  }

  .unsupported-text {
    font-size: 0.875rem;
    font-weight: 500;
  }

  .attachment-info {
    padding: 1rem;
    background: white;
  }

  .attachment-name {
    font-size: 0.875rem;
    font-weight: 600;
    color: #1e293b;
    margin-bottom: 0.25rem;
    word-break: break-word;
    line-height: 1.4;
  }

  .attachment-shortname {
    font-size: 0.6875rem;
    color: #94a3b8;
    margin: 0 0 0.5rem 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    word-break: break-all;
  }

  .attachment-description {
    font-size: 0.75rem;
    color: #475569;
    margin: 0 0 0.5rem 0;
    line-height: 1.45;
    display: -webkit-box;
    -webkit-line-clamp: 3;
    line-clamp: 3;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .attachment-tags {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem;
    margin-top: 0.25rem;
  }

  .attachment-tag {
    font-size: 0.625rem;
    padding: 0.125rem 0.5rem;
    border-radius: 9999px;
    background: #eef2ff;
    color: #4338ca;
    font-weight: 500;
  }

  .modal-overlay {
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.8);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
    padding: 1rem;
  }

  .modal-content {
    background: var(--surface-card);
    border-radius: var(--radius-xl);
    width: min(900px, 92vw);
    max-height: 92vh;
    overflow: hidden;
    display: flex;
    flex-direction: column;
    box-shadow: var(--shadow-xl);
  }

  .modal-header,
  .modal-footer {
    flex: 0 0 auto;
  }

  .modal-edit {
    flex: 0 0 auto;
  }

  .modal-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 1.5rem;
    border-bottom: 1px solid var(--color-gray-200);
    background: var(--color-gray-50);
  }

  .modal-title-wrap {
    display: flex;
    flex-direction: column;
    min-width: 0;
    flex: 1;
  }

  .modal-title {
    font-size: 1.25rem;
    font-weight: 600;
    color: var(--color-gray-800);
    margin: 0;
    word-break: break-word;
  }

  .modal-shortname {
    font-size: 0.75rem;
    color: var(--color-gray-400);
    margin: 0.125rem 0 0 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    word-break: break-all;
  }

  .modal-close {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 2.5rem;
    height: 2.5rem;
    border-radius: var(--radius-md);
    border: 1px solid var(--color-gray-200);
    background: var(--surface-card);
    cursor: pointer;
    transition: all var(--duration-fast) ease;
  }

  .modal-close:hover {
    background: var(--color-gray-100);
    border-color: var(--color-gray-300);
  }

  .modal-body {
    flex: 1 1 auto;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 1.25rem;
    min-height: 180px;
    overflow: auto;
  }

  .modal-loading {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 200px;
  }

  .modal-image {
    max-width: 100%;
    max-height: 40vh;
    width: auto;
    height: auto;
    object-fit: contain;
    border-radius: 8px;
    box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
  }

  .modal-video {
    max-width: 100%;
    max-height: 40vh;
    border-radius: 8px;
    box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
  }

  .modal-footer {
    display: flex;
    justify-content: flex-end;
    gap: 1rem;
    padding: 1.5rem;
    border-top: 1px solid var(--color-gray-200);
    background: var(--color-gray-50);
  }

  .modal-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    border-radius: var(--radius-lg);
    font-weight: 500;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    border: 1px solid;
  }

  .modal-button.cancel {
    background: white;
    border-color: var(--color-gray-300);
    color: var(--color-gray-700);
  }

  .modal-button.cancel:hover:not(:disabled) {
    background: var(--color-gray-50);
    border-color: var(--color-gray-400);
  }

  .modal-button.save {
    background: #4f46e5;
    border-color: #4f46e5;
    color: white;
  }

  .modal-button.save:hover:not(:disabled) {
    background: #4338ca;
    border-color: #4338ca;
  }

  .modal-button:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }

  .save-spinner {
    width: 1rem;
    height: 1rem;
    border: 2px solid rgba(255, 255, 255, 0.35);
    border-top-color: white;
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
  }

  @keyframes spin {
    to { transform: rotate(360deg); }
  }

  .modal-edit {
    padding: 1rem 1.5rem;
    display: flex;
    flex-direction: column;
    gap: 0.875rem;
    border-bottom: 1px solid var(--color-gray-200);
    background: var(--color-gray-50);
  }

  .edit-field {
    display: flex;
    flex-direction: column;
    gap: 0.375rem;
  }

  .edit-label {
    font-size: 0.6875rem;
    font-weight: 600;
    color: var(--color-gray-500);
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  .edit-label-hint {
    text-transform: none;
    font-weight: 400;
    color: var(--color-gray-400);
    letter-spacing: 0;
  }

  .edit-translations {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 0.5rem;
  }

  .edit-input {
    width: 100%;
    padding: 0.5rem 0.75rem;
    font-size: 0.8125rem;
    color: var(--color-gray-800);
    background: white;
    border: 1px solid var(--color-gray-200);
    border-radius: 0.5rem;
    transition: border-color 0.15s ease, box-shadow 0.15s ease;
  }

  .edit-input:focus {
    outline: none;
    border-color: #6366f1;
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.2);
  }

  .edit-input:disabled {
    background: var(--color-gray-50);
    color: var(--color-gray-400);
  }

  textarea.edit-input {
    resize: vertical;
    min-height: 3rem;
  }

  @media (max-width: 640px) {
    .edit-translations {
      grid-template-columns: 1fr;
    }
  }

  :global(.attachment-preview .media-wrapper) {
    width: 100%;
    height: 100%;
  }

  :global(.attachment-preview img) {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }

  :global(.attachment-preview video) {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }

  @media (max-width: 640px) {
    .attachment-preview {
      height: 150px;
    }
    .audio-container {
      min-width: auto;
      width: 100%;
      padding: 1.5rem;
    }

    .modal-audio {
      max-width: 100%;
    }
    .modal-content {
      margin: 0;
      border-radius: 0;
      max-width: 100vw;
      max-height: 100vh;
    }

    .modal-body {
      padding: 1rem;
    }
  }
</style>
