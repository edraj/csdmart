<script lang="ts">
  import { LanguageOutline } from "flowbite-svelte-icons";
  import { _, locale, switchLocale } from "@/i18n";
  import { website } from "@/config";
  import DropdownMenu, { type MenuItem } from "@/components/ui/DropdownMenu.svelte";

  // The language control: a real button + menu built from website.languages
  // (code → native name), reachable by keyboard and screen reader. Replaces
  // the "En ▾" that was not in the accessibility tree.
  let { class: className = "" }: { class?: string } = $props();

  const items = $derived<MenuItem[]>(
    Object.entries(website.languages).map(([code, name]) => ({ id: code, label: name })),
  );
  const current = $derived($locale ?? website.default_language);
</script>

<DropdownMenu
  label={$_("ui.change_language")}
  {items}
  selected={current}
  onSelect={switchLocale}
  class={className}
>
  {#snippet trigger()}
    <LanguageOutline size="md" aria-hidden="true" />
    <span class="uppercase text-xs font-semibold tracking-wide" aria-hidden="true">{current}</span>
  {/snippet}
</DropdownMenu>
