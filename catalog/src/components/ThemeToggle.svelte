<script lang="ts">
  import { DesktopPcOutline, MoonOutline, SunOutline } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";
  import DropdownMenu, { type MenuItem } from "@/components/ui/DropdownMenu.svelte";
  import {
    isThemePreference,
    resolvedTheme,
    setTheme,
    themePreference,
  } from "@/lib/theme";

  // Light / dark / system, persisted in localStorage by lib/theme.ts.
  let { class: className = "" }: { class?: string } = $props();

  const items = $derived<MenuItem[]>([
    { id: "light", label: $_("theme.light"), icon: SunOutline },
    { id: "dark", label: $_("theme.dark"), icon: MoonOutline },
    { id: "system", label: $_("theme.system"), icon: DesktopPcOutline },
  ]);

  function select(id: string) {
    if (isThemePreference(id)) setTheme(id);
  }
</script>

<DropdownMenu
  label={$_("theme.change")}
  {items}
  selected={$themePreference}
  onSelect={select}
  class={className}
>
  {#snippet trigger()}
    {#if $resolvedTheme === "dark"}
      <MoonOutline size="md" aria-hidden="true" />
    {:else}
      <SunOutline size="md" aria-hidden="true" />
    {/if}
  {/snippet}
</DropdownMenu>
