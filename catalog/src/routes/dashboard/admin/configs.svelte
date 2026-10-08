<script lang="ts">
  import { onMount } from "svelte";
  import { createEntity, getEntity, getSpaceContents, setDefaultUserRole } from "@/lib/dmart_services";
  import { toasts } from "@/lib/toast";
  import { log } from "@/lib/logger";
  import { setTitle } from "@/lib/title";
  import { localized } from "@/lib/catalogItems";
  import { APPLICATIONS_SPACE, MANAGEMENT_SPACE } from "@/lib/constants";
  import { ResourceType, DmartScope } from "@edraj/tsdmart";
  import { _, locale } from "@/i18n";
  import { CogOutline, ExclamationCircleOutline } from "flowbite-svelte-icons";
  import Modal from "@/components/Modal.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import Card from "@/components/ui/Card.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  interface RoleOption {
    shortname: string;
    displayname: string;
    description: string;
  }

  interface WebConfig {
    payload?: { body?: { items?: Array<{ key: string; value?: string }> } };
  }

  let availableRoles = $state<RoleOption[]>([]);
  let selectedDefaultRole = $state("");
  let currentDefaultRole = $state("");
  let isLoading = $state(true);
  let isSaving = $state(false);

  let showAutoFixModal = $state(false);
  let isAutoFixing = $state(false);

  $effect(() => setTitle($_("systemConfiguration")));

  async function loadRoles() {
    try {
      const rolesResponse = await getSpaceContents(MANAGEMENT_SPACE, "roles", DmartScope.managed);
      if (rolesResponse.status === "success") {
        availableRoles = rolesResponse.records.map((role) => {
          const attrs = role.attributes as { displayname?: unknown; description?: unknown };
          return {
            shortname: role.shortname,
            displayname: localized(attrs?.displayname as never, $locale) || role.shortname,
            description: localized(attrs?.description as never, $locale) || `${$_("role")}: ${role.shortname}`,
          };
        });
      } else {
        toasts.error($_("failedToLoadAvailableRoles"));
      }
    } catch (error) {
      log.error("Error loading roles:", error);
      toasts.error($_("failedToLoadRoles"));
    }
  }

  async function loadCurrentDefaultRole() {
    try {
      const defaultRole = (await getEntity(
        "web_config",
        APPLICATIONS_SPACE,
        DmartScope.public,
        ResourceType.content,
        DmartScope.managed,
        true,
        false,
      )) as unknown as WebConfig | null;

      if (defaultRole) {
        const value = defaultRole.payload?.body?.items?.find((item) => item.key === "default_user_role")?.value ?? "";
        currentDefaultRole = value;
        selectedDefaultRole = value;
      } else if (availableRoles.length > 0) {
        selectedDefaultRole = availableRoles[0].shortname;
      }
    } catch (error) {
      const err = error as { code?: number; message?: string };
      if (err.code === 220 || err.message?.includes("Cannot read properties of undefined")) {
        showAutoFixModal = true;
        return;
      }
      toasts.error($_("failedToLoadCurrentDefaultRole"));
    }
  }

  async function autoFixConfiguration() {
    isAutoFixing = true;
    try {
      const result = await createEntity(
        APPLICATIONS_SPACE,
        DmartScope.public,
        ResourceType.content,
        {
          displayname: { en: "web_config" },
          description: { en: "", ar: "", ku: "" },
          is_active: true,
          tags: [],
          relationships: [],
          payload: {
            content_type: "json",
            schema_shortname: "configuration",
            body: { items: [{ key: "default_user_role" }] },
          },
        },
        "web_config",
      );

      if (result) {
        showAutoFixModal = false;
        toasts.success($_("configurationEntityCreatedSuccessfully"));
        await loadCurrentDefaultRole();
      } else {
        toasts.error($_("failedToCreateConfigurationEntity"));
      }
    } catch (error) {
      log.error("Error creating configuration entity:", error);
      toasts.error($_("failedToCreateConfigurationEntity"));
    } finally {
      isAutoFixing = false;
    }
  }

  async function saveDefaultRole(event: SubmitEvent) {
    event.preventDefault();
    if (!selectedDefaultRole) {
      toasts.error($_("pleaseSelectDefaultRole"));
      return;
    }
    isSaving = true;
    try {
      const success = await setDefaultUserRole(selectedDefaultRole);
      if (success) {
        currentDefaultRole = selectedDefaultRole;
        toasts.success($_("defaultUserRoleUpdatedSuccessfully"));
      } else {
        toasts.error($_("failedToSaveDefaultUserRole"));
      }
    } catch (error) {
      log.error("Error saving default role:", error);
      toasts.error($_("failedToSaveDefaultUserRole"));
    } finally {
      isSaving = false;
    }
  }

  const selectedRole = $derived(availableRoles.find((r) => r.shortname === selectedDefaultRole));

  onMount(async () => {
    isLoading = true;
    await loadRoles();
    await loadCurrentDefaultRole();
    isLoading = false;
  });
</script>

{#if showAutoFixModal}
  <Modal title={$_("configurationMissing")} size="lg" dismissable={!isAutoFixing} onClose={() => (showAutoFixModal = false)}>
    {#snippet icon()}
      <ExclamationCircleOutline size="lg" class="text-warning" />
    {/snippet}
    <div class="rounded-card border border-warning/40 bg-warning-soft p-4 mb-4 text-sm text-text">
      <strong class="block mb-1">{$_("configurationEntityNotFound")}</strong>
      <p class="m-0 text-text-muted">{$_("systemConfigurationEntityMissingDescription")}</p>
    </div>
    <p class="text-sm text-text">{$_("autoCreateConfigurationEntityQuestion")}</p>
    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={() => (showAutoFixModal = false)} disabled={isAutoFixing}>
        {$_("cancel")}
      </button>
      <button type="button" class="app-btn app-btn-primary" onclick={autoFixConfiguration} disabled={isAutoFixing} aria-busy={isAutoFixing}>
        {#if isAutoFixing}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("creating")}
        {:else}
          {$_("autoFix")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}

<div class="mx-auto max-w-4xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("systemConfiguration")} description={$_("systemConfigurationSubtitle")} icon={CogOutline} />

  <Card>
    <h2 class="text-lg font-semibold text-text mb-1">{$_("userDefaultRole")}</h2>
    <p class="text-sm text-text-muted mb-6">{$_("userDefaultRoleDescription")}</p>

    {#if isLoading}
      <LoadingState label={$_("loadingConfiguration")} />
    {:else if availableRoles.length === 0}
      <EmptyState icon={CogOutline} title={$_("noRolesAvailable")} hint={$_("createRolesFirstMessage")} />
    {:else}
      <form class="space-y-6" onsubmit={saveDefaultRole}>
        <div>
          {#if currentDefaultRole}
            <Badge variant="info">{$_("currentDefaultRole")}: <strong>{currentDefaultRole}</strong></Badge>
          {:else}
            <Badge variant="warning">{$_("noDefaultRoleConfigured")}</Badge>
          {/if}
        </div>

        <div class="max-w-md space-y-3">
          <label for="default-role-select" class="block text-sm font-medium text-text">{$_("selectDefaultRole")}</label>
          <select
            id="default-role-select"
            class="w-full h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
            bind:value={selectedDefaultRole}
          >
            <option value="">{$_("selectRoleOption")}</option>
            {#each availableRoles as role (role.shortname)}
              <option value={role.shortname}>{role.displayname}</option>
            {/each}
          </select>

          {#if selectedRole}
            <div class="rounded-card border border-border bg-surface p-4">
              <h4 class="text-sm font-semibold text-text mb-1">{selectedRole.displayname}</h4>
              <p class="text-sm text-text-muted m-0">{selectedRole.description}</p>
            </div>
          {/if}
        </div>

        <div class="flex flex-wrap items-center gap-3">
          <button
            type="submit"
            class="app-btn app-btn-primary"
            disabled={isSaving || !selectedDefaultRole || selectedDefaultRole === currentDefaultRole}
            aria-busy={isSaving}
          >
            {#if isSaving}
              <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
              {$_("saving")}
            {:else}
              {$_("saveConfiguration")}
            {/if}
          </button>
          {#if currentDefaultRole && selectedDefaultRole !== currentDefaultRole}
            <button type="button" class="app-btn app-btn-secondary" onclick={() => (selectedDefaultRole = currentDefaultRole)} disabled={isSaving}>
              {$_("resetToCurrent")}
            </button>
          {/if}
        </div>
      </form>
    {/if}
  </Card>
</div>
