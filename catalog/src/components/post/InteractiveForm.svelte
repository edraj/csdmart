<script lang="ts">
  import { PaperPlaneOutline } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";

  // The "write a comment" box. Enter submits, Shift+Enter breaks the line.
  let {
    newComment = $bindable(""),
    isSubmittingComment = false,
    onAddComment,
  }: { newComment: string; isSubmittingComment: boolean; onAddComment: () => void } = $props();

  const uid = $props.id();
  const canSubmit = $derived(newComment.trim().length > 0 && !isSubmittingComment);

  function submit() {
    if (canSubmit) onAddComment();
  }

  function handleKeydown(event: KeyboardEvent) {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      submit();
    }
  }
</script>

<form
  class="composer"
  onsubmit={(event) => {
    event.preventDefault();
    submit();
  }}
>
  <label for="{uid}-comment" class="sr-only">{$_("post_detail.comments.placeholder")}</label>
  <textarea
    id="{uid}-comment"
    bind:value={newComment}
    placeholder={$_("post_detail.comments.placeholder")}
    class="composer-textarea"
    rows="3"
    disabled={isSubmittingComment}
    onkeydown={handleKeydown}
  ></textarea>

  <div class="flex flex-wrap items-center justify-between gap-2">
    <span class="text-xs text-text-faint">{$_("post_detail.comments.shift_enter_hint")}</span>
    <button type="submit" class="app-btn app-btn-primary app-btn-sm" disabled={!canSubmit} aria-busy={isSubmittingComment}>
      {#if isSubmittingComment}
        <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
      {:else}
        <PaperPlaneOutline size="sm" class="rtl:-scale-x-100" aria-hidden="true" />
      {/if}
      {$_("post_detail.comments.submit")}
    </button>
  </div>
</form>

<style>
  .composer {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  .composer-textarea {
    width: 100%;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-card);
    padding: 0.875rem 1rem;
    font-size: var(--font-size-sm);
    font-family: inherit;
    color: var(--color-text);
    line-height: var(--line-height-normal);
    resize: vertical;
    min-height: 5rem;
  }

  .composer-textarea::placeholder {
    color: var(--color-text-faint);
  }

  .composer-textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 1px var(--color-primary);
  }

  .composer-textarea:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }
</style>
