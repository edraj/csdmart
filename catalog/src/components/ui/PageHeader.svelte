<script lang="ts">
  import type { Component, Snippet } from "svelte";
  import { ArrowLeftOutline } from "flowbite-svelte-icons";
  import { withBase } from "@/lib/paths";

  // The one page title block: optional breadcrumb or back link above, title
  // (24/600) with an optional description (14, muted) at the start, actions
  // at the end. Wraps on narrow widths.
  let {
    title,
    description,
    icon,
    iconTone = "primary",
    backHref,
    backLabel,
    onBack,
    breadcrumb,
    actions,
    class: className = "",
  }: {
    title: string;
    description?: string;
    /** Optional leading icon component (flowbite-svelte-icons). */
    icon?: Component<Record<string, unknown>>;
    iconTone?: "primary" | "danger";
    /** App-relative route ("/catalogs/x"); withBase() is applied here. */
    backHref?: string;
    backLabel?: string;
    /** Alternative to backHref: a handler (e.g. history.back). */
    onBack?: () => void;
    /** Rendered above the title instead of / in addition to the back link. */
    breadcrumb?: Snippet;
    actions?: Snippet;
    class?: string;
  } = $props();

  const Icon = $derived(icon);
  const backClass =
    "inline-flex items-center gap-1.5 text-sm text-text-muted hover:text-primary mb-3 transition-colors rounded-control bg-transparent border-0 p-0 cursor-pointer";
</script>

<header class="mb-6 {className}">
  {#if breadcrumb}
    <div class="mb-3">{@render breadcrumb()}</div>
  {/if}
  {#if backLabel && (backHref || onBack)}
    {#if backHref}
      <a href={withBase(backHref)} class={backClass}>
        <ArrowLeftOutline size="sm" class="rtl:rotate-180" aria-hidden="true" />
        <span>{backLabel}</span>
      </a>
    {:else}
      <button type="button" onclick={onBack} class={backClass}>
        <ArrowLeftOutline size="sm" class="rtl:rotate-180" aria-hidden="true" />
        <span>{backLabel}</span>
      </button>
    {/if}
  {/if}
  <div class="flex flex-wrap items-start justify-between gap-x-6 gap-y-3">
    <div class="flex items-start gap-3 min-w-0">
      {#if Icon}
        <div
          class="shrink-0 w-11 h-11 rounded-full flex items-center justify-center
            {iconTone === 'danger' ? 'bg-danger-soft text-danger' : 'bg-primary-soft text-primary'}"
          aria-hidden="true"
        >
          <Icon size="lg" />
        </div>
      {/if}
      <div class="min-w-0">
        <h1 class="text-2xl font-semibold text-text leading-tight break-words">{title}</h1>
        {#if description}
          <p class="mt-1 text-sm text-text-muted">{description}</p>
        {/if}
      </div>
    </div>
    {#if actions}
      <div class="flex flex-wrap items-center gap-2 ms-auto">
        {@render actions()}
      </div>
    {/if}
  </div>
</header>
