<script lang="ts">
  import { resolveTotal } from "@shared/query-total";
  import { onMount } from "svelte";
  import { can, permissions } from "@/stores/permissions";
  import { visibleColumns } from "@/lib/access-fields";
  import { getAllUsers, filterUserByRole, getSpaceContents, updateUserRoles, getEntity } from "@/lib/dmart_services";
  import { createEntity } from "@/lib/dmart_services/core";
  import { toasts } from "@/lib/toast";
  import { log } from "@/lib/logger";
  import { setTitle } from "@/lib/title";
  import { formatDate } from "@/lib/format";
  import { localized } from "@/lib/catalogItems";
  import { _, locale } from "@/i18n";
  import { formatNumber } from "@/lib/helpers";
  import { MANAGEMENT_SPACE } from "@/lib/constants";
  import Modal from "@/components/Modal.svelte";
  import { ResourceType, Dmart, RequestType, DmartScope } from "@edraj/tsdmart";
  import { parseValueByType, getFieldType, setNestedValue } from "@/lib/schemaTypes";
  import ModalCSVUpload from "@/components/management/Modals/ModalCSVUpload.svelte";
  import ModalCSVDownload from "@/components/management/Modals/ModalCSVDownload.svelte";
  import MetaForm from "@/components/management/forms/MetaForm.svelte";
  import MetaUserForm from "@/components/management/forms/MetaUserForm.svelte";
  import DataTable from "@/components/DataTable.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import CatalogToolbar from "@/components/ui/CatalogToolbar.svelte";
  import Card from "@/components/ui/Card.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import IconButton from "@/components/ui/IconButton.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import {
    CheckOutline,
    CloseOutline,
    CogOutline,
    DownloadOutline,
    EditOutline,
    PlusOutline,
    ShieldCheckOutline,
    TrashBinOutline,
    UploadOutline,
    UsersOutline,
  } from "flowbite-svelte-icons";

  interface UserRow {
    shortname: string;
    displayname: string;
    email: string;
    roles: string[];
    is_active: boolean;
    created_at: string;
    attributes?: Record<string, unknown>;
    [key: string]: unknown;
  }

  interface RoleOption {
    shortname: string;
    displayname: string;
    description: string;
    permissions: string[];
  }

  interface PermissionInfo {
    shortname: string;
    displayname: string;
    description: string;
    actions: string[];
    resource_types: string[];
    subpaths: Record<string, unknown>;
  }

  interface ColumnSetting {
    key: string;
    name: string;
  }

  let users = $state<UserRow[]>([]);
  let availableRoles = $state<RoleOption[]>([]);
  let isLoading = $state(true);
  let loadError = $state<unknown>(null);
  let isUpdating = $state(false);
  let selectedUser = $state<UserRow | null>(null);
  let showRoleModal = $state(false);
  let selectedRoles = $state<string[]>([]);
  let searchTerm = $state("");
  let selectedRoleFilter = $state("");
  let roleSearchTerm = $state("");
  let showUserModal = $state(false);
  let isEditingUserMode = $state(false);
  let isSavingUser = $state(false);

  let metaContent = $state<Record<string, unknown>>({});
  let validateMetaForm = $state<(() => boolean) | null>(null);
  let validateRTForm = $state<(() => boolean) | null>(null);

  let folderMetadata = $state<{ attributes?: { payload?: { body?: Record<string, unknown> } } } | null>(null);
  const folderBody = $derived((folderMetadata?.attributes?.payload?.body ?? {}) as Record<string, unknown>);
  const canUploadCSV = $derived(folderBody.allow_upload_csv === true);
  const canDownloadCSV = $derived(folderBody.allow_csv === true);
  const indexAttributes = $derived((Array.isArray(folderBody.index_attributes) ? folderBody.index_attributes : []) as ColumnSetting[]);
  const defaultColumns = $derived<ColumnSetting[]>([
    { key: "displayname", name: $_("users_page.columns.user") },
    { key: "email", name: $_("users_page.columns.email") },
    { key: "roles", name: $_("users_page.columns.roles") },
    { key: "status", name: $_("users_page.columns.status") },
  ]);
  const effectiveColumns = $derived(indexAttributes.length > 0 ? indexAttributes : defaultColumns);

  let isCSVUploadModalOpen = $state(false);
  let isCSVDownloadModalOpen = $state(false);

  let showColumnSettingsModal = $state(false);
  let editingIndexAttributes = $state<ColumnSetting[]>([]);
  let isSavingColumns = $state(false);

  let showBulkEditModal = $state(false);
  let bulkEditData = $state<Record<string, Record<string, unknown>>>({});
  let isBulkSaving = $state(false);

  let showViewUserModal = $state(false);
  let viewUserData = $state<UserRow | null>(null);
  let permissionsMap = $state<Record<string, PermissionInfo>>({});
  let permissionsLoaded = $state(false);

  let selectedItems = $state(new Set<string>());
  let currentPage = $state(1);
  let itemsPerPage = $state(20);
  let totalUsers = $state(0);
  let totalPages = $state(0);

  $effect(() => setTitle($_("user_management")));

  const filteredRoles = $derived.by(() => {
    const term = roleSearchTerm.trim().toLowerCase();
    if (!term) return availableRoles;
    return availableRoles.filter(
      (role) =>
        role.shortname.toLowerCase().includes(term) ||
        role.displayname.toLowerCase().includes(term) ||
        role.description.toLowerCase().includes(term),
    );
  });

  function getAttributeValue(item: UserRow, key: string): unknown {
    if (!item || !key) return "";
    if (key === "displayname") return item.displayname || item.shortname;
    if (key === "email") return item.email;
    if (key === "roles") return item.roles.map((r) => getRoleDisplayName(r)).join(", ");
    if (key === "status") return item.is_active ? $_("admin_content.status.active") : $_("admin_content.status.inactive");
    const findValue = (obj: unknown, k: string): unknown => {
      if (!obj || typeof obj !== "object") return undefined;
      const record = obj as Record<string, unknown>;
      if (record[k] !== undefined) return record[k];
      const tk = k.toLowerCase();
      const foundKey = Object.keys(record).find((ok) => ok.toLowerCase() === tk);
      return foundKey ? record[foundKey] : undefined;
    };
    if (key.includes(".")) {
      let current: unknown = item;
      for (const part of key.split(".")) {
        if (current === undefined || current === null) break;
        current = part === "attributes" ? item : findValue(current, part);
      }
      return current ?? "";
    }
    return findValue(item, key) ?? "";
  }

  function cellText(item: UserRow, key: string): string {
    const value = getAttributeValue(item, key);
    if (value === null || value === undefined) return "";
    return typeof value === "object" ? JSON.stringify(value) : String(value);
  }

  function toggleItemSelection(shortname: string) {
    if (selectedItems.has(shortname)) selectedItems.delete(shortname);
    else selectedItems.add(shortname);
    selectedItems = new Set(selectedItems);
  }

  function clearSelection() {
    selectedItems = new Set();
  }

  async function loadUsers() {
    try {
      isLoading = true;
      loadError = null;
      folderMetadata = (await getEntity("users", MANAGEMENT_SPACE, "/", ResourceType.folder, DmartScope.managed)) as typeof folderMetadata;

      const offset = (currentPage - 1) * itemsPerPage;
      const usersResponse = selectedRoleFilter
        ? await filterUserByRole(selectedRoleFilter, itemsPerPage, offset, searchTerm)
        : await getAllUsers(itemsPerPage, offset, searchTerm);

      if (usersResponse && usersResponse.status === "success") {
        users = usersResponse.records.map((user): UserRow => {
          const attrs = (user.attributes ?? {}) as Record<string, unknown>;
          const rawRoles = attrs.roles;
          return {
            ...(user as unknown as Record<string, unknown>),
            shortname: user.shortname,
            displayname: localized(attrs.displayname as never, $locale) || user.shortname,
            email: typeof attrs.email === "string" ? attrs.email : "",
            roles: Array.isArray(rawRoles)
              ? rawRoles.map((r) => (typeof r === "string" ? r : ((r as { shortname?: string; name?: string }).shortname ?? (r as { name?: string }).name ?? String(r))))
              : [],
            is_active: attrs.is_active !== false,
            created_at: typeof attrs.created_at === "string" ? attrs.created_at : "",
            attributes: attrs,
          };
        });
        totalUsers = resolveTotal((usersResponse.attributes as { total?: number })?.total, users.length);
        totalPages = Math.ceil(totalUsers / itemsPerPage);
      } else {
        loadError = $_("failed_to_load_users");
      }
    } catch (error) {
      log.error("Error loading users:", error);
      loadError = error;
    } finally {
      isLoading = false;
    }
  }

  async function loadRoles() {
    try {
      const rolesResponse = await getSpaceContents(MANAGEMENT_SPACE, "roles", DmartScope.managed);
      if (rolesResponse.status === "success") {
        availableRoles = rolesResponse.records.map((role) => {
          const attrs = (role.attributes ?? {}) as Record<string, unknown>;
          return {
            shortname: role.shortname,
            displayname: localized(attrs.displayname as never, $locale) || role.shortname,
            description: localized(attrs.description as never, $locale) || `${$_("role")}: ${role.shortname}`,
            permissions: Array.isArray(attrs.permissions) ? (attrs.permissions as string[]) : [],
          };
        });
      } else {
        toasts.error($_("failed_to_load_roles"));
      }
    } catch (error) {
      log.error("Error loading roles:", error);
      toasts.error($_("failed_to_load_roles"));
    }
  }

  function search(query: string) {
    searchTerm = query;
    currentPage = 1;
    loadUsers();
  }

  function openCreateUserModal() {
    isEditingUserMode = false;
    selectedUser = null;
    metaContent = {};
    showUserModal = true;
  }

  function openEditUserModal(user: UserRow) {
    isEditingUserMode = true;
    selectedUser = user;
    metaContent = { ...$state.snapshot(user.attributes ?? {}), shortname: user.shortname };
    showUserModal = true;
  }

  function closeUserModal() {
    if (isSavingUser) return;
    showUserModal = false;
    isEditingUserMode = false;
    selectedUser = null;
    metaContent = {};
  }

  function openRoleModal(user: UserRow) {
    selectedUser = user;
    selectedRoles = [...user.roles];
    roleSearchTerm = "";
    showRoleModal = true;
  }

  function closeRoleModal() {
    if (isUpdating) return;
    selectedUser = null;
    selectedRoles = [];
    roleSearchTerm = "";
    showRoleModal = false;
  }

  function toggleRole(roleShortname: string) {
    selectedRoles = selectedRoles.includes(roleShortname)
      ? selectedRoles.filter((r) => r !== roleShortname)
      : [...selectedRoles, roleShortname];
  }

  function handleOpenColumnSettings() {
    editingIndexAttributes = effectiveColumns.map((c) => ({ ...c }));
    showColumnSettingsModal = true;
  }

  function addColumnSetting() {
    editingIndexAttributes = [...editingIndexAttributes, { key: "", name: "" }];
  }

  function removeColumnSetting(index: number) {
    editingIndexAttributes = editingIndexAttributes.filter((_, i) => i !== index);
  }

  async function handleUpdateColumns(event: SubmitEvent) {
    event.preventDefault();
    isSavingColumns = true;
    try {
      const response = await Dmart.request({
        space_name: MANAGEMENT_SPACE,
        request_type: RequestType.update,
        records: [
          {
            resource_type: ResourceType.folder,
            shortname: "users",
            subpath: "/",
            attributes: {
              payload: {
                ...(folderMetadata?.attributes?.payload ?? {}),
                body: {
                  ...folderBody,
                  index_attributes: editingIndexAttributes.filter((a) => a.key?.trim() && a.name?.trim()),
                },
              },
            },
          },
        ],
      });
      if (response && response.status === "success") {
        isSavingColumns = false;
        showColumnSettingsModal = false;
        toasts.success($_("toast.folder_updated"));
        await loadUsers();
      } else {
        toasts.error($_("toast.folder_update_failed"));
      }
    } catch (err) {
      log.error("Error updating columns:", err);
      toasts.error($_("toast.folder_update_failed"));
    } finally {
      isSavingColumns = false;
    }
  }

  function openBulkEditModal() {
    isBulkSaving = false;
    const initialData: Record<string, Record<string, unknown>> = {};
    for (const shortname of selectedItems) {
      const item = users.find((i) => i.shortname === shortname);
      if (!item) continue;
      const editData: Record<string, unknown> = {};
      for (const col of effectiveColumns) editData[col.key] = getAttributeValue(item, col.key);
      initialData[shortname] = editData;
    }
    bulkEditData = initialData;
    showBulkEditModal = true;
  }

  function closeBulkEditModal() {
    if (isBulkSaving) return;
    showBulkEditModal = false;
    bulkEditData = {};
  }

  function updateBulkEditField(shortname: string, field: string, value: string) {
    if (bulkEditData[shortname]) bulkEditData[shortname] = { ...bulkEditData[shortname], [field]: value };
  }

  async function handleBulkSave(event: SubmitEvent) {
    event.preventDefault();
    if (Object.keys(bulkEditData).length === 0) return;
    isBulkSaving = true;
    try {
      const records = [];
      for (const [shortname, editData] of Object.entries(bulkEditData)) {
        const item = users.find((i) => i.shortname === shortname);
        if (!item) continue;
        const attributes: Record<string, unknown> = { ...(item.attributes ?? {}) };
        for (const [key, value] of Object.entries(editData)) {
          if (key === "shortname") continue;
          const parsedValue = parseValueByType(value as never, getFieldType(key));
          const attrKey = key.startsWith("attributes.") ? key.slice(11) : key;
          setNestedValue(attributes as never, attrKey, parsedValue);
        }
        records.push({ resource_type: ResourceType.user, shortname: item.shortname, subpath: "/users", attributes });
      }
      const response = await Dmart.request({ space_name: MANAGEMENT_SPACE, request_type: RequestType.update, records });
      if (response && response.status === "success") {
        toasts.success($_("admin_content.bulk_actions.edit_success", { values: { count: records.length } }));
        isBulkSaving = false;
        closeBulkEditModal();
        selectedItems = new Set();
        await loadUsers();
      } else {
        toasts.error($_("admin_content.bulk_actions.edit_failed", { values: { count: records.length } }));
      }
    } catch (err) {
      log.error("Bulk edit failed:", err);
      toasts.error($_("admin_content.bulk_actions.edit_error"));
    } finally {
      isBulkSaving = false;
    }
  }

  async function saveUserRoles() {
    if (!selectedUser) return;
    const target = selectedUser;
    isUpdating = true;
    try {
      const success = await updateUserRoles(target.shortname, selectedRoles);
      if (success) {
        const row = users.find((u) => u.shortname === target.shortname);
        if (row) row.roles = [...selectedRoles];
        toasts.success($_("users_page.roles_updated", { values: { name: target.displayname } }));
        isUpdating = false;
        closeRoleModal();
      } else {
        toasts.error($_("failed_to_update_user_roles"));
      }
    } catch (error) {
      log.error("Error updating user roles:", error);
      toasts.error($_("failed_to_update_user_roles"));
    } finally {
      isUpdating = false;
    }
  }

  function getRoleDisplayName(roleShortname: string): string {
    return availableRoles.find((r) => r.shortname === roleShortname)?.displayname ?? roleShortname;
  }

  function goToPage(page: number) {
    if (page >= 1 && page <= totalPages) {
      currentPage = page;
      loadUsers();
    }
  }

  async function loadPermissions() {
    try {
      const response = await getSpaceContents(MANAGEMENT_SPACE, "permissions", DmartScope.managed);
      if (response.status === "success") {
        const map: Record<string, PermissionInfo> = {};
        for (const p of response.records) {
          const attrs = (p.attributes ?? {}) as Record<string, unknown>;
          map[p.shortname] = {
            shortname: p.shortname,
            displayname: localized(attrs.displayname as never, $locale) || p.shortname,
            description: localized(attrs.description as never, $locale),
            actions: Array.isArray(attrs.actions) ? (attrs.actions as string[]) : [],
            resource_types: Array.isArray(attrs.resource_types) ? (attrs.resource_types as string[]) : [],
            subpaths: (attrs.subpaths as Record<string, unknown>) ?? {},
          };
        }
        permissionsMap = map;
        permissionsLoaded = true;
      }
    } catch (error) {
      log.error("Error loading permissions:", error);
    }
  }

  async function openViewUserModal(user: UserRow) {
    viewUserData = user;
    showViewUserModal = true;
    // Permissions are only needed for the breakdown in this modal, so the
    // round-trip waits until it is opened, and happens once.
    if (!permissionsLoaded) await loadPermissions();
  }

  function closeViewUserModal() {
    showViewUserModal = false;
    viewUserData = null;
  }

  function getRolePermissions(roleShortname: string): PermissionInfo[] {
    const role = availableRoles.find((r) => r.shortname === roleShortname);
    if (!role) return [];
    return role.permissions.map(
      (perm) => permissionsMap[perm] ?? { shortname: perm, displayname: perm, description: "", actions: [], resource_types: [], subpaths: {} },
    );
  }

  onMount(async () => {
    await Promise.all([loadUsers(), loadRoles()]);
  });

  async function handleSaveUser(event: SubmitEvent) {
    event.preventDefault();
    if (validateMetaForm && !validateMetaForm()) return;
    if (validateRTForm && !validateRTForm()) return;

    isSavingUser = true;
    try {
      const payload = $state.snapshot(metaContent) as Record<string, unknown>;
      const shortname = payload.shortname as string;
      delete payload.shortname;

      // Blank password fields mean "no credential change" — strip them so an
      // attribute-only edit never sends password/old_password (and a stored
      // hash loaded into the form can never be resubmitted as a password).
      if (!payload.password) {
        delete payload.password;
        delete payload.old_password;
      } else if (!payload.old_password) {
        delete payload.old_password;
      }

      let ok = false;
      if (isEditingUserMode) {
        const response = await Dmart.request({
          space_name: MANAGEMENT_SPACE,
          request_type: RequestType.update,
          records: [{ resource_type: ResourceType.user, shortname, subpath: "/users", attributes: payload }],
        });
        ok = !!response && response.status === "success";
      } else {
        ok = !!(await createEntity(MANAGEMENT_SPACE, "users", ResourceType.user, payload, shortname));
      }

      if (ok) {
        toasts.success(isEditingUserMode ? $_("user_updated_successfully") : $_("user_created_successfully"));
        isSavingUser = false;
        closeUserModal();
        await loadUsers();
      } else {
        toasts.error(isEditingUserMode ? $_("failed_to_update_user") : $_("failed_to_create_user"));
      }
    } catch (error) {
      log.error("Error saving user:", error);
      toasts.error(isEditingUserMode ? $_("failed_to_update_user") : $_("failed_to_create_user"));
    } finally {
      isSavingUser = false;
    }
  }

  const stats = $derived([
    { label: $_("total_users"), value: totalUsers },
    { label: $_("active_users"), value: users.filter((u) => u.is_active).length },
    { label: $_("available_roles"), value: availableRoles.length },
    { label: $_("users_without_roles"), value: users.filter((u) => u.roles.length === 0).length },
  ]);

  const inputClass =
    "w-full px-3 py-2 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary";
</script>

<div class="mx-auto max-w-7xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("user_management")} description={$_("manage_users_and_roles")} icon={UsersOutline}>
    {#snippet actions()}
      <IconButton label={$_("admin_content.column_settings.title")} variant="outline" onclick={handleOpenColumnSettings}>
        <CogOutline size="sm" />
      </IconButton>
      {#if canUploadCSV}
        <IconButton label={$_("users_page.upload_csv")} variant="outline" onclick={() => (isCSVUploadModalOpen = true)}>
          <UploadOutline size="sm" />
        </IconButton>
      {/if}
      {#if canDownloadCSV}
        <IconButton label={$_("users_page.download_csv")} variant="outline" onclick={() => (isCSVDownloadModalOpen = true)}>
          <DownloadOutline size="sm" />
        </IconButton>
      {/if}
      {#if $can("create", MANAGEMENT_SPACE, "users", ResourceType.user)}
        <button type="button" class="app-btn app-btn-primary" onclick={openCreateUserModal}>
          <PlusOutline size="sm" aria-hidden="true" />
          {$_("create_user")}
        </button>
      {/if}
    {/snippet}
  </PageHeader>

  <dl class="grid grid-cols-2 lg:grid-cols-4 gap-3 mb-6" aria-label={$_("statistics")}>
    {#each stats as stat (stat.label)}
      <div class="rounded-card border border-border bg-surface-2 shadow-card p-4">
        <dt class="text-xs text-text-muted">{stat.label}</dt>
        <dd class="text-2xl font-semibold text-text tabular-nums mt-1">{formatNumber(stat.value, $locale ?? "en")}</dd>
      </div>
    {/each}
  </dl>

  <CatalogToolbar class="mb-4" bind:search={searchTerm} placeholder={$_("search_users")} onSearch={search}>
    {#snippet filters()}
      <label for="role-filter" class="sr-only">{$_("all_roles")}</label>
      <select
        id="role-filter"
        bind:value={selectedRoleFilter}
        onchange={() => {
          currentPage = 1;
          loadUsers();
        }}
        class="h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
      >
        <option value="">{$_("all_roles")}</option>
        {#each availableRoles as role (role.shortname)}
          <option value={role.shortname}>{role.displayname}</option>
        {/each}
      </select>
    {/snippet}
  </CatalogToolbar>

  {#if loadError}
    <ErrorState title={$_("failed_to_load_users")} error={loadError} onRetry={loadUsers} />
  {:else}
    <DataTable
      items={users}
      indexAttributes={visibleColumns(effectiveColumns, $permissions, MANAGEMENT_SPACE, "/users", "user")}
      selectable={true}
      {selectedItems}
      onSelectAll={(checked) => {
        selectedItems = checked ? new Set(users.map((u) => u.shortname)) : new Set();
      }}
      onSelectItem={toggleItemSelection}
      onRowClick={(user) => openViewUserModal(user)}
      rowLabel={(user) => user.displayname}
      loading={isLoading}
      {currentPage}
      {totalPages}
      totalItems={totalUsers}
      {itemsPerPage}
      onPageChange={goToPage}
      onItemsPerPageChange={(count) => {
        itemsPerPage = count;
        currentPage = 1;
        loadUsers();
      }}
      emptyMessage={searchTerm || selectedRoleFilter ? $_("no_users_match_filters") : $_("no_users_found")}
    >
      {#snippet cell({ item: user, attr })}
        {#if attr.key === "displayname"}
          <span class="font-medium text-text">{user.displayname}</span>
        {:else if attr.key === "email"}
          <span class="text-text-muted break-all">{user.email || "—"}</span>
        {:else if attr.key === "roles"}
          {#if user.roles.length > 0}
            <span class="flex flex-wrap gap-1">
              {#each user.roles as role (role)}
                <Badge variant="info" size="sm">{getRoleDisplayName(role)}</Badge>
              {/each}
            </span>
          {:else}
            <span class="text-xs text-text-faint italic">{$_("no_roles_assigned")}</span>
          {/if}
        {:else if attr.key === "status"}
          <Badge variant={user.is_active ? "success" : "danger"} size="sm">
            {user.is_active ? $_("admin_content.status.active") : $_("admin_content.status.inactive")}
          </Badge>
        {:else if attr.key === "created_at" || attr.key === "updated_at"}
          <span class="text-text-muted whitespace-nowrap">{formatDate(cellText(user, attr.key), "datetime", $locale)}</span>
        {:else}
          <span class="text-text-muted">{cellText(user, attr.key)}</span>
        {/if}
      {/snippet}

      {#snippet actions({ item: user })}
        {#if $can("update", MANAGEMENT_SPACE, "/users", "user")}
          <IconButton label="{$_('edit_user')} {user.displayname}" size="sm" onclick={() => openEditUserModal(user)}>
            <EditOutline size="sm" />
          </IconButton>
          <IconButton label="{$_('manageRolesFor')} {user.displayname}" size="sm" onclick={() => openRoleModal(user)}>
            <ShieldCheckOutline size="sm" />
          </IconButton>
        {/if}
      {/snippet}

      {#snippet bulkActions()}
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={clearSelection}>
          <CloseOutline size="xs" aria-hidden="true" />
          {$_("admin_content.bulk_actions.clear_selection")}
        </button>
        <button type="button" class="app-btn app-btn-primary app-btn-sm" onclick={openBulkEditModal}>
          <EditOutline size="xs" aria-hidden="true" />
          {$_("admin_content.bulk_actions.edit")}
        </button>
      {/snippet}
    </DataTable>
  {/if}
</div>

{#if showRoleModal && selectedUser}
  <Modal title="{$_('manageRolesFor')} {selectedUser.displayname}" size="2xl" dismissable={!isUpdating} onClose={closeRoleModal}>
    <p class="text-sm text-text-muted mb-4">{$_("selectRolesToAssignDescription")}</p>

    {#if availableRoles.length === 0}
      <EmptyState icon={ShieldCheckOutline} title={$_("noRolesAvailable")} />
    {:else}
      <div class="mb-4">
        <label for="role-search" class="sr-only">{$_("search_roles")}</label>
        <input id="role-search" type="search" class={inputClass} placeholder={$_("search_roles")} bind:value={roleSearchTerm} />
      </div>

      {#if filteredRoles.length === 0}
        <p class="text-center py-8 text-sm text-text-muted">{$_("no_roles_match_search")}</p>
      {:else}
        <div class="space-y-2">
          {#each filteredRoles as role (role.shortname)}
            <label class="flex items-start gap-3 p-3 rounded-card border border-border bg-surface hover:bg-surface-3 cursor-pointer transition-colors">
              <input type="checkbox" class="mt-0.5 accent-primary" checked={selectedRoles.includes(role.shortname)} onchange={() => toggleRole(role.shortname)} />
              <span class="min-w-0">
                <span class="block text-sm font-medium text-text">{role.displayname}</span>
                <span class="block text-xs text-text-muted mt-0.5">{role.description}</span>
              </span>
            </label>
          {/each}
        </div>
      {/if}
    {/if}

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeRoleModal} disabled={isUpdating}>{$_("cancel")}</button>
      {#if $can("update", MANAGEMENT_SPACE, "users", ResourceType.user)}
        <button type="button" class="app-btn app-btn-primary" onclick={saveUserRoles} disabled={isUpdating || availableRoles.length === 0} aria-busy={isUpdating}>
          {#if isUpdating}
            <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
            {$_("saving")}
          {:else}
            {$_("saveChanges")}
          {/if}
        </button>
      {/if}
    {/snippet}
  </Modal>
{/if}

{#if showUserModal}
  <Modal title={isEditingUserMode ? $_("edit_user") : $_("create_user")} size="2xl" dismissable={!isSavingUser} onClose={closeUserModal}>
    <form id="user-form" class="space-y-4" onsubmit={handleSaveUser}>
      <MetaForm bind:formData={metaContent} bind:validateFn={validateMetaForm} isCreate={!isEditingUserMode} fullWidth={true} />
      <MetaUserForm bind:formData={metaContent} bind:validateFn={validateRTForm} isCreate={!isEditingUserMode} fullWidth={true} />
    </form>

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeUserModal} disabled={isSavingUser}>{$_("cancel")}</button>
      <button type="submit" form="user-form" class="app-btn app-btn-primary" disabled={isSavingUser} aria-busy={isSavingUser}>
        {#if isSavingUser}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {isEditingUserMode ? $_("updating") : $_("creating")}
        {:else}
          {isEditingUserMode ? $_("update_user") : $_("create_user")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}

{#if showViewUserModal && viewUserData}
  {@const user = viewUserData}
  {@const userAttrs = (user.attributes ?? {}) as Record<string, unknown>}
  {@const yesNo = (v: unknown) => (v ? $_("view_user.yes") : $_("view_user.no"))}
  <Modal title={user.displayname || user.shortname} size="3xl" onClose={closeViewUserModal}>
    <div class="space-y-5">
      <Card>
        <h3 class="text-sm font-semibold text-text mb-4">{$_("view_user.user_information")}</h3>
        <dl class="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-3 text-sm">
          <div><dt class="text-xs text-text-muted">{$_("view_user.shortname")}</dt><dd class="text-text font-mono">{user.shortname}</dd></div>
          <div><dt class="text-xs text-text-muted">{$_("view_user.display_name")}</dt><dd class="text-text">{user.displayname || "—"}</dd></div>
          <div><dt class="text-xs text-text-muted">{$_("view_user.email")}</dt><dd class="text-text break-all">{String(userAttrs.email ?? user.email ?? "") || "—"}</dd></div>
          <div><dt class="text-xs text-text-muted">{$_("view_user.mobile_number")}</dt><dd class="text-text">{String(userAttrs.msisdn ?? "") || "—"}</dd></div>
          <div>
            <dt class="text-xs text-text-muted">{$_("view_user.status")}</dt>
            <dd>
              <Badge variant={user.is_active ? "success" : "danger"} size="sm">
                {user.is_active ? $_("admin_content.status.active") : $_("admin_content.status.inactive")}
              </Badge>
            </dd>
          </div>
          <div><dt class="text-xs text-text-muted">{$_("view_user.type")}</dt><dd class="text-text">{String(userAttrs.type ?? "") || "—"}</dd></div>
          <div><dt class="text-xs text-text-muted">{$_("view_user.email_verified")}</dt><dd class="text-text">{yesNo(userAttrs.is_email_verified)}</dd></div>
          <div><dt class="text-xs text-text-muted">{$_("view_user.phone_verified")}</dt><dd class="text-text">{yesNo(userAttrs.is_msisdn_verified)}</dd></div>
          <div><dt class="text-xs text-text-muted">{$_("view_user.force_password_change")}</dt><dd class="text-text">{yesNo(userAttrs.force_password_change)}</dd></div>
          <div><dt class="text-xs text-text-muted">{$_("view_user.language")}</dt><dd class="text-text">{String(userAttrs.language ?? "") || "—"}</dd></div>
          {#if userAttrs.created_at}
            <div><dt class="text-xs text-text-muted">{$_("view_user.created_at")}</dt><dd class="text-text tabular-nums">{formatDate(userAttrs.created_at, "datetime", $locale)}</dd></div>
          {/if}
          {#if userAttrs.updated_at}
            <div><dt class="text-xs text-text-muted">{$_("view_user.updated_at")}</dt><dd class="text-text tabular-nums">{formatDate(userAttrs.updated_at, "datetime", $locale)}</dd></div>
          {/if}
        </dl>
      </Card>

      {#if Array.isArray(userAttrs.groups) && userAttrs.groups.length > 0}
        <Card>
          <h3 class="text-sm font-semibold text-text mb-3">{$_("view_user.groups")}</h3>
          <div class="flex flex-wrap gap-2">
            {#each userAttrs.groups as group (group)}
              <Badge size="sm">{String(group)}</Badge>
            {/each}
          </div>
        </Card>
      {/if}

      <Card>
        <h3 class="text-sm font-semibold text-text mb-3">{$_("view_user.roles_and_permissions")}</h3>
        {#if user.roles.length === 0}
          <p class="text-sm text-text-muted italic">{$_("view_user.no_roles_assigned")}</p>
        {:else}
          <div class="space-y-3">
            {#each user.roles as roleShortname (roleShortname)}
              {@const rolePerms = getRolePermissions(roleShortname)}
              <details class="group rounded-card border border-border bg-surface overflow-hidden" open>
                <summary class="flex items-center justify-between gap-2 px-4 py-3 cursor-pointer hover:bg-surface-3 transition-colors">
                  <span class="flex items-center gap-2 min-w-0">
                    <span class="text-sm font-medium text-text truncate">{getRoleDisplayName(roleShortname)}</span>
                    <span class="text-xs text-text-faint font-mono">({roleShortname})</span>
                  </span>
                  <Badge variant="primary" size="sm">
                    {rolePerms.length} {rolePerms.length === 1 ? $_("view_user.permission_singular") : $_("view_user.permission_plural")}
                  </Badge>
                </summary>
                <div class="px-4 py-3 border-t border-border bg-surface-2">
                  {#if rolePerms.length === 0}
                    <p class="text-xs text-text-muted italic">{$_("view_user.no_permissions")}</p>
                  {:else}
                    <ul class="space-y-2 list-none p-0 m-0">
                      {#each rolePerms as perm (perm.shortname)}
                        <li class="flex items-start gap-2 text-sm">
                          <CheckOutline size="sm" class="text-success mt-0.5 shrink-0" aria-hidden="true" />
                          <div class="min-w-0 flex-1">
                            <div class="font-medium text-text">{perm.displayname || perm.shortname}</div>
                            {#if perm.description}
                              <div class="text-xs text-text-muted mt-0.5">{perm.description}</div>
                            {/if}
                            {#if perm.actions.length > 0}
                              <div class="flex flex-wrap gap-1 mt-1.5">
                                {#each perm.actions as action (action)}
                                  <Badge variant="info" size="sm">{action}</Badge>
                                {/each}
                              </div>
                            {/if}
                          </div>
                        </li>
                      {/each}
                    </ul>
                  {/if}
                </div>
              </details>
            {/each}
          </div>
        {/if}
      </Card>
    </div>

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeViewUserModal}>{$_("view_user.close")}</button>
      {#if $can("update", MANAGEMENT_SPACE, "/users", "user")}
        <button
          type="button"
          class="app-btn app-btn-primary"
          onclick={() => {
            closeViewUserModal();
            openEditUserModal(user);
          }}
        >
          <EditOutline size="sm" aria-hidden="true" />
          {$_("view_user.edit")}
        </button>
      {/if}
    {/snippet}
  </Modal>
{/if}

{#if isCSVUploadModalOpen}
  <ModalCSVUpload
    space_name={MANAGEMENT_SPACE}
    subpath="users"
    bind:isOpen={isCSVUploadModalOpen}
    onUploadSuccess={() => loadUsers()}
  />
{/if}

{#if isCSVDownloadModalOpen}
  <ModalCSVDownload
    space_name={MANAGEMENT_SPACE}
    subpath="users"
    bind:isOpen={isCSVDownloadModalOpen}
    {indexAttributes}
    {folderMetadata}
  />
{/if}

{#if showColumnSettingsModal}
  <Modal title={$_("admin_content.column_settings.title")} size="lg" dismissable={!isSavingColumns} onClose={() => (showColumnSettingsModal = false)}>
    {#snippet icon()}
      <CogOutline size="lg" />
    {/snippet}
    <form id="column-settings-form" onsubmit={handleUpdateColumns} class="space-y-3">
      {#each editingIndexAttributes as attr, i (i)}
        <div class="flex items-end gap-3 p-3 rounded-card border border-border bg-surface">
          <div class="flex-1 grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label for="col-name-{i}" class="block text-xs text-text-muted mb-1">{$_("users_page.column_label")}</label>
              <input id="col-name-{i}" type="text" bind:value={attr.name} placeholder={$_("users_page.column_label_placeholder")} class={inputClass} />
            </div>
            <div>
              <label for="col-key-{i}" class="block text-xs text-text-muted mb-1">{$_("users_page.column_key")}</label>
              <input id="col-key-{i}" type="text" bind:value={attr.key} placeholder={$_("users_page.column_key_placeholder")} class="{inputClass} font-mono" />
            </div>
          </div>
          <IconButton label={$_("users_page.remove_column")} variant="danger" onclick={() => removeColumnSetting(i)}>
            <TrashBinOutline size="sm" />
          </IconButton>
        </div>
      {/each}
      <button type="button" class="w-full py-3 rounded-card border-2 border-dashed border-border text-sm font-medium text-text-muted hover:border-primary hover:text-primary transition-colors inline-flex items-center justify-center gap-2 cursor-pointer" onclick={addColumnSetting}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("users_page.add_column")}
      </button>
    </form>
    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={() => (showColumnSettingsModal = false)} disabled={isSavingColumns}>{$_("common.cancel")}</button>
      <button type="submit" form="column-settings-form" class="app-btn app-btn-primary" disabled={isSavingColumns} aria-busy={isSavingColumns}>
        {#if isSavingColumns}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("common.saving")}
        {:else}
          {$_("common.save_changes")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}

{#if showBulkEditModal}
  <Modal title={$_("admin_content.bulk_actions.edit_title")} size="4xl" dismissable={!isBulkSaving} onClose={closeBulkEditModal}>
    <p class="text-sm text-text-muted mb-4">{$_("admin_content.bulk_actions.edit_subtitle", { values: { count: Object.keys(bulkEditData).length } })}</p>
    <form id="bulk-edit-form" onsubmit={handleBulkSave}>
      <div class="overflow-auto max-h-[60vh] rounded-card border border-border">
        <table class="w-full text-sm text-start border-collapse">
          <thead class="sticky top-0 z-10 bg-surface-3 text-xs text-text-muted">
            <tr>
              <th scope="col" class="px-3 py-2 text-start font-semibold">{$_("delete_confirmation.item_label")}</th>
              {#each effectiveColumns as attr (attr.key)}
                <th scope="col" class="px-3 py-2 text-start font-semibold whitespace-nowrap min-w-48">{attr.name}</th>
              {/each}
            </tr>
          </thead>
          <tbody class="divide-y divide-border">
            {#each Object.entries(bulkEditData) as [shortname, editData] (shortname)}
              <tr>
                <td class="px-3 py-2 font-mono text-xs text-text-muted bg-surface">{shortname}</td>
                {#each effectiveColumns as attr (attr.key)}
                  <td class="px-3 py-2">
                    <label for="bulk-{shortname}-{attr.key}" class="sr-only">{attr.name} — {shortname}</label>
                    <input
                      id="bulk-{shortname}-{attr.key}"
                      type="text"
                      class={inputClass}
                      value={editData[attr.key] !== undefined && editData[attr.key] !== null ? String(editData[attr.key]) : ""}
                      oninput={(e) => updateBulkEditField(shortname, attr.key, (e.currentTarget as HTMLInputElement).value)}
                    />
                  </td>
                {/each}
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    </form>
    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeBulkEditModal} disabled={isBulkSaving}>{$_("common.cancel")}</button>
      <button type="submit" form="bulk-edit-form" class="app-btn app-btn-primary" disabled={isBulkSaving} aria-busy={isBulkSaving}>
        {#if isBulkSaving}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("admin_content.bulk_actions.saving")}
        {:else}
          {$_("admin_content.bulk_actions.save_changes")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}
