<script lang="ts">
  import { Dmart, RequestType, ResourceType } from "@edraj/tsdmart";
  import { successToastMessage } from "@/lib/toasts_messages";
  import {
    CloseOutline,
    DownloadOutline,
    EyeOutline,
    TrashBinSolid,
    PlaySolid,
  } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";
  import { confirm } from "@/lib/confirm";
  import { getFileExtension } from "@shared/file-extension";
  import {
    getFileTypeIcon,
    isAudioFile,
    isImageFile,
    isPdfFile,
    isVideoFile,
    removeFileExtension,
  } from "../lib/fileUtils";

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

  function openPreview(attachment: any) {
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
      const shortname = removeFileExtension(attachment.shortname);
      currentPreview = {
        ...attachment,
        url: Dmart.getAttachmentUrl({
          resource_type: attachment.resource_type as ResourceType,
          space_name,
          subpath,
          parent_shortname,
          shortname,
          ext: getFileExtension(filename),
        }),
        type,
        filename,
      };
      previewModal = true;
    }
  }

  function closePreview() {
    previewModal = false;
    currentPreview = null;
  }

  function downloadFile(attachment: any) {
    const filename = attachment.attributes?.payload?.body ?? "";
    const url = Dmart.getAttachmentUrl({
      resource_type: attachment.resource_type as ResourceType,
      space_name,
      subpath,
      parent_shortname,
      shortname: attachment.shortname,
      ext: getFileExtension(filename),
    });

    const link = document.createElement("a");
    link.href = url;
    link.download = attachment.shortname;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  }

  async function handleDelete(attachment: any) {
    const confirmed = await confirm({
      title: $_("attachment_list.delete_title"),
      body: $_("attachment_list.delete_body", {
        values: { name: attachment.shortname },
      }),
      variant: "danger",
    });
    if (!confirmed) return;

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

  function getAttachmentUrl(attachment: any) {
    const filename = attachment.attributes?.payload?.body ?? "";
    return Dmart.getAttachmentUrl({
      resource_type: attachment.resource_type as ResourceType,
      space_name,
      subpath,
      parent_shortname,
      shortname: attachment.shortname,
      ext: getFileExtension(filename) ?? "",
    });
  }

</script>

<div class="messenger-attachments">
  {#if attachments.length === 0}
    <div class="no-attachments">
      <div class="no-attachments-icon">📎</div>
      <p class="no-attachments-text">{$_("NoAttachments")}</p>
    </div>
  {:else}
    <div class="attachments-container">
      {#each attachments as attachment (attachment.shortname)}
        {@const filename = attachment.attributes?.payload?.body}
        {@const url = getAttachmentUrl(attachment)}

        {#if isImageFile(filename)}
          <!-- Image Attachment -->
          <div class="image-attachment" role="button" tabindex="0" onclick={() => openPreview(attachment)} onkeydown={(e) => { if (e.key === 'Enter' || e.key === ' ') openPreview(attachment); }}>
            <img src={url} alt={attachment.shortname} loading="lazy" />
            <div class="image-overlay">
              <div class="overlay-actions">
                <button
                  class="overlay-btn"
                  onclick={(e) => {
                    e.stopPropagation();
                    downloadFile(attachment);
                  }}
                  title={$_("labels.download")}
                  aria-label={$_("labels.download")}
                >
                  <DownloadOutline class="w-4 h-4" />
                </button>
                {#if isOwner}
                  <button
                    class="overlay-btn delete"
                    onclick={(e) => {
                      e.stopPropagation();
                      handleDelete(attachment);
                    }}
                    title={$_("common.delete")}
                    aria-label={$_("common.delete")}
                  >
                    <TrashBinSolid class="w-4 h-4" />
                  </button>
                {/if}
              </div>
            </div>
          </div>
        {:else if isVideoFile(filename)}
          <!-- Video Attachment -->
          <div class="video-attachment" role="button" tabindex="0" onclick={() => openPreview(attachment)} onkeydown={(e) => { if (e.key === 'Enter' || e.key === ' ') openPreview(attachment); }}>
            <video src={url} preload="metadata">
              <track kind="captions" src="" srclang="en" label="English" />
            </video>
            <div class="video-overlay">
              <div class="play-button">
                <PlaySolid class="w-8 h-8" />
              </div>
              <div class="overlay-actions">
                <button
                  class="overlay-btn"
                  onclick={(e) => {
                    e.stopPropagation();
                    downloadFile(attachment);
                  }}
                  title={$_("labels.download")}
                  aria-label={$_("labels.download")}
                >
                  <DownloadOutline class="w-4 h-4" />
                </button>
                {#if isOwner}
                  <button
                    class="overlay-btn delete"
                    onclick={(e) => {
                      e.stopPropagation();
                      handleDelete(attachment);
                    }}
                    title={$_("common.delete")}
                    aria-label={$_("common.delete")}
                  >
                    <TrashBinSolid class="w-4 h-4" />
                  </button>
                {/if}
              </div>
            </div>
          </div>
        {:else if isAudioFile(filename)}
          <!-- Audio Attachment -->
          <div class="audio-attachment">
            <div class="audio-header">
              <div class="audio-icon">🎵</div>
              <div class="audio-info">
                <div class="audio-name">{attachment.shortname}</div>
                <div class="audio-meta">{$_("attachment_list.audio_file")}</div>
              </div>
              <div class="audio-actions">
                <button
                  class="action-btn"
                  onclick={() => downloadFile(attachment)}
                  title={$_("labels.download")}
                  aria-label={$_("labels.download")}
                >
                  <DownloadOutline class="w-4 h-4" />
                </button>
                {#if isOwner}
                  <button
                    class="action-btn delete"
                    onclick={() => handleDelete(attachment)}
                    title={$_("common.delete")}
                    aria-label={$_("common.delete")}
                  >
                    <TrashBinSolid class="w-4 h-4" />
                  </button>
                {/if}
              </div>
            </div>
            <audio controls class="audio-player" preload="metadata">
              <source src={url} type="audio/mpeg" />
              <source src={url} type="audio/wav" />
              <source src={url} type="audio/ogg" />
              Your browser does not support the audio element.
            </audio>
          </div>
        {:else}
          <!-- File Attachment -->
          <div class="file-attachment">
            <div class="file-icon">
              {getFileTypeIcon(filename)}
            </div>
            <div class="file-info">
              <div class="file-name" title={attachment.shortname}>
                {attachment.shortname}
              </div>
              <div class="file-meta">
                <span class="file-type">
                  {getFileExtension(filename)?.toUpperCase() || "FILE"}
                </span>
                <!-- <span class="file-size">{formatFileSize(attachment.size)}</span> -->
              </div>
            </div>
            <div class="file-actions">
              {#if isPdfFile(filename)}
                <button
                  class="action-btn preview"
                  onclick={() => openPreview(attachment)}
                  title={$_("labels.preview")}
                  aria-label={$_("labels.preview")}
                >
                  <EyeOutline class="w-4 h-4" />
                </button>
              {/if}
              <button
                class="action-btn download"
                onclick={() => downloadFile(attachment)}
                title={$_("labels.download")}
                  aria-label={$_("labels.download")}
              >
                <DownloadOutline class="w-4 h-4" />
              </button>
              {#if isOwner}
                <button
                  class="action-btn delete"
                  onclick={() => handleDelete(attachment)}
                  title={$_("common.delete")}
                    aria-label={$_("common.delete")}
                >
                  <TrashBinSolid class="w-4 h-4" />
                </button>
              {/if}
            </div>
          </div>
        {/if}
      {/each}
    </div>
  {/if}
</div>

<!-- Preview Modal -->
{#if previewModal && currentPreview}
  <div
    class="modal-overlay"
    onclick={closePreview}
    role="button"
    tabindex="0"
    onkeydown={(e) => {
      if (e.key === "Enter" || e.key === " ") closePreview();
    }}
    aria-label={$_("labels.close_preview")}
  >
    <div
      class="modal-content"
      onclick={(e) => e.stopPropagation()}
      role="dialog"
      aria-modal="true"
      tabindex="0"
      onkeydown={(e) => {
        if (e.key === "Escape") closePreview();
      }}
    >
      <div class="modal-header">
        <h3 class="modal-title">{currentPreview.shortname}</h3>
        <button
          class="modal-close"
          onclick={closePreview}
          aria-label={$_("common.close")}
        >
          <CloseOutline class="w-6 h-6" />
        </button>
      </div>

      <div class="modal-body">
        {#if currentPreview.type === "image"}
          <img loading="lazy" decoding="async"
            src={currentPreview.url}
            alt={currentPreview.shortname || "preview"}
            class="modal-image"
          />
        {:else if currentPreview.type === "video"}
          <video src={currentPreview.url} controls class="modal-video">
            <track kind="captions" src="" srclang="en" label="English" />
            Your browser doesn't support video playback.
          </video>
        {:else if currentPreview.type === "audio"}
          <div class="audio-modal-container">
            <div class="audio-modal-icon">🎵</div>
            <h4 class="audio-modal-title">{currentPreview.shortname}</h4>
            <audio src={currentPreview.url} controls class="modal-audio">
              Your browser doesn't support audio playback.
            </audio>
          </div>
        {:else if currentPreview.type === "pdf"}
          <iframe
            src={currentPreview.url}
            class="modal-pdf"
            title={currentPreview.shortname}
          >
            Your browser doesn't support PDF viewing.
          </iframe>
        {/if}
      </div>

      <div class="modal-footer">
        <button
          class="modal-button download"
          onclick={() => downloadFile(currentPreview)}
        >
          <DownloadOutline class="w-4 h-4" />
          Download
        </button>
        {#if isOwner}
          <button
            class="modal-button delete"
            onclick={() => handleDelete(currentPreview)}
          >
            <TrashBinSolid class="w-4 h-4" />
            Delete
          </button>
        {/if}
      </div>
    </div>
  </div>
{/if}

<style>
  .messenger-attachments {
    width: 100%;
    max-width: 280px;
  }

  .no-attachments {
    text-align: center;
    padding: 1rem;
    color: var(--color-text-muted);
  }

  .no-attachments-icon {
    font-size: 2rem;
    margin-bottom: 0.5rem;
    opacity: 0.5;
  }

  .no-attachments-text {
    font-size: 0.875rem;
    margin: 0;
  }

  .attachments-container {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  /* Image Attachments */
  .image-attachment {
    position: relative;
    border-radius: 12px;
    overflow: hidden;
    cursor: pointer;
    transition: transform 0.2s ease;
    max-height: 200px;
  }

  .image-attachment:hover {
    transform: scale(1.02);
  }

  .image-attachment img {
    width: 100%;
    height: auto;
    max-height: 200px;
    object-fit: cover;
    display: block;
  }

  .image-overlay {
    position: absolute;
    top: 0;
    inset-inline-start: 0;
    inset-inline-end: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.3);
    opacity: 0;
    transition: opacity 0.2s ease;
    display: flex;
    align-items: flex-start;
    justify-content: flex-end;
    padding: 8px;
  }

  .image-attachment:hover .image-overlay {
    opacity: 1;
  }

  .overlay-actions {
    display: flex;
    gap: 4px;
  }

  .overlay-btn {
    width: 28px;
    height: 28px;
    border-radius: 6px;
    border: none;
    background: rgba(255, 255, 255, 0.9);
    color: var(--color-text);
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .overlay-btn:hover {
    background: var(--color-surface);
    transform: scale(1.1);
  }

  .overlay-btn.delete {
    background: rgba(244, 67, 54, 0.9);
    color: white;
  }

  .overlay-btn.delete:hover {
    background: var(--color-danger);
  }

  /* Video Attachments */
  .video-attachment {
    position: relative;
    border-radius: 12px;
    overflow: hidden;
    cursor: pointer;
    transition: transform 0.2s ease;
    max-height: 200px;
  }

  .video-attachment:hover {
    transform: scale(1.02);
  }

  .video-attachment video {
    width: 100%;
    height: auto;
    max-height: 200px;
    object-fit: cover;
    display: block;
  }

  .video-overlay {
    position: absolute;
    top: 0;
    inset-inline-start: 0;
    inset-inline-end: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.3);
    display: flex;
    align-items: center;
    justify-content: center;
    transition: background 0.2s ease;
  }

  .video-attachment:hover .video-overlay {
    background: rgba(0, 0, 0, 0.5);
  }

  .play-button {
    position: absolute;
    width: 48px;
    height: 48px;
    border-radius: 50%;
    background: rgba(255, 255, 255, 0.9);
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--color-text);
    transition: all 0.2s ease;
  }

  .video-attachment:hover .play-button {
    background: var(--color-surface);
    transform: scale(1.1);
  }

  .video-overlay .overlay-actions {
    position: absolute;
    top: 8px;
    inset-inline-end: 8px;
  }

  /* Audio Attachments */
  .audio-attachment {
    background: var(--color-surface);
    border-radius: 12px;
    padding: 12px;
    border: 1px solid var(--color-border);
  }

  .audio-header {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-bottom: 8px;
  }

  .audio-icon {
    width: 32px;
    height: 32px;
    border-radius: 8px;
    background: var(--color-primary);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 1.2rem;
    color: white;
  }

  .audio-info {
    flex: 1;
    min-width: 0;
  }

  .audio-name {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .audio-meta {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }

  .audio-actions {
    display: flex;
    gap: 4px;
  }

  .audio-player {
    width: 100%;
    height: 32px;
  }

  /* File Attachments */
  .file-attachment {
    background: var(--color-surface);
    border-radius: 12px;
    padding: 12px;
    border: 1px solid var(--color-border);
    display: flex;
    align-items: center;
    gap: 12px;
    transition: background 0.2s ease;
  }

  .file-attachment:hover {
    background: var(--color-border);
  }

  .file-icon {
    width: 40px;
    height: 40px;
    border-radius: 8px;
    background: var(--color-primary);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 1.5rem;
    color: white;
    flex-shrink: 0;
  }

  .file-info {
    flex: 1;
    min-width: 0;
  }

  .file-name {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    margin-bottom: 2px;
  }

  .file-meta {
    display: flex;
    gap: 8px;
    align-items: center;
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }

  .file-type {
    background: var(--color-border);
    padding: 2px 6px;
    border-radius: 4px;
    font-weight: 500;
  }

  .file-actions {
    display: flex;
    gap: 4px;
    flex-shrink: 0;
  }

  .action-btn {
    width: 28px;
    height: 28px;
    border-radius: 6px;
    border: none;
    background: var(--color-border);
    color: var(--color-text-muted);
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .action-btn:hover {
    background: var(--color-border);
    color: var(--color-text);
  }

  .action-btn.preview {
    color: var(--color-primary);
  }

  .action-btn.preview:hover {
    background: var(--color-info-soft);
    color: var(--color-info);
  }

  .action-btn.download {
    color: var(--color-success);
  }

  .action-btn.download:hover {
    background: var(--color-success-soft);
    color: var(--color-success);
  }

  .action-btn.delete {
    color: var(--color-danger);
  }

  .action-btn.delete:hover {
    background: var(--color-danger-soft);
    color: var(--color-danger);
  }

  /* Modal Styles */
  .modal-overlay {
    position: fixed;
    top: 0;
    inset-inline-start: 0;
    inset-inline-end: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.8);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
    padding: 1rem;
  }

  .modal-content {
    background: var(--color-surface);
    border-radius: 16px;
    max-width: 90vw;
    max-height: 90vh;
    overflow: hidden;
    display: flex;
    flex-direction: column;
    box-shadow: 0 20px 25px -5px rgba(0, 0, 0, 0.3);
  }

  .modal-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 1rem 1.5rem;
    border-bottom: 1px solid var(--color-border);
    background: var(--color-surface);
  }

  .modal-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--color-text);
    margin: 0;
  }

  .modal-close {
    width: 32px;
    height: 32px;
    border-radius: 50%;
    border: none;
    background: var(--color-border);
    color: var(--color-text-muted);
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .modal-close:hover {
    background: var(--color-border);
    color: var(--color-text);
  }

  .modal-body {
    flex: 1;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 1rem;
    min-height: 300px;
  }

  .modal-image {
    max-width: 100%;
    max-height: 70vh;
    border-radius: 8px;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  }

  .modal-video {
    max-width: 100%;
    max-height: 70vh;
    border-radius: 8px;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  }

  .audio-modal-container {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1rem;
    padding: 2rem;
    min-width: 300px;
  }

  .audio-modal-icon {
    font-size: 3rem;
    opacity: 0.7;
  }

  .audio-modal-title {
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
    text-align: center;
    margin: 0;
  }

  .modal-audio {
    width: 100%;
    max-width: 400px;
  }

  .modal-pdf {
    width: 80vw;
    height: 70vh;
    min-height: 500px;
    border: none;
    border-radius: 8px;
  }

  .modal-footer {
    display: flex;
    justify-content: flex-end;
    gap: 0.75rem;
    padding: 1rem 1.5rem;
    border-top: 1px solid var(--color-border);
    background: var(--color-surface);
  }

  .modal-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.25rem;
    border-radius: 8px;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s ease;
    border: none;
    font-size: 0.875rem;
  }

  .modal-button.download {
    background: var(--color-success);
    color: white;
  }

  .modal-button.download:hover {
    background: var(--color-success);
  }

  .modal-button.delete {
    background: var(--color-danger);
    color: white;
  }

  .modal-button.delete:hover {
    background: var(--color-danger);
  }

  /* Responsive */
  @media (max-width: 480px) {
    .messenger-attachments {
      max-width: 100%;
    }

    .modal-content {
      margin: 0;
      border-radius: 0;
      max-width: 100vw;
      max-height: 100vh;
    }

    .modal-pdf {
      width: 100vw;
      height: 60vh;
    }
  }
</style>
