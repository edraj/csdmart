<script lang="ts">
  import type { Snippet } from "svelte";
  import type { JsonTableTheme } from "./types";

  interface Props {
    displayName: string;
    isArray: boolean;
    isOdd: boolean;
    isNested: boolean;
    theme: JsonTableTheme;
    children: Snippet;
  }

  let { displayName, isArray, isOdd, isNested, theme: t, children }: Props = $props();

  let hovered: boolean = $state(false);
</script>

<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<tr
  onmouseenter={() => hovered = true}
  onmouseleave={() => hovered = false}
  style:background={hovered ? t.rowHover : (isOdd ? t.rowOdd : t.rowEven)}
  style:transition="background 0.12s"
>
  <td
    class="tr-key"
    class:tr-array-idx={isArray}
    style:background={hovered ? t.keyHover : (isOdd ? t.keyOdd : t.keyEven)}
    style:color={isArray ? t.idxColor : t.keyColor}
    style:transition="background 0.12s"
  >
    {displayName}
  </td>
  <td class="tr-value" class:tr-value-nested={isNested}>
    {@render children()}
  </td>
</tr>

<style>
  .tr-key {
    padding: 6px 12px;
    font-weight: 500;
    white-space: nowrap;
    vertical-align: top;
  }
  .tr-array-idx {
    font-weight: 400;
    font-style: italic;
    font-size: 0.88em;
  }
  .tr-value {
    padding: 6px 12px;
    vertical-align: top;
  }
  .tr-value-nested {
    padding: 6px 10px;
  }
</style>
