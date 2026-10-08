<script lang="ts">
  import type { Snippet } from "svelte";
  import { _ } from "@/i18n";

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

  const SIZE_MAX_WIDTH: Record<ModalSize, string> = {
    sm: "24rem",
    md: "28rem",
    lg: "32rem",
    xl: "36rem",
    "2xl": "42rem",
    "3xl": "48rem",
    "4xl": "56rem",
  };

  const uid = $props.id();
  const titleId = `${uid}-title`;

  let panel: HTMLDivElement | undefined = $state();

  const FOCUSABLE =
    'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

  function focusables(): HTMLElement[] {
    if (!panel) return [];
    return Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
      (el) => el.offsetParent !== null || el === document.activeElement,
    );
  }

  function requestClose() {
    if (!dismissable) return;
    onClose();
  }

  function handleBackdropClick(e: MouseEvent) {
    if (e.target === e.currentTarget) requestClose();
  }

  // Escape closes; Tab and Shift+Tab stay inside the panel.
  function handleKeydown(e: KeyboardEvent) {
    if (e.key === "Escape") {
      e.stopPropagation();
      requestClose();
      return;
    }
    if (e.key !== "Tab") return;
    const els = focusables();
    if (els.length === 0) {
      e.preventDefault();
      panel?.focus();
      return;
    }
    const first = els[0];
    const last = els[els.length - 1];
    const active = document.activeElement;
    if (e.shiftKey && (active === first || active === panel)) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && active === last) {
      e.preventDefault();
      first.focus();
    }
  }

  // Lock page scroll, move focus in (data-autofocus wins, then the first
  // control, then the panel itself) and give it back on close.
  $effect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    const auto = panel?.querySelector<HTMLElement>("[data-autofocus]");
    (auto ?? focusables()[0] ?? panel)?.focus();
    return () => {
      document.body.style.overflow = previousOverflow;
      previouslyFocused?.focus?.();
    };
  });

  const hasHeader = $derived(
    Boolean(title) || Boolean(icon) || Boolean(headerActions) || showClose,
  );
</script>

<div
  class="fixed inset-0 z-[60] flex items-center justify-center p-4 bg-[var(--surface-overlay)] backdrop-blur-sm app-modal-backdrop"
  onclick={handleBackdropClick}
  onkeydown={handleKeydown}
  role="dialog"
  aria-modal="true"
  aria-labelledby={title ? titleId : undefined}
  aria-label={title ? undefined : ariaLabel || $_("ui.dialog")}
  tabindex="-1"
>
  <div
    bind:this={panel}
    class="bg-surface-2 text-text rounded-modal shadow-modal w-full overflow-hidden border border-border flex flex-col max-h-[90vh] outline-none app-modal-container"
    style="max-width: {SIZE_MAX_WIDTH[size]}"
    role="document"
    tabindex="-1"
  >
    {#if hasHeader}
      <div
        class="px-5 py-4 border-b border-border flex items-center justify-between gap-3 shrink-0 app-modal-header"
      >
        <div class="flex items-center gap-3 min-w-0 flex-1">
          {#if icon}
            <div
              class="w-10 h-10 bg-primary-soft rounded-card flex items-center justify-center text-primary shrink-0"
              aria-hidden="true"
            >
              {@render icon()}
            </div>
          {/if}
          {#if title}
            <h2 id={titleId} class="text-lg font-semibold text-text truncate">{title}</h2>
          {/if}
        </div>
        <div class="flex items-center gap-2 shrink-0">
          {#if headerActions}
            {@render headerActions()}
          {/if}
          {#if showClose && dismissable}
            <button
              onclick={requestClose}
              aria-label={$_("common.close")}
              title={$_("common.close")}
              class="p-2 text-text-muted hover:text-text hover:bg-surface-3 rounded-control transition-colors"
              type="button"
            >
              <svg
                class="w-5 h-5"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                aria-hidden="true"
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
      class="p-5 flex-1 app-modal-content {contentScroll ? 'overflow-y-auto' : ''}"
    >
      {#if children}
        {@render children()}
      {/if}
    </div>

    {#if footer}
      <div
        class="px-5 py-4 border-t border-border flex flex-wrap items-center justify-end gap-2 bg-surface shrink-0 app-modal-footer"
      >
        {@render footer()}
      </div>
    {/if}
  </div>
</div>
