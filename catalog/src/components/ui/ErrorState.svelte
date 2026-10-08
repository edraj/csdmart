<script lang="ts">
  import type { Snippet } from "svelte";
  import { ExclamationCircleOutline } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";
  import { apiErrorKey } from "@/lib/apiError";

  // The one way a page says "that failed": a titled, readable message with an
  // optional retry. `error` may be anything that was thrown; an axios error is
  // turned into the matching `errors.*` message instead of its raw text.
  let {
    title,
    error,
    message,
    onRetry,
    retryLabel,
    compact = false,
    class: className = "",
    children,
  }: {
    title?: string;
    /** Whatever was caught. A string is shown as-is; anything else is classified. */
    error?: unknown;
    /** Explicit text; wins over `error`. */
    message?: string;
    onRetry?: () => void;
    retryLabel?: string;
    /** Inline banner instead of a centred block. */
    compact?: boolean;
    class?: string;
    children?: Snippet;
  } = $props();

  const text = $derived(
    message ?? (typeof error === "string" ? error : error ? $_(apiErrorKey(error)) : ""),
  );
</script>

<div
  role="alert"
  class="rounded-card border border-danger/30 bg-danger-soft text-text
    {compact ? 'flex items-start gap-3 p-3' : 'flex flex-col items-center text-center px-6 py-10'} {className}"
>
  <div
    class="shrink-0 text-danger {compact
      ? 'mt-0.5'
      : 'w-12 h-12 mb-3 rounded-full bg-surface-2 flex items-center justify-center'}"
    aria-hidden="true"
  >
    <ExclamationCircleOutline size={compact ? "md" : "lg"} />
  </div>
  <div class="min-w-0 {compact ? 'grow' : ''}">
    <p class="font-semibold {compact ? 'text-sm' : 'text-base'}">
      {title ?? $_("ui.something_went_wrong")}
    </p>
    {#if text}
      <p class="mt-1 text-sm text-text-muted break-words whitespace-pre-wrap">{text}</p>
    {/if}
    {#if children}
      <div class="mt-3">{@render children()}</div>
    {/if}
    {#if onRetry}
      <div class="mt-3">
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={onRetry}>
          {retryLabel ?? $_("ui.retry")}
        </button>
      </div>
    {/if}
  </div>
</div>
