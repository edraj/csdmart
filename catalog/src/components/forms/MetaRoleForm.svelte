<script lang="ts">
  import { onMount } from "svelte";
  import { Dmart, QueryType } from "@edraj/tsdmart";
  import { _ } from "@/i18n";
  import { log } from "@/lib/logger";
  import { MANAGEMENT_SPACE } from "@/lib/constants";
  import { CloseOutline, SearchOutline } from "flowbite-svelte-icons";
  import Badge from "@/components/ui/Badge.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  interface RoleFormData {
    permissions?: string[];
    [key: string]: unknown;
  }

  let {
    formData = $bindable(),
    // eslint-disable-next-line no-useless-assignment -- $bindable() prop: assigned here, read by the parent through bind:validateFn
    validateFn = $bindable(),
    fullWidth = false,
  }: {
    formData: RoleFormData;
    validateFn?: (() => boolean) | null;
    fullWidth?: boolean;
  } = $props();

  const uid = $props.id();

  let availablePermissions = $state<string[]>([]);
  let loading = $state(true);
  let searchTerm = $state("");
  let showDropdown = $state(false);
  let dropdownWrapperRef = $state<HTMLDivElement | null>(null);

  if (!formData.permissions) {
    formData.permissions = [];
  }

  const selected = $derived(formData.permissions ?? []);
  const filteredPermissions = $derived.by(() => {
    const term = searchTerm.trim().toLowerCase();
    return term ? availablePermissions.filter((p) => p.toLowerCase().includes(term)) : availablePermissions;
  });

  async function getPermissions() {
    try {
      const response = await Dmart.query({
        space_name: MANAGEMENT_SPACE,
        subpath: "/permissions",
        type: QueryType.search,
        search: "",
        limit: 100,
        retrieve_json_payload: false,
        retrieve_attachments: false,
      });
      availablePermissions = (response?.records ?? []).map((perm) => perm.shortname);
    } catch (error) {
      log.error("Failed to load permissions:", error);
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    getPermissions();
    const handleClickOutside = (event: MouseEvent) => {
      if (dropdownWrapperRef && !dropdownWrapperRef.contains(event.target as Node)) {
        showDropdown = false;
      }
    };
    document.addEventListener("click", handleClickOutside);
    return () => document.removeEventListener("click", handleClickOutside);
  });

  function togglePermission(permission: string) {
    const current = formData.permissions ?? [];
    formData.permissions = current.includes(permission)
      ? current.filter((p) => p !== permission)
      : [...current, permission];
  }

  function removePermission(permission: string) {
    formData.permissions = (formData.permissions ?? []).filter((p) => p !== permission);
  }

  function validate() {
    return (formData.permissions ?? []).length !== 0;
  }

  // eslint-disable-next-line @typescript-eslint/no-unused-vars, no-useless-assignment -- $bindable() prop: assigned here, read by the parent through bind:validateFn
  validateFn = validate;
</script>

<div class={fullWidth ? "" : "rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5"}>
  <h2 class="text-lg font-semibold text-text mb-4">{$_("rolePermissions")}</h2>

  <div>
    <label for="{uid}-permissions-search" class="block text-sm font-medium text-text mb-1.5">
      <span class="text-danger" aria-hidden="true">*</span>
      {$_("permissions.permissions")}
    </label>

    {#if loading}
      <LoadingState variant="skeleton" rows={2} />
    {:else}
      <div class="relative" bind:this={dropdownWrapperRef}>
        <div class="relative">
          <SearchOutline size="sm" class="absolute start-3 top-1/2 -translate-y-1/2 text-text-faint pointer-events-none" aria-hidden="true" />
          <input
            id="{uid}-permissions-search"
            type="search"
            class="w-full h-9 ps-9 pe-3 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary"
            placeholder={$_("searchPermissionsPlaceholder")}
            bind:value={searchTerm}
            role="combobox"
            aria-expanded={showDropdown}
            aria-controls="{uid}-permissions-list"
            aria-autocomplete="list"
            onfocus={() => (showDropdown = true)}
            onkeydown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                showDropdown = false;
              }
              if (e.key === "Escape") showDropdown = false;
            }}
          />
        </div>

        {#if showDropdown && filteredPermissions.length > 0}
          <ul
            id="{uid}-permissions-list"
            role="listbox"
            aria-multiselectable="true"
            class="absolute start-0 end-0 mt-1 z-10 max-h-60 overflow-y-auto rounded-card border border-border bg-surface-2 shadow-modal list-none p-1 m-0"
          >
            {#each filteredPermissions as permission (permission)}
              {@const isSelected = selected.includes(permission)}
              <li>
                <button
                  type="button"
                  role="option"
                  aria-selected={isSelected}
                  class="w-full flex items-center justify-between gap-2 px-3 py-2 text-sm text-start text-text rounded-control hover:bg-surface-3 cursor-pointer"
                  onclick={() => togglePermission(permission)}
                >
                  <span>{permission}</span>
                  {#if isSelected}
                    <Badge variant="primary" size="sm">{$_("selected")}</Badge>
                  {/if}
                </button>
              </li>
            {/each}
          </ul>
        {/if}
      </div>
    {/if}

    {#if selected.length > 0}
      <div class="mt-4">
        <p class="text-sm font-medium text-text mb-2" id="{uid}-added-label">{$_("addedPermissions")}</p>
        <ul class="flex flex-wrap gap-2 list-none p-0 m-0" aria-labelledby="{uid}-added-label">
          {#each selected as permission (permission)}
            <li class="inline-flex items-center gap-1 ps-3 pe-1 py-1 rounded-full text-sm bg-primary-soft text-primary">
              <span>{permission}</span>
              <button
                type="button"
                class="w-6 h-6 inline-flex items-center justify-center rounded-full hover:bg-primary hover:text-text-on-primary transition-colors cursor-pointer"
                aria-label="{$_('remove')} {permission}"
                onclick={() => removePermission(permission)}
              >
                <CloseOutline size="xs" aria-hidden="true" />
              </button>
            </li>
          {/each}
        </ul>
      </div>
    {:else}
      <p class="mt-4 py-6 px-4 text-sm text-center text-text-muted rounded-card border-2 border-dashed border-border">
        {$_("noPermissionsAdded")}
      </p>
    {/if}
  </div>
</div>
