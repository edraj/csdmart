<script lang="ts">
  import MetaPermissionForm from "@/components/forms/MetaPermissionForm.svelte";
  import { guardAccess } from "@/lib/guards";
  import { can } from "@/stores/permissions";
  import { toasts } from "@/lib/toast";
  import { confirm } from "@/lib/confirm";
  import { log } from "@/lib/logger";
  import { setTitle } from "@/lib/title";
  import { formatDate } from "@/lib/format";
  import { localized } from "@/lib/catalogItems";
  import { MANAGEMENT_SPACE } from "@/lib/constants";
  import { onMount } from "svelte";
  import { createPermission, deleteEntity, getEntity, getSpaceContents, updatePermission } from "@/lib/dmart_services";
  import { ResourceType, DmartScope } from "@edraj/tsdmart";
  import { _, locale } from "@/i18n";
  import { LockOutline, PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";
  import Modal from "@/components/Modal.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import Card from "@/components/ui/Card.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  const PERMISSIONS_SUBPATH = "permissions";

  interface PermissionEntity {
    shortname: string;
    resource_types?: string[];
    actions?: string[];
    subpaths?: Record<string, string[]>;
    conditions?: string[];
    restricted_fields?: string[];
    allowed_fields_values?: Record<string, unknown>;
  }

  let formData = $state<Record<string, unknown>>({});
  let validateFn = $state<() => boolean>(() => true);
  let isLoading = $state(false);
  let isSaving = $state(false);
  let lastSaved = $state<Date | null>(null);
  let permissionExists = $state(false);
  let currentPermissionShortname = $state("");
  let permissionTypes = $state<Array<{ name: string; value: string }>>([]);
  let selectedPermissionType = $state("");
  let isLoadingPermissions = $state(true);
  let isCreating = $state(false);
  let showAddModal = $state(false);
  let newPermissionName = $state("");

  $effect(() => setTitle($_("user_permissions_management")));

  const infoText = $derived(
    selectedPermissionType === "world"
      ? $_("world_permissions_info")
      : selectedPermissionType === "catalog_user"
        ? $_("catalog_user_permissions_info")
        : $_("permissions_page.info_default", { values: { name: selectedPermissionType } }),
  );

  async function loadPermissionTypes() {
    isLoadingPermissions = true;
    try {
      const response = await getSpaceContents(MANAGEMENT_SPACE, PERMISSIONS_SUBPATH, DmartScope.managed);
      if (response.status === "success") {
        permissionTypes = response.records.map((permission) => ({
          name: localized((permission.attributes as { displayname?: unknown })?.displayname as never, $locale) || permission.shortname,
          value: permission.shortname,
        }));
        if (permissionTypes.length > 0 && !permissionTypes.some((t) => t.value === selectedPermissionType)) {
          selectedPermissionType = permissionTypes[0].value;
        }
      } else {
        toasts.error($_("failed_to_load_permission_types"));
      }
    } catch (error) {
      log.error("Error loading permission types:", error);
      toasts.error($_("failed_to_load_permission_types"));
    } finally {
      isLoadingPermissions = false;
    }
  }

  async function loadPermissionData(permissionType: string) {
    if (!permissionType) return;
    isLoading = true;
    try {
      const entity = (await getEntity(
        permissionType,
        MANAGEMENT_SPACE,
        PERMISSIONS_SUBPATH,
        ResourceType.permission,
        DmartScope.managed,
        true,
        false,
      )) as unknown as PermissionEntity | null;

      if (entity) {
        permissionExists = true;
        currentPermissionShortname = entity.shortname;
        formData = {
          resource_types: entity.resource_types ?? [],
          actions: entity.actions ?? [],
          subpaths: entity.subpaths ?? {},
          conditions: entity.conditions ?? [],
          restricted_fields: entity.restricted_fields ?? [],
          allowed_fields_values: entity.allowed_fields_values ?? {},
        };
      } else {
        permissionExists = false;
        currentPermissionShortname = "";
        formData = {};
      }
    } catch (error) {
      log.error("Error loading permission data:", error);
      toasts.error($_("failed_to_load_permission_data"));
    } finally {
      isLoading = false;
    }
  }

  async function savePermissions(event?: SubmitEvent) {
    event?.preventDefault();
    if (!validateFn()) {
      toasts.error($_("fix_validation_errors"));
      return;
    }
    isSaving = true;
    try {
      const result = await updatePermission(
        currentPermissionShortname,
        MANAGEMENT_SPACE,
        PERMISSIONS_SUBPATH,
        ResourceType.permission,
        {
          tags: ["permission", selectedPermissionType],
          subpaths: formData.subpaths ?? {},
          resource_types: formData.resource_types ?? [],
          actions: formData.actions ?? [],
          conditions: formData.conditions ?? [],
          restricted_fields: formData.restricted_fields ?? [],
          allowed_fields_values: formData.allowed_fields_values ?? {},
        },
        "",
        "",
      );
      if (!result) throw new Error("Failed to save permissions");
      lastSaved = new Date();
      toasts.success($_("permissions_page.saved", { values: { name: selectedPermissionType } }));
    } catch (error) {
      log.error("Error saving permissions:", error);
      toasts.error($_("failed_to_save_permissions"));
    } finally {
      isSaving = false;
    }
  }

  async function createNewPermission(event?: SubmitEvent) {
    event?.preventDefault();
    const name = newPermissionName.trim();
    if (!name) {
      toasts.error($_("enter_permission_name"));
      return;
    }
    isCreating = true;
    try {
      const result = await createPermission(
        {
          shortname: name,
          tags: ["permission"],
          subpaths: {},
          resource_types: [],
          actions: [],
          conditions: [],
          restricted_fields: [],
          allowed_fields_values: {},
        },
        MANAGEMENT_SPACE,
        PERMISSIONS_SUBPATH,
        ResourceType.permission,
        "",
        "",
      );
      if (!result) throw new Error("Failed to create permission");
      toasts.success($_("permissions_page.created", { values: { name } }));
      showAddModal = false;
      newPermissionName = "";
      await loadPermissionTypes();
      selectedPermissionType = result;
    } catch (error) {
      log.error("Error creating permission:", error);
      toasts.error($_("failed_to_create_permission"));
    } finally {
      isCreating = false;
    }
  }

  async function deletePermission() {
    if (!currentPermissionShortname) {
      toasts.error($_("no_permission_selected"));
      return;
    }
    const name = selectedPermissionType;
    const deleted = await confirm({
      title: $_("delete_permission"),
      body: `${$_("are_you_sure_delete_permission")} “${name}”? ${$_("action_cannot_be_undone")}`,
      variant: "danger",
      confirmLabel: $_("delete_permission"),
      action: async () => {
        const ok = await deleteEntity(currentPermissionShortname, MANAGEMENT_SPACE, PERMISSIONS_SUBPATH, ResourceType.permission);
        if (!ok) throw new Error($_("failed_to_delete_permission"));
      },
    });
    if (!deleted) return;
    toasts.success($_("permissions_page.deleted", { values: { name } }));
    selectedPermissionType = "";
    permissionExists = false;
    currentPermissionShortname = "";
    formData = {};
    await loadPermissionTypes();
  }

  onMount(async () => {
    if (!guardAccess("query", MANAGEMENT_SPACE, PERMISSIONS_SUBPATH, ResourceType.permission)) return;
    await loadPermissionTypes();
  });

  $effect(() => {
    if (selectedPermissionType && !isLoadingPermissions) {
      loadPermissionData(selectedPermissionType);
    }
  });
</script>

<div class="mx-auto max-w-5xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("user_permissions_management")} description={$_("configure_access_permissions")} icon={LockOutline}>
    {#snippet actions()}
      {#if $can("create", MANAGEMENT_SPACE, PERMISSIONS_SUBPATH, ResourceType.permission)}
        <button type="button" class="app-btn app-btn-primary" onclick={() => (showAddModal = true)}>
          <PlusOutline size="sm" aria-hidden="true" />
          {$_("add_permission")}
        </button>
      {/if}
      {#if permissionExists && $can("delete", MANAGEMENT_SPACE, PERMISSIONS_SUBPATH, ResourceType.permission)}
        <button type="button" class="app-btn app-btn-danger" onclick={deletePermission}>
          <TrashBinOutline size="sm" aria-hidden="true" />
          {$_("delete")}
        </button>
      {/if}
    {/snippet}
  </PageHeader>

  <Card class="mb-6">
    <h2 class="text-lg font-semibold text-text mb-3">{$_("select_permission_type")}</h2>
    {#if isLoadingPermissions}
      <LoadingState label={$_("loading_permission_data")} class="py-4" />
    {:else if permissionTypes.length === 0}
      <EmptyState icon={LockOutline} title={$_("permissions_page.none_found")} hint={$_("permissions_page.none_available")} />
    {:else}
      <div class="flex flex-col sm:flex-row sm:items-center gap-4">
        <div class="sm:w-80">
          <label for="permission-type" class="sr-only">{$_("select_permission_type")}</label>
          <select
            id="permission-type"
            class="w-full h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
            bind:value={selectedPermissionType}
          >
            {#each permissionTypes as type (type.value)}
              <option value={type.value}>{type.name}</option>
            {/each}
          </select>
        </div>
        <div class="flex flex-wrap items-center gap-2">
          {#if lastSaved}
            <Badge variant="success" size="sm">
              {$_("role_management_page.last_saved", { values: { time: formatDate(lastSaved, "datetime", $locale) } })}
            </Badge>
          {/if}
          <Badge variant={permissionExists ? "info" : "warning"} size="sm">
            {permissionExists ? $_("existing_configuration") : $_("new_configuration")}
          </Badge>
        </div>
      </div>
    {/if}
  </Card>

  {#if !isLoadingPermissions && permissionTypes.length > 0}
    <div class="rounded-card border border-info/30 bg-info-soft p-4 mb-6 text-sm text-text" role="note">
      <strong>{$_("permission_info")}:</strong>
      {infoText}
    </div>

    {#if isLoading}
      <Card><LoadingState label={$_("loading_permission_data")} /></Card>
    {:else}
      <form onsubmit={savePermissions} class="space-y-6">
        <Card>
          <MetaPermissionForm bind:formData bind:validateFn />
        </Card>

        <div class="flex flex-wrap items-center justify-between gap-4">
          <div class="text-sm text-text-muted space-y-1">
            <div>
              <strong class="text-text">{$_("permissions_page.permission_type")}:</strong>
              <code class="text-xs bg-surface-3 px-1.5 py-0.5 rounded-control">{selectedPermissionType}</code>
            </div>
            {#if currentPermissionShortname}
              <div>
                <strong class="text-text">{$_("role_management_page.id")}:</strong>
                <code class="text-xs bg-surface-3 px-1.5 py-0.5 rounded-control">{currentPermissionShortname}</code>
              </div>
            {/if}
          </div>
          {#if $can(permissionExists ? "update" : "create", MANAGEMENT_SPACE, PERMISSIONS_SUBPATH, ResourceType.permission)}
            <button type="submit" class="app-btn app-btn-primary" disabled={isSaving} aria-busy={isSaving}>
              {#if isSaving}
                <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
                {$_("saving")}
              {:else}
                {permissionExists ? $_("update") : $_("create")} {$_("permission")}
              {/if}
            </button>
          {/if}
        </div>
      </form>
    {/if}
  {/if}
</div>

{#if showAddModal}
  <Modal title={$_("add_new_permission")} size="md" dismissable={!isCreating} onClose={() => (showAddModal = false)}>
    <form id="add-permission-form" onsubmit={createNewPermission}>
      <label for="permissionName" class="block text-sm font-medium text-text mb-1.5">{$_("permission_name")}</label>
      <input
        id="permissionName"
        type="text"
        class="w-full px-3 py-2 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary"
        bind:value={newPermissionName}
        placeholder={$_("enter_permission_name")}
        required
        data-autofocus
      />
    </form>
    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={() => (showAddModal = false)} disabled={isCreating}>
        {$_("cancel")}
      </button>
      <button
        type="submit"
        form="add-permission-form"
        class="app-btn app-btn-primary"
        disabled={isCreating || !newPermissionName.trim()}
        aria-busy={isCreating}
      >
        {#if isCreating}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("creating")}
        {:else}
          {$_("create_permission")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}
