<script lang="ts">
  import MetaRoleForm from "@/components/forms/MetaRoleForm.svelte";
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
  import { createRole, deleteEntity, getEntity, getSpaceContents, updateRole } from "@/lib/dmart_services";
  import { ResourceType, DmartScope } from "@edraj/tsdmart";
  import { _, locale } from "@/i18n";
  import { PlusOutline, ShieldCheckOutline, TrashBinOutline } from "flowbite-svelte-icons";
  import Modal from "@/components/Modal.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import Card from "@/components/ui/Card.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  const ROLES_SUBPATH = "roles";

  let roleTypes = $state<Array<{ name: string; value: string }>>([]);
  let selectedRoleType = $state("");
  let formData = $state<Record<string, unknown>>({});
  let validateFn = $state<() => boolean>(() => true);
  let isLoading = $state(false);
  let isSaving = $state(false);
  let isLoadingRoles = $state(true);
  let lastSaved = $state<Date | null>(null);
  let roleExists = $state(false);
  let currentRoleShortname = $state("");
  let isCreating = $state(false);
  let showAddModal = $state(false);
  let newRoleName = $state("");

  $effect(() => setTitle($_("role_management")));

  const roleInfoKey = $derived(
    ["super_admin", "admin", "moderator", "catalog_user", "guest"].includes(selectedRoleType)
      ? `role_management_page.info.${selectedRoleType}`
      : "role_management_page.info.default",
  );

  async function loadRoleTypes() {
    isLoadingRoles = true;
    try {
      const response = await getSpaceContents(MANAGEMENT_SPACE, ROLES_SUBPATH, DmartScope.managed);
      if (response.status === "success") {
        roleTypes = response.records.map((role) => ({
          name: localized((role.attributes as { displayname?: unknown })?.displayname as never, $locale) || role.shortname,
          value: role.shortname,
        }));
        if (roleTypes.length > 0 && !roleTypes.some((t) => t.value === selectedRoleType)) {
          selectedRoleType = roleTypes[0].value;
        }
      } else {
        toasts.error($_("failed_to_load_role_types"));
      }
    } catch (error) {
      log.error("Error loading role types:", error);
      toasts.error($_("failed_to_load_role_types"));
    } finally {
      isLoadingRoles = false;
    }
  }

  async function loadRoleData(roleType: string) {
    if (!roleType) return;
    isLoading = true;
    try {
      const roleEntity = await getEntity(roleType, MANAGEMENT_SPACE, ROLES_SUBPATH, ResourceType.role, DmartScope.managed, true, false);
      if (roleEntity) {
        roleExists = true;
        currentRoleShortname = roleEntity.shortname;
        formData = { permissions: (roleEntity as unknown as { permissions?: unknown[] }).permissions ?? [] };
      } else {
        roleExists = false;
        currentRoleShortname = "";
        formData = { permissions: [] };
      }
    } catch (error) {
      log.error("Error loading role data:", error);
      toasts.error($_("failed_to_load_role_data"));
    } finally {
      isLoading = false;
    }
  }

  async function saveRole(event?: SubmitEvent) {
    event?.preventDefault();
    if (!validateFn()) {
      toasts.error($_("fix_validation_errors"));
      return;
    }
    isSaving = true;
    try {
      const result = await updateRole(currentRoleShortname, MANAGEMENT_SPACE, ROLES_SUBPATH, ResourceType.role, formData, "", "");
      if (!result) throw new Error("Failed to save role");
      lastSaved = new Date();
      toasts.success($_("role_management_page.saved", { values: { name: selectedRoleType } }));
    } catch (error) {
      log.error("Error saving role:", error);
      toasts.error($_("failed_to_save_role"));
    } finally {
      isSaving = false;
    }
  }

  async function createNewRole(event?: SubmitEvent) {
    event?.preventDefault();
    const name = newRoleName.trim();
    if (!name) {
      toasts.error($_("enter_role_name"));
      return;
    }
    isCreating = true;
    try {
      const result = await createRole(
        { title: name, content: `Role configuration for ${name}`, is_active: true, tags: [] },
        MANAGEMENT_SPACE,
        ROLES_SUBPATH,
        ResourceType.role,
        "",
        "",
      );
      if (!result) throw new Error("Failed to create role");
      toasts.success($_("role_management_page.created", { values: { name } }));
      showAddModal = false;
      newRoleName = "";
      await loadRoleTypes();
      selectedRoleType = result;
    } catch (error) {
      log.error("Error creating role:", error);
      toasts.error($_("failed_to_create_role"));
    } finally {
      isCreating = false;
    }
  }

  async function deleteRole() {
    if (!currentRoleShortname) {
      toasts.error($_("no_role_selected"));
      return;
    }
    const name = selectedRoleType;
    const deleted = await confirm({
      title: $_("delete_role"),
      body: `${$_("are_you_sure_delete_role")} “${name}”? ${$_("action_cannot_be_undone")}`,
      variant: "danger",
      confirmLabel: $_("delete_role"),
      action: async () => {
        const ok = await deleteEntity(currentRoleShortname, MANAGEMENT_SPACE, ROLES_SUBPATH, ResourceType.role);
        if (!ok) throw new Error($_("failed_to_delete_role"));
      },
    });
    if (!deleted) return;
    toasts.success($_("role_management_page.deleted", { values: { name } }));
    selectedRoleType = "";
    roleExists = false;
    currentRoleShortname = "";
    formData = {};
    await loadRoleTypes();
  }

  onMount(async () => {
    if (!guardAccess("query", MANAGEMENT_SPACE, ROLES_SUBPATH, ResourceType.role)) return;
    await loadRoleTypes();
  });

  $effect(() => {
    if (selectedRoleType && !isLoadingRoles) {
      loadRoleData(selectedRoleType);
    }
  });
</script>

<div class="mx-auto max-w-5xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("role_management")} description={$_("configure_roles_and_permissions")} icon={ShieldCheckOutline}>
    {#snippet actions()}
      {#if $can("create", MANAGEMENT_SPACE, ROLES_SUBPATH, ResourceType.role)}
        <button type="button" class="app-btn app-btn-primary" onclick={() => (showAddModal = true)}>
          <PlusOutline size="sm" aria-hidden="true" />
          {$_("add_role")}
        </button>
      {/if}
      {#if roleExists && $can("delete", MANAGEMENT_SPACE, ROLES_SUBPATH, ResourceType.role)}
        <button type="button" class="app-btn app-btn-danger" onclick={deleteRole}>
          <TrashBinOutline size="sm" aria-hidden="true" />
          {$_("delete")}
        </button>
      {/if}
    {/snippet}
  </PageHeader>

  <Card class="mb-6">
    <h2 class="text-lg font-semibold text-text mb-3">{$_("select_role_type")}</h2>
    {#if isLoadingRoles}
      <LoadingState label={$_("loading_role_types")} class="py-4" />
    {:else if roleTypes.length === 0}
      <EmptyState icon={ShieldCheckOutline} title={$_("no_roles_found")} hint={$_("role_management_page.none_available")} />
    {:else}
      <div class="flex flex-col sm:flex-row sm:items-center gap-4">
        <div class="sm:w-80">
          <label for="role-type" class="sr-only">{$_("role_type")}</label>
          <select
            id="role-type"
            class="w-full h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
            bind:value={selectedRoleType}
          >
            {#each roleTypes as type (type.value)}
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
          <Badge variant={roleExists ? "info" : "warning"} size="sm">
            {roleExists ? $_("existing_role") : $_("new_role")}
          </Badge>
        </div>
      </div>
    {/if}
  </Card>

  {#if !isLoadingRoles && roleTypes.length > 0}
    <div class="rounded-card border border-info/30 bg-info-soft p-4 mb-6 text-sm text-text" role="note">
      <strong>{$_("role_info")}:</strong>
      {$_(roleInfoKey, { values: { role: selectedRoleType } })}
    </div>

    {#if isLoading}
      <Card><LoadingState label={$_("loading_role_data")} /></Card>
    {:else}
      <form onsubmit={saveRole} class="space-y-6">
        <MetaRoleForm bind:formData bind:validateFn />

        <div class="flex flex-wrap items-center justify-between gap-4">
          <div class="text-sm text-text-muted space-y-1">
            <div><strong class="text-text">{$_("role_type")}:</strong> {selectedRoleType}</div>
            {#if currentRoleShortname}
              <div>
                <strong class="text-text">{$_("role_management_page.id")}:</strong>
                <code class="text-xs bg-surface-3 px-1.5 py-0.5 rounded-control">{currentRoleShortname}</code>
              </div>
            {/if}
          </div>
          {#if $can(roleExists ? "update" : "create", MANAGEMENT_SPACE, ROLES_SUBPATH, ResourceType.role)}
            <button type="submit" class="app-btn app-btn-primary" disabled={isSaving} aria-busy={isSaving}>
              {#if isSaving}
                <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
                {$_("saving")}
              {:else}
                {roleExists ? $_("update") : $_("create")} {$_("role")}
              {/if}
            </button>
          {/if}
        </div>
      </form>
    {/if}
  {/if}
</div>

{#if showAddModal}
  <Modal title={$_("add_new_role")} size="md" dismissable={!isCreating} onClose={() => (showAddModal = false)}>
    <form id="add-role-form" onsubmit={createNewRole}>
      <label for="new-role-name" class="block text-sm font-medium text-text mb-1.5">{$_("role_name")}</label>
      <input
        id="new-role-name"
        type="text"
        class="w-full px-3 py-2 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary"
        bind:value={newRoleName}
        placeholder={$_("enter_role_name")}
        required
        data-autofocus
      />
    </form>
    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={() => (showAddModal = false)} disabled={isCreating}>
        {$_("cancel")}
      </button>
      <button type="submit" form="add-role-form" class="app-btn app-btn-primary" disabled={isCreating || !newRoleName.trim()} aria-busy={isCreating}>
        {#if isCreating}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("creating")}
        {:else}
          {$_("create_role")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}
