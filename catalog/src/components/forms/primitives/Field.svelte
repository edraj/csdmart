<script lang="ts" module>
  import { getContext, setContext } from "svelte";

  const FIELD_CTX = Symbol("catalog.form.field");

  export interface FieldContextValue {
    id: string;
    descriptionId: string | null;
    errorId: string | null;
    hasError: boolean;
  }

  export function getFieldContext(): FieldContextValue | null {
    return (getContext(FIELD_CTX) as FieldContextValue) ?? null;
  }

  export function setFieldContext(value: FieldContextValue): void {
    setContext(FIELD_CTX, value);
  }
</script>

<script lang="ts">
  import type { Snippet } from "svelte";

  interface Props {
    id: string;
    error?: string | null;
    description?: string | null;
    class?: string;
    children: Snippet;
  }

  let { id, error = null, description = null, class: className = "", children }: Props = $props();

  const descriptionId = $derived(description ? `${id}-description` : null);
  const errorId = $derived(error ? `${id}-error` : null);

  $effect(() => {
    setFieldContext({
      id,
      descriptionId,
      errorId,
      hasError: !!error,
    });
  });
</script>

<div class="field {className}">
  {@render children()}

  {#if description}
    <p id={descriptionId} class="field-description">{description}</p>
  {/if}

  {#if error}
    <p id={errorId} class="field-error" role="alert">{error}</p>
  {/if}
</div>

<style>
  .field {
    display: flex;
    flex-direction: column;
    gap: 0.375rem;
  }

  .field-description {
    font-size: var(--font-size-xs);
    color: var(--color-gray-500);
    margin: 0;
  }

  .field-error {
    font-size: var(--font-size-xs);
    color: var(--color-danger-strong);
    font-weight: var(--font-weight-medium);
    margin: 0;
  }
</style>
