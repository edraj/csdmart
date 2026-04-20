<script lang="ts">
  import { _ } from "@/i18n";
  import {
    getPreviewUrl,
    getFileIcon,
    formatFileSize,
    formatRecordingDuration,
  } from "@/lib/utils/messagingUtils";

  interface Props {
    currentMessage: string;
    selectedAttachments: File[];
    isConnected: boolean;
    isRecording: boolean;
    isAttachmentLoading: boolean;
    recordingDuration: number;
    placeholder?: string;
    onMessageChange: (value: string) => void;
    onSend: () => void;
    onKeydown: (event: KeyboardEvent) => void;
    onFileSelect: (event: Event) => void;
    onRemoveAttachment: (index: number) => void;
    onStartRecording: () => void;
    onStopRecording: () => void;
    onCancelRecording: () => void;
  }

  let {
    currentMessage,
    selectedAttachments,
    isConnected,
    isRecording,
    isAttachmentLoading,
    recordingDuration,
    placeholder = "Type a message...",
    onMessageChange,
    onSend,
    onKeydown,
    onFileSelect,
    onRemoveAttachment,
    onStartRecording,
    onStopRecording,
    onCancelRecording,
  }: Props = $props();

  function handleInput(event: Event) {
    const target = event.target as HTMLTextAreaElement;
    onMessageChange(target.value);
  }

  function canSend() {
    return (
      (currentMessage.trim() || selectedAttachments.length > 0) &&
      isConnected &&
      !isAttachmentLoading &&
      !isRecording
    );
  }
</script>

<div class="message-input-container">
  <!-- Attachment Preview -->
  {#if selectedAttachments.length > 0}
    <div class="attachment-preview-container">
      {#each selectedAttachments as file, index}
        <div class="attachment-preview-item">
          {#if file.type.startsWith("audio/") && file.name.includes("voice_message_")}
            <div class="voice-message-icon">🎤</div>
            <div class="file-info">
              <div class="file-name">{$_("messaging.voice_message")}</div>
              <div class="file-size">{formatFileSize(file.size)}</div>
            </div>
          {:else if getPreviewUrl(file)}
            <img
              src={getPreviewUrl(file)}
              alt={file.name}
              class="preview-image"
            />
            <div class="file-info">
              <div class="file-name">{file.name}</div>
              <div class="file-size">{formatFileSize(file.size)}</div>
            </div>
          {:else}
            <div class="file-icon">{getFileIcon(file)}</div>
            <div class="file-info">
              <div class="file-name">{file.name}</div>
              <div class="file-size">{formatFileSize(file.size)}</div>
            </div>
          {/if}
          <button
            class="remove-attachment-btn"
            onclick={() => onRemoveAttachment(index)}
            aria-label={$_("messaging.remove_attachment")}
          >
            ✕
          </button>
        </div>
      {/each}
    </div>
  {/if}

  <div class="message-input">
    <button
      class="attachment-btn"
      onclick={() => document.getElementById("attachment-input")?.click()}
      disabled={!isConnected || isRecording}
      aria-label={$_("messaging.add_attachment")}
    >
      📎
    </button>

    <input
      type="file"
      id="attachment-input"
      multiple
      accept="image/*,video/*,audio/*,.pdf,.doc,.docx,.txt"
      onchange={onFileSelect}
      style="display: none;"
    />

    <!-- Voice Recording Button -->
    {#if !isRecording}
      <button
        class="voice-btn"
        onclick={onStartRecording}
        disabled={!isConnected}
        aria-label={$_("messaging.record_voice_message")}
        title={$_("messaging.record_voice_message")}
      >
        🎤
      </button>
    {:else}
      <div class="voice-recording-controls">
        <div class="recording-indicator">
          <div class="recording-dot"></div>
          <span class="recording-duration"
            >{formatRecordingDuration(recordingDuration)}</span
          >
        </div>
        <button
          class="stop-recording-btn"
          onclick={onStopRecording}
          aria-label={$_("messaging.stop_recording")}
        >
          ⏹️
        </button>
        <button
          class="cancel-recording-btn"
          onclick={onCancelRecording}
          aria-label={$_("messaging.cancel_recording")}
        >
          ❌
        </button>
      </div>
    {/if}

    <textarea
      value={currentMessage}
      oninput={handleInput}
      {placeholder}
      disabled={!isConnected || isRecording}
      rows="1"
      onkeydown={onKeydown}
    ></textarea>

    <div class="input-actions">
      <button
        class="send-btn"
        onclick={onSend}
        disabled={!canSend()}
        aria-label={$_("messaging.send_message_btn")}
      >
        {#if isAttachmentLoading}
          <div class="loading-spinner"></div>
        {:else}
          <svg
            width="20"
            height="20"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
          >
            <path d="m22 2-7 20-4-9-9-4 20-7z" />
          </svg>
        {/if}
      </button>
    </div>
  </div>
</div>

<style>
  .message-input-container {
    background: var(--surface-card);
    border-top: 1px solid var(--color-gray-200);
    padding: 1rem;
  }

  .attachment-preview-container {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    margin-bottom: 1rem;
    max-height: 150px;
    overflow-y: auto;
  }

  .attachment-preview-item {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem;
    background: var(--color-gray-50);
    border: 1px solid var(--color-gray-200);
    border-radius: var(--radius-md);
    position: relative;
    max-width: 200px;
  }

  .preview-image {
    width: 40px;
    height: 40px;
    border-radius: var(--radius-sm);
    object-fit: cover;
  }

  .file-icon,
  .voice-message-icon {
    font-size: 1.5rem;
    display: flex;
    align-items: center;
    justify-content: center;
    width: 40px;
    height: 40px;
  }

  .file-info {
    flex: 1;
    min-width: 0;
  }

  .file-name {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-gray-700);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .file-size {
    font-size: 0.75rem;
    color: var(--color-gray-500);
  }

  .remove-attachment-btn {
    position: absolute;
    top: -5px;
    right: -5px;
    background: var(--color-error);
    color: white;
    border: none;
    border-radius: var(--radius-full);
    width: 20px;
    height: 20px;
    font-size: 0.75rem;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    transition: background var(--duration-fast) ease;
  }

  .remove-attachment-btn:hover {
    background: #dc2626;
  }

  .message-input {
    display: flex;
    align-items: flex-end;
    gap: 0.5rem;
    background: var(--color-gray-50);
    border: 1px solid var(--color-gray-200);
    border-radius: var(--radius-xl);
    padding: 0.5rem;
    transition: border-color var(--duration-fast) ease;
  }

  .message-input:focus-within {
    border-color: var(--color-primary-300);
  }

  .attachment-btn,
  .voice-btn {
    background: none;
    border: none;
    padding: 0.5rem;
    border-radius: var(--radius-full);
    cursor: pointer;
    font-size: 1.2rem;
    display: flex;
    align-items: center;
    justify-content: center;
    transition: background-color var(--duration-fast) ease;
  }

  .attachment-btn:hover,
  .voice-btn:hover {
    background: var(--color-gray-200);
  }

  .attachment-btn:disabled,
  .voice-btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .voice-recording-controls {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .recording-indicator {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    background: #fee2e2;
    padding: 0.5rem;
    border-radius: var(--radius-xl);
  }

  .recording-dot {
    width: 8px;
    height: 8px;
    background: var(--color-error);
    border-radius: var(--radius-full);
    animation: pulse 1s infinite;
  }

  .recording-duration {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-error);
  }

  .stop-recording-btn,
  .cancel-recording-btn {
    background: none;
    border: none;
    padding: 0.25rem;
    cursor: pointer;
    font-size: 1rem;
  }

  textarea {
    flex: 1;
    border: none;
    background: transparent;
    resize: none;
    font-size: 1rem;
    padding: 0.5rem;
    min-height: 24px;
    max-height: 120px;
    outline: none;
  }

  .input-actions {
    display: flex;
    align-items: center;
  }

  .send-btn {
    background: var(--color-primary-500);
    color: white;
    border: none;
    border-radius: var(--radius-full);
    width: 40px;
    height: 40px;
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    transition: background-color var(--duration-fast) ease;
  }

  .send-btn:hover:not(:disabled) {
    background: var(--color-primary-600);
  }

  .send-btn:disabled {
    background: var(--color-gray-400);
    cursor: not-allowed;
  }

  .loading-spinner {
    width: 16px;
    height: 16px;
    border: 2px solid transparent;
    border-top: 2px solid currentColor;
    border-radius: 50%;
    animation: spin 1s linear infinite;
  }

  @keyframes spin {
    to {
      transform: rotate(360deg);
    }
  }

  @keyframes pulse {
    0%,
    100% {
      opacity: 1;
    }
    50% {
      opacity: 0.5;
    }
  }
</style>
