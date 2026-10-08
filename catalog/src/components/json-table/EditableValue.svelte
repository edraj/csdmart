<script lang="ts">
  import { _ } from "@/i18n";
  import type { JsonTableTheme, JsonPrimitive } from "./types";

  interface Props {
    value: JsonPrimitive;
    theme: JsonTableTheme;
    onUpdate: (value: JsonPrimitive) => void;
  }

  let { value, theme: t, onUpdate }: Props = $props();

  let editing: boolean = $state(false);
  let hover: boolean = $state(false);
  let draft: string = $state("");
  let copied: boolean = $state(false);
  let inputEl: HTMLTextAreaElement | null = $state(null);

  type ValueType = "null" | "bool-true" | "bool-false" | "number" | "string";

  const valueType: ValueType = $derived(
    value === null ? "null"
    : typeof value === "boolean" ? (value ? "bool-true" : "bool-false")
    : typeof value === "number" ? "number"
    : "string"
  );

  const displayText: string = $derived(value === null ? "null" : String(value));

  const color: string = $derived(
    valueType === "bool-true" ? t.boolTrue
    : valueType === "bool-false" ? t.boolFalse
    : valueType === "number" ? t.number
    : valueType === "null" ? t.nullColor
    : t.string
  );

  function parseInput(raw: string, originalType: string): JsonPrimitive {
    const trimmed = raw.trim();
    if (trimmed === "null") return null;
    if (trimmed === "true") return true;
    if (trimmed === "false") return false;
    if (originalType === "number" && !isNaN(Number(trimmed)) && trimmed !== "") {
      return Number(trimmed);
    }
    return raw;
  }

  function startEdit(): void {
    draft = value === null ? "null" : String(value);
    editing = true;
  }

  function commit(): void {
    editing = false;
    const parsed = parseInput(draft, typeof value);
    if (parsed !== value) onUpdate(parsed);
  }

  function cancel(): void {
    editing = false;
  }

  function handleClick(): void {
    if (!editing && typeof value !== "boolean") {
      startEdit();
    }
  }

  function handleBoolToggle(e: MouseEvent): void {
    e.stopPropagation();
    onUpdate(!value);
  }

  function handleCopy(e: MouseEvent): void {
    e.stopPropagation();
    const text = value === null ? "null" : String(value);
    navigator.clipboard.writeText(text).then(() => {
      copied = true;
      setTimeout(() => { copied = false; }, 1200);
    });
  }

  function handleKeydown(e: KeyboardEvent): void {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      commit();
    }
    if (e.key === "Escape") cancel();
  }

  $effect(() => {
    if (editing && inputEl) {
      inputEl.focus();
      inputEl.select();
    }
  });
</script>

<span
  class="ev-wrap"
  class:ev-hover={!editing && hover}
  role="button"
  tabindex="0"
  onclick={handleClick}
  onkeydown={(e) => {
    if (!editing && (e.key === "Enter" || e.key === " ")) {
      e.preventDefault();
      handleClick();
    }
  }}
  onmouseenter={() => hover = true}
  onmouseleave={() => hover = false}
  onfocus={() => hover = true}
  onblur={() => hover = false}
  style:--ev-hover-bg={t.hoverBg}
  style:cursor={editing ? "default" : typeof value === "boolean" ? "pointer" : "text"}
>
  {#if typeof value === "boolean" && !editing}
    <button type="button" class="ev-text ev-control" style:color onclick={handleBoolToggle}>{displayText}</button>
  {:else}
    <span
      class="ev-text"
      class:ev-null={valueType === "null"}
      style:color
      style:visibility={editing ? "hidden" : "visible"}
    >
      {displayText}
    </span>
  {/if}

  {#if !editing && hover}
    <button
      type="button"
      class="ev-copy ev-control"
      class:ev-copied={copied}
      style:--copy-color={copied ? t.string : t.idxColor}
      style:--copy-bg={t.themeName === "dark" ? "rgba(255,255,255,0.04)" : "rgba(0,0,0,0.04)"}
      onclick={handleCopy}
      title={$_("labels.copy_value")}
      aria-label={$_("labels.copy_value")}
    >
      {copied ? "✓" : "⎘"}
    </button>
  {/if}

  {#if editing}
    <textarea
      bind:this={inputEl}
      bind:value={draft}
      onblur={commit}
      onkeydown={handleKeydown}
      class="ev-input"
      rows="1"
      style:--edit-bg={t.editBg}
      style:--edit-border={t.editBorder}
      style:color={t.text}
    ></textarea>
  {/if}
</span>

<style>
  .ev-wrap {
    position: relative;
    display: block;
    border-radius: 3px;
    padding: 0 4px;
    padding-inline-end: 24px;
    background: transparent;
    transition: background 0.12s;
  }
  .ev-hover {
    background: var(--ev-hover-bg);
  }
  .ev-text {
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    min-width: 32px;
    display: inline-block;
  }
  .ev-null {
    font-style: italic;
  }
  .ev-copy {
    position: absolute;
    inset-inline-end: 4px;
    top: 50%;
    transform: translateY(-50%);
    cursor: pointer;
    font-size: 11px;
    color: var(--copy-color);
    opacity: 0.6;
    transition: opacity 0.15s, color 0.15s;
    padding: 2px 4px;
    border-radius: 3px;
    background: var(--copy-bg);
    line-height: 1;
    user-select: none;
  }
  .ev-copy:hover {
    opacity: 1;
  }
  .ev-copied {
    opacity: 1;
  }
  .ev-input {
    position: absolute;
    top: -2px;
    inset-inline-start: -2px;
    inset-inline-end: -2px;
    bottom: -2px;
    background: var(--edit-bg);
    border: 1.5px solid var(--edit-border);
    border-radius: 3px;
    padding: 2px 6px;
    font-size: inherit;
    font-family: inherit;
    line-height: inherit;
    outline: none;
    box-sizing: border-box;
    resize: none;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .ev-control {
    background: none;
    border: 0;
    padding: 0;
    margin: 0;
    font: inherit;
    color: inherit;
    cursor: pointer;
  }
</style>
