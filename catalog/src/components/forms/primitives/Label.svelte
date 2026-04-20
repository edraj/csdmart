<script lang="ts">
  import type { Snippet } from "svelte";
  import { getFieldContext } from "./Field.svelte";

  interface Props {
    required?: boolean;
    hint?: string | null;
    for?: string;
    class?: string;
    children: Snippet;
  }

  let {
    required = false,
    hint = null,
    for: forAttr,
    class: className = "",
    children,
  }: Props = $props();

  const ctx = getFieldContext();
  const htmlFor = $derived(forAttr || ctx?.id || "");
</script>

<label for={htmlFor} class="field-label {className}">
  <span class="field-label-text">{@render children()}</span>
  {#if required}
    <span class="field-label-required" aria-hidden="true">*</span>
    <span class="sr-only">(required)</span>
  {/if}
  {#if hint}
    <span class="field-label-hint">{hint}</span>
  {/if}
</label>

<style>
  .field-label {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    font-size: var(--font-size-sm);
    font-weight: var(--font-weight-medium);
    color: var(--color-gray-700);
  }

  .field-label-required {
    color: var(--color-danger-strong);
    font-weight: var(--font-weight-semibold);
  }

  .field-label-hint {
    margin-inline-start: auto;
    color: var(--color-gray-400);
    font-size: var(--font-size-xs);
    font-weight: var(--font-weight-regular);
  }
</style>
