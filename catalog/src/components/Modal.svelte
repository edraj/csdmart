<script lang="ts">
  import type { Snippet } from "svelte";
  import { locale, _ } from "@/i18n";
  import { derived as derivedStore } from "svelte/store";

  type ModalSize = "sm" | "md" | "lg" | "xl" | "2xl" | "3xl" | "4xl";

  interface Props {
    onClose?: () => void;
    title?: string;
    ariaLabel?: string;
    size?: ModalSize;
    dismissable?: boolean;
    showClose?: boolean;
    contentScroll?: boolean;
    icon?: Snippet;
    headerActions?: Snippet;
    footer?: Snippet;
    children?: Snippet;
  }

  let {
    onClose = () => {},
    title,
    ariaLabel,
    size = "xl",
    dismissable = true,
    showClose = true,
    contentScroll = true,
    icon,
    headerActions,
    footer,
    children,
  }: Props = $props();

  const isRTL = derivedStore(
    locale,
    (val: any) => val === "ar" || val === "ku",
  );

  const SIZE_MAX_WIDTH: Record<ModalSize, string> = {
    sm: "24rem",
    md: "28rem",
    lg: "32rem",
    xl: "36rem",
    "2xl": "42rem",
    "3xl": "48rem",
    "4xl": "56rem",
  };

  function requestClose() {
    if (!dismissable) return;
    onClose();
  }

  function handleBackdropClick(e: MouseEvent) {
    if (e.target === e.currentTarget) requestClose();
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === "Escape") {
      e.stopPropagation();
      requestClose();
    }
  }

  $effect(() => {
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  });

  const hasHeader = $derived(
    Boolean(title) || Boolean(icon) || Boolean(headerActions) || showClose,
  );
</script>

<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<!-- svelte-ignore a11y_click_events_have_key_events -->
<div
  class="fixed inset-0 z-[60] flex items-center justify-center p-4 bg-black/50 backdrop-blur-sm app-modal-backdrop"
  class:rtl={$isRTL}
  onclick={handleBackdropClick}
  onkeydown={handleKeydown}
  role="dialog"
  aria-modal="true"
  aria-label={ariaLabel || title || "Dialog"}
  tabindex="-1"
>
  <div
    class="bg-white rounded-[24px] shadow-2xl w-full overflow-hidden border border-gray-100 flex flex-col max-h-[90vh] app-modal-container"
    style="max-width: {SIZE_MAX_WIDTH[size]}"
    role="document"
  >
    {#if hasHeader}
      <div
        class="p-6 border-b border-gray-100 flex items-center justify-between bg-white shrink-0 app-modal-header"
      >
        <div class="flex items-center gap-3 min-w-0 flex-1">
          {#if icon}
            <div
              class="w-10 h-10 bg-indigo-50 rounded-xl flex items-center justify-center text-indigo-600 shrink-0"
            >
              {@render icon()}
            </div>
          {/if}
          {#if title}
            <h2 class="text-xl font-bold text-gray-900 truncate">{title}</h2>
          {/if}
        </div>
        <div class="flex items-center gap-2 shrink-0">
          {#if headerActions}
            {@render headerActions()}
          {/if}
          {#if showClose && dismissable}
            <button
              onclick={requestClose}
              aria-label={$_("common.close") || "Close"}
              class="p-2 text-gray-400 hover:text-gray-600 hover:bg-gray-50 rounded-lg transition-colors"
              type="button"
            >
              <svg
                class="w-6 h-6"
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
      </div>
    {/if}

    <div
      class="p-6 bg-gray-50/30 flex-1 app-modal-content {contentScroll
        ? 'overflow-y-auto'
        : ''}"
    >
      {#if children}
        {@render children()}
      {/if}
    </div>

    {#if footer}
      <div
        class="p-6 border-t border-gray-100 flex items-center justify-end gap-3 bg-white shrink-0 app-modal-footer"
      >
        {@render footer()}
      </div>
    {/if}
  </div>
</div>

<style>
  .rtl {
    direction: rtl;
  }
</style>
