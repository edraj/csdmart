<script lang="ts">
  import { RequestType, ResourceType } from "@edraj/tsdmart";
  import { onMount } from "svelte";
  import { _ } from "@/i18n";
  import { getChildren, getChildrenAndSubChildren, getSpaces } from "@/lib/dmart_services";
  import { toasts } from "@/lib/toast";
  import { log } from "@/lib/logger";
  import { ChevronDownOutline, CloseOutline, PlusOutline } from "flowbite-svelte-icons";
  import IconButton from "@/components/ui/IconButton.svelte";

  interface PermissionFormData {
    subpaths: Record<string, string[]>;
    resource_types: string[];
    actions: string[];
    conditions: string[];
    restricted_fields: string[];
    allowed_fields_values: Record<string, unknown>;
    [key: string]: unknown;
  }

  let {
    formData = $bindable(),
    // eslint-disable-next-line @typescript-eslint/no-unused-vars, no-useless-assignment -- $bindable() prop: assigned here, read by the parent through bind:validateFn
    validateFn = $bindable(),
  }: {
    formData: Partial<PermissionFormData>;
    validateFn?: (() => boolean) | null;
  } = $props();

  const uid = $props.id();
  let form = $state<HTMLFormElement | null>(null);

  formData = {
    ...formData,
    subpaths: formData.subpaths ?? {},
    resource_types: formData.resource_types ?? [],
    actions: formData.actions ?? [],
    conditions: formData.conditions ?? [],
    restricted_fields: formData.restricted_fields ?? [],
    allowed_fields_values: formData.allowed_fields_values ?? {},
  };

  const resourceTypeOptions = Object.entries(ResourceType).map(([name, value]) => ({ name, value: String(value) }));
  const requestTypeOptions = [
    { name: "query", value: "query" },
    { name: "view", value: "view" },
    ...Object.entries(RequestType).map(([name, value]) => ({ name, value: String(value) })),
  ];

  let selectedResourceType = $state("");
  let selectedAction = $state("");
  let newCondition = $state("");
  let newRestrictedField = $state("");

  let spaces = $state<string[]>([]);
  let subpaths = $state<string[]>([]);
  let selectedSpace = $state("");
  let selectedSubpath = $state("");
  let loadingSpaces = $state(true);
  let loadingSubpaths = $state(false);

  type Section = "subpaths" | "conditions" | "restrictedFields" | "allowedFields";
  let openSections = $state<Record<Section, boolean>>({
    subpaths: false,
    conditions: false,
    restrictedFields: false,
    allowedFields: false,
  });

  let jsonEditorContent = $state(JSON.stringify(formData.allowed_fields_values ?? {}, null, 2));

  onMount(async () => {
    try {
      const spacesResponse = await getSpaces(true);
      spaces = ["__all_spaces__", ...spacesResponse.records.map((space) => space.shortname)];
    } catch (error) {
      log.error("Failed to load spaces:", error);
    } finally {
      loadingSpaces = false;
    }
  });

  function addTo(key: "resource_types" | "actions" | "conditions" | "restricted_fields", value: string) {
    const clean = value.trim();
    const list = formData[key] ?? [];
    if (clean && !list.includes(clean)) formData[key] = [...list, clean];
  }

  function removeFrom(key: "resource_types" | "actions" | "conditions" | "restricted_fields", value: string) {
    formData[key] = (formData[key] ?? []).filter((i) => i !== value);
  }

  async function loadSubpaths(spaceName: string) {
    if (!spaceName) return;
    loadingSubpaths = true;
    const found: string[] = [];
    try {
      const childSubpaths = await getChildren(spaceName, "/");
      await getChildrenAndSubChildren(found, spaceName, "", childSubpaths);
    } catch (error) {
      log.error("Failed to load subpaths:", error);
    } finally {
      subpaths = ["/", "__all_subpaths__", ...found];
      loadingSubpaths = false;
    }
  }

  function addSubpathToSpace() {
    if (!selectedSpace || !selectedSubpath) return;
    const current = formData.subpaths ?? {};
    const list = current[selectedSpace] ?? [];
    if (!list.includes(selectedSubpath)) {
      formData.subpaths = { ...current, [selectedSpace]: [...list, selectedSubpath] };
    }
    selectedSubpath = "";
  }

  function removeSubpath(space: string, subpath: string) {
    const current = { ...(formData.subpaths ?? {}) };
    const remaining = (current[space] ?? []).filter((p) => p !== subpath);
    if (remaining.length === 0) delete current[space];
    else current[space] = remaining;
    formData.subpaths = current;
  }

  function applyJson(): boolean {
    try {
      formData.allowed_fields_values = JSON.parse(jsonEditorContent || "{}");
      return true;
    } catch {
      return false;
    }
  }

  function saveJsonEditor() {
    if (!applyJson()) toasts.error($_("errors.invalid_json"));
  }

  function validate() {
    if (!applyJson()) {
      toasts.error($_("validation.json_syntax_error"));
      return false;
    }
    if (!form) return true;
    const isValid = form.checkValidity();
    if (!isValid) form.reportValidity();
    return isValid;
  }

  function toggleSection(section: Section) {
    openSections[section] = !openSections[section];
  }

  $effect(() => {
    validateFn = validate;
  });

  $effect(() => {
    if (selectedSpace) loadSubpaths(selectedSpace);
  });

  const subpathEntries = $derived(Object.entries(formData.subpaths ?? {}));

  const selectClass =
    "h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary disabled:opacity-60";
  const inputClass =
    "h-9 px-3 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary";

  const sections: Array<{ id: Section; label: () => string }> = [
    { id: "subpaths", label: () => $_("sections.subpaths") },
    { id: "conditions", label: () => $_("sections.conditions") },
    { id: "restrictedFields", label: () => $_("sections.restricted_fields") },
    { id: "allowedFields", label: () => $_("sections.allowed_fields_values") },
  ];
</script>

{#snippet tagList(items: string[], onRemove: (item: string) => void, tone: "primary" | "warning" | "danger" | "neutral" = "primary")}
  {#if items.length > 0}
    <ul class="flex flex-wrap gap-2 mt-3 list-none p-0 m-0">
      {#each items as item (item)}
        <li
          class="inline-flex items-center gap-1 ps-3 pe-1 py-1 rounded-full text-xs font-medium
 {tone === 'primary' ? 'bg-primary-soft text-primary' : tone === 'warning' ? 'bg-warning-soft text-warning' : tone === 'danger' ? 'bg-danger-soft text-danger' : 'bg-surface-3 text-text-muted'}"
        >
          <span>{item}</span>
          <button
            type="button"
            class="w-5 h-5 inline-flex items-center justify-center rounded-full hover:bg-surface-2/60 cursor-pointer"
            aria-label="{$_('remove')} {item}"
            onclick={() => onRemove(item)}
          >
            <CloseOutline size="xs" aria-hidden="true" />
          </button>
        </li>
      {/each}
    </ul>
  {/if}
{/snippet}

<div>
  <h2 class="text-lg font-semibold text-text mb-5">{$_("permission_form.title")}</h2>

  <form bind:this={form} onsubmit={(e) => e.preventDefault()}>
    <div class="mb-6">
      <label class="block text-sm font-medium text-text mb-1.5" for="{uid}-resourceType">{$_("permissions.resource_types")}</label>
      <div class="flex gap-2">
        <select class="{selectClass} flex-1" bind:value={selectedResourceType} id="{uid}-resourceType">
          <option value="">{$_("options.select_resource_type")}</option>
          {#each resourceTypeOptions as option (option.value)}
            <option value={option.value}>{option.name}</option>
          {/each}
        </select>
        <IconButton label={$_("permission_form.add_resource_type")} variant="outline" onclick={() => { addTo("resource_types", selectedResourceType); selectedResourceType = ""; }} disabled={!selectedResourceType}>
          <PlusOutline size="sm" />
        </IconButton>
      </div>
      {@render tagList(formData.resource_types ?? [], (item) => removeFrom("resource_types", item), "primary")}
    </div>

    <div class="mb-6">
      <label class="block text-sm font-medium text-text mb-1.5" for="{uid}-action">{$_("permissions.actions")}</label>
      <div class="flex gap-2">
        <select class="{selectClass} flex-1" bind:value={selectedAction} id="{uid}-action">
          <option value="">{$_("options.select_action")}</option>
          {#each requestTypeOptions as option (option.value)}
            <option value={option.value}>{option.name}</option>
          {/each}
        </select>
        <IconButton label={$_("permission_form.add_action")} variant="outline" onclick={() => { addTo("actions", selectedAction); selectedAction = ""; }} disabled={!selectedAction}>
          <PlusOutline size="sm" />
        </IconButton>
      </div>
      {@render tagList(formData.actions ?? [], (item) => removeFrom("actions", item), "warning")}
    </div>

    <div class="border-t border-border divide-y divide-border">
      {#each sections as section (section.id)}
        <div>
          <h3 class="m-0">
            <button
              type="button"
              class="w-full flex items-center justify-between gap-3 py-3 text-sm font-semibold text-text cursor-pointer"
              aria-expanded={openSections[section.id]}
              aria-controls="{uid}-{section.id}"
              onclick={() => toggleSection(section.id)}
            >
              <span>{section.label()}</span>
              <ChevronDownOutline size="sm" class="text-text-faint transition-transform {openSections[section.id] ? 'rotate-180' : ''}" aria-hidden="true" />
            </button>
          </h3>

          {#if openSections[section.id]}
            <div id="{uid}-{section.id}" class="pb-4 pt-1">
              {#if section.id === "subpaths"}
                <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                  <div>
                    <label class="block text-xs font-medium text-text-muted mb-1" for="{uid}-space">{$_("fields.space")}</label>
                    {#if loadingSpaces}
                      <div class="flex items-center gap-2 text-sm text-text-muted h-9">
                        <span class="spinner spinner-xs" aria-hidden="true"></span>
                        <span>{$_("loading.spaces")}</span>
                      </div>
                    {:else}
                      <select class="{selectClass} w-full" bind:value={selectedSpace} id="{uid}-space">
                        <option value="">{$_("options.select_space")}</option>
                        {#each spaces as space (space)}
                          <option value={space}>{space}</option>
                        {/each}
                      </select>
                    {/if}
                  </div>

                  <div>
                    <label class="block text-xs font-medium text-text-muted mb-1" for="{uid}-subpath">{$_("fields.subpath")}</label>
                    {#if loadingSubpaths}
                      <div class="flex items-center gap-2 text-sm text-text-muted h-9">
                        <span class="spinner spinner-xs" aria-hidden="true"></span>
                        <span>{$_("loading.subpaths")}</span>
                      </div>
                    {:else}
                      <div class="flex gap-2">
                        <select class="{selectClass} flex-1" bind:value={selectedSubpath} disabled={!selectedSpace} id="{uid}-subpath">
                          <option value="">{$_("options.select_subpath")}</option>
                          {#each subpaths as subpath (subpath)}
                            <option value={subpath}>{subpath}</option>
                          {/each}
                        </select>
                        <IconButton label={$_("permission_form.add_subpath")} variant="outline" onclick={addSubpathToSpace} disabled={!selectedSpace || !selectedSubpath}>
                          <PlusOutline size="sm" />
                        </IconButton>
                      </div>
                    {/if}
                  </div>
                </div>

                {#if subpathEntries.length > 0}
                  <div class="rounded-card border border-border bg-surface p-4 mt-4 space-y-3">
                    {#each subpathEntries as [space, paths] (space)}
                      <div>
                        <div class="text-sm font-semibold text-primary mb-1">{space}</div>
                        {@render tagList(Array.isArray(paths) ? paths : [], (path) => removeSubpath(space, path), "neutral")}
                      </div>
                    {/each}
                  </div>
                {/if}
              {:else if section.id === "conditions"}
                <div class="flex gap-2">
                  <label for="{uid}-condition" class="sr-only">{$_("options.select_condition")}</label>
                  <select class="{selectClass} flex-1" bind:value={newCondition} id="{uid}-condition">
                    <option value="">{$_("options.select_condition")}</option>
                    <option value="own">{$_("conditions.own")}</option>
                    <option value="is_active">{$_("conditions.is_active")}</option>
                  </select>
                  <IconButton label={$_("permission_form.add_condition")} variant="outline" onclick={() => { addTo("conditions", newCondition); newCondition = ""; }} disabled={!newCondition}>
                    <PlusOutline size="sm" />
                  </IconButton>
                </div>
                {@render tagList(formData.conditions ?? [], (item) => removeFrom("conditions", item), "warning")}
              {:else if section.id === "restrictedFields"}
                <div class="flex gap-2">
                  <label for="{uid}-restricted" class="sr-only">{$_("placeholders.restricted_field")}</label>
                  <input
                    type="text"
                    class="{inputClass} flex-1"
                    placeholder={$_("placeholders.restricted_field")}
                    bind:value={newRestrictedField}
                    id="{uid}-restricted"
                    onkeydown={(e) => {
                      if (e.key === "Enter") {
                        e.preventDefault();
                        addTo("restricted_fields", newRestrictedField);
                        newRestrictedField = "";
                      }
                    }}
                  />
                  <IconButton label={$_("permission_form.add_restricted_field")} variant="outline" onclick={() => { addTo("restricted_fields", newRestrictedField); newRestrictedField = ""; }} disabled={!newRestrictedField.trim()}>
                    <PlusOutline size="sm" />
                  </IconButton>
                </div>
                {@render tagList(formData.restricted_fields ?? [], (item) => removeFrom("restricted_fields", item), "danger")}
              {:else}
                <label class="block text-xs font-medium text-text-muted mb-1" for="{uid}-json">{$_("fields.json_editor")}</label>
                <p class="text-xs text-text-faint mb-2">{$_("help.json_editor")}</p>
                <textarea
                  class="w-full min-h-48 p-3 font-mono text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
                  bind:value={jsonEditorContent}
                  id="{uid}-json"
                  spellcheck="false"
                ></textarea>
                <div class="flex justify-end mt-2">
                  <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={saveJsonEditor}>
                    {$_("buttons.apply_changes")}
                  </button>
                </div>
              {/if}
            </div>
          {/if}
        </div>
      {/each}
    </div>
  </form>
</div>
