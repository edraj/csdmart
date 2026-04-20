<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    legend?: string | null;
    class?: string;
    children: Snippet;
  }

  let { legend = null, class: className = "", children }: Props = $props();
</script>

<fieldset class="field-group {className}">
  {#if legend}
    <legend class="field-group-legend">{legend}</legend>
  {/if}
  {@render children()}
</fieldset>

<style>
  .field-group {
    display: flex;
    flex-direction: column;
    gap: 1rem;
    border: 0;
    padding: 0;
    margin: 0;
  }

  .field-group-legend {
    font-size: var(--font-size-base);
    font-weight: var(--font-weight-semibold);
    color: var(--color-gray-800);
    padding: 0;
    margin: 0 0 0.5rem 0;
  }
</style>
