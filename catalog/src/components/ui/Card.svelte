<script lang="ts">
  import type { Snippet } from "svelte";

  // Consistent padding/border/radius/hover. A card that navigates is an <a>
  // (pass the final href — withBase(...) applied — so middle-click and
  // open-in-new-tab work); one that acts is a <button>; otherwise a <div>.
  let {
    href,
    onclick,
    padding = "md",
    hover = false,
    class: className = "",
    header,
    footer,
    children,
    ...rest
  }: {
    href?: string;
    onclick?: (event: MouseEvent) => void;
    padding?: "none" | "sm" | "md";
    /** Lift on hover; implied for links and buttons. */
    hover?: boolean;
    class?: string;
    header?: Snippet;
    footer?: Snippet;
    children?: Snippet;
    [key: string]: unknown;
  } = $props();

  const pad = $derived(padding === "none" ? "" : padding === "sm" ? "p-3" : "p-4 sm:p-5");
  const interactive = $derived(hover || !!href || !!onclick);
  const base = $derived(
    `block rounded-card border border-border bg-surface-2 text-text shadow-card text-start ${
      interactive
        ? "transition-[box-shadow,border-color] hover:shadow-modal hover:border-border-strong focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
        : ""
    } ${className}`,
  );
</script>

{#snippet inner()}
  {#if header}
    <div class="px-4 sm:px-5 py-3 border-b border-border">{@render header()}</div>
  {/if}
  <div class={pad}>{@render children?.()}</div>
  {#if footer}
    <div class="px-4 sm:px-5 py-3 border-t border-border">{@render footer()}</div>
  {/if}
{/snippet}

{#if href}
  <a {href} class={base} {...rest}>{@render inner()}</a>
{:else if onclick}
  <button type="button" {onclick} class="{base} w-full cursor-pointer" {...rest}>{@render inner()}</button>
{:else}
  <div class={base} {...rest}>{@render inner()}</div>
{/if}
