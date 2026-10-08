<script module lang="ts">
  import type { Component } from "svelte";

  export type MenuItem = {
    id: string;
    label: string;
    icon?: Component<Record<string, unknown>>;
  };
</script>

<script lang="ts">
  import { tick, type Snippet } from "svelte";

  // A button that opens a menu of radio-style choices (language, theme).
  // Keyboard: Enter/Space/ArrowDown open and focus the selected item; arrows
  // move (wrapping), Home/End jump, Escape and Tab close, Escape returns focus
  // to the trigger; clicking outside closes.
  let {
    label,
    items,
    selected,
    onSelect,
    align = "end",
    class: className = "",
    trigger,
  }: {
    /** aria-label of the trigger button. */
    label: string;
    items: MenuItem[];
    selected?: string;
    onSelect: (id: string) => void;
    /** Which inline edge of the trigger the menu aligns to. */
    align?: "start" | "end";
    class?: string;
    /** Trigger content (icon and/or short text). */
    trigger: Snippet;
  } = $props();

  const uid = $props.id();
  const menuId = `${uid}-menu`;

  let open = $state(false);
  let root: HTMLDivElement | undefined = $state();
  let triggerEl: HTMLButtonElement | undefined = $state();
  let itemEls: HTMLButtonElement[] = $state([]);

  function focusItem(index: number) {
    const els = itemEls.filter(Boolean);
    if (els.length === 0) return;
    const i = ((index % els.length) + els.length) % els.length;
    els[i]?.focus();
  }

  async function openMenu(focusSelected = true) {
    open = true;
    // Items render on the next tick; focus once they exist.
    await tick();
    const idx = focusSelected ? Math.max(0, items.findIndex((it) => it.id === selected)) : 0;
    focusItem(idx);
  }

  function close(returnFocus = false) {
    open = false;
    if (returnFocus) triggerEl?.focus();
  }

  function toggle() {
    if (open) close();
    else openMenu();
  }

  function onTriggerKeydown(e: KeyboardEvent) {
    if (e.key === "ArrowDown" || e.key === "ArrowUp" || e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      openMenu();
    }
  }

  function onMenuKeydown(e: KeyboardEvent) {
    const els = itemEls.filter(Boolean);
    const current = els.indexOf(document.activeElement as HTMLButtonElement);
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        focusItem(current + 1);
        break;
      case "ArrowUp":
        e.preventDefault();
        focusItem(current - 1);
        break;
      case "Home":
        e.preventDefault();
        focusItem(0);
        break;
      case "End":
        e.preventDefault();
        focusItem(els.length - 1);
        break;
      case "Escape":
        e.preventDefault();
        e.stopPropagation();
        close(true);
        break;
      case "Tab":
        close();
        break;
    }
  }

  function choose(id: string) {
    onSelect(id);
    close(true);
  }

  $effect(() => {
    if (!open) return;
    function onPointerDown(e: PointerEvent) {
      if (root && !root.contains(e.target as Node)) close();
    }
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  });
</script>

<div class="relative inline-flex {className}" bind:this={root}>
  <button
    bind:this={triggerEl}
    type="button"
    class="inline-flex items-center justify-center gap-1 h-9 min-w-9 px-2 rounded-control text-sm font-medium text-text-muted hover:text-text hover:bg-surface-3 transition-colors cursor-pointer"
    aria-label={label}
    title={label}
    aria-haspopup="menu"
    aria-expanded={open}
    aria-controls={open ? menuId : undefined}
    onclick={toggle}
    onkeydown={onTriggerKeydown}
  >
    {@render trigger()}
  </button>

  {#if open}
    <div
      id={menuId}
      role="menu"
      aria-label={label}
      tabindex="-1"
      class="absolute top-full mt-1.5 z-50 min-w-[10rem] py-1 rounded-card border border-border bg-surface-2 shadow-modal {align ===
      'end'
        ? 'end-0'
        : 'start-0'}"
      onkeydown={onMenuKeydown}
    >
      {#each items as item, i (item.id)}
        {@const Icon = item.icon}
        <button
          bind:this={itemEls[i]}
          type="button"
          role="menuitemradio"
          aria-checked={item.id === selected}
          tabindex="-1"
          class="flex w-full items-center gap-2 px-3 py-2 text-sm text-start text-text hover:bg-surface-3 focus:bg-surface-3 focus:outline-none cursor-pointer
            {item.id === selected ? 'font-semibold text-primary' : ''}"
          onclick={() => choose(item.id)}
        >
          {#if Icon}
            <Icon size="sm" aria-hidden="true" />
          {/if}
          <span class="flex-1">{item.label}</span>
          {#if item.id === selected}
            <svg class="w-4 h-4 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7" />
            </svg>
          {/if}
        </button>
      {/each}
    </div>
  {/if}
</div>
