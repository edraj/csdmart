<script lang="ts">
  import { onMount, untrack } from "svelte";
  import {
    getAvatar,
    getProfile,
    getSpaceSchema,
    setAvatar,
    updatePassword,
    updateProfile,
  } from "@/lib/dmart_services";
  import { DmartScope } from "@edraj/tsdmart";
  import { toasts } from "@/lib/toast";
  import Avatar from "@/components/Avatar.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import Card from "@/components/ui/Card.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import { _, locale } from "@/i18n";
  import { website } from "@/config";
  import { MANAGEMENT_SPACE } from "@/lib/constants";
  import { setTitle } from "@/lib/title";
  import DynamicSchemaBasedForms from "@/components/forms/DynamicSchemaBasedForms.svelte";
  import { CameraPhotoOutline, LockOutline, UserOutline, EditOutline } from "flowbite-svelte-icons";
  import type { Schema } from "@/lib/types";

  type Localized = Record<string, string | undefined>;
  interface ProfileRecord {
    shortname: string;
    attributes: {
      displayname?: Localized;
      description?: Localized;
      email?: string;
      msisdn?: string;
      is_active?: boolean;
      payload?: { content_type?: string; schema_shortname?: string; body?: Record<string, unknown> };
    };
  }

  let isLoading = $state(true);
  let isUploadingAvatar = $state(false);
  let isSaving = $state(false);
  let profile: ProfileRecord | null = $state(null);
  let avatar: string | null = $state(null);

  let displayname = $state("");
  let description = $state("");
  let email = $state("");
  let msisdn = $state("");
  let fileInput: HTMLInputElement | undefined = $state();

  let userSchema: Schema | null = $state(null);
  let profileData: Record<string, unknown> = $state({});
  let loadingSchema = $state(false);

  let oldPassword = $state("");
  let newPassword = $state("");
  let confirmPassword = $state("");
  let isChangingPassword = $state(false);
  let showChangePassword = $state(false);

  const currentLocale = $derived($locale ?? website.default_language);
  const languageName = $derived(website.languages[currentLocale] ?? currentLocale);

  $effect(() => setTitle($_("profile.title")));

  onMount(async () => {
    isLoading = true;
    try {
      const u = (await getProfile()) as ProfileRecord | null;
      profile = u;
      if (u) {
        email = u.attributes?.email ?? "";
        msisdn = u.attributes?.msisdn ?? "";
        avatar = await getAvatar(u.shortname);
        await loadUserSchema(u);
      }
    } finally {
      isLoading = false;
    }
  });

  // The editable name/bio follow the active locale: each language has its own
  // value in the record, so switching language edits that language's text.
  $effect(() => {
    const u = profile;
    const loc = currentLocale;
    if (!u) return;
    untrack(() => {
      displayname = u.attributes?.displayname?.[loc] ?? "";
      if (!displayname && loc === "en") {
        displayname = u.attributes?.email ?? "";
      }
      description = u.attributes?.description?.[loc] ?? "";
    });
  });

  async function loadUserSchema(u: ProfileRecord) {
    loadingSchema = true;
    try {
      const response = await getSpaceSchema(MANAGEMENT_SPACE, DmartScope.managed);

      if (response?.status === "success" && response?.records) {
        const schemaShortname = u.attributes?.payload?.schema_shortname;
        const userSchemaRecord = schemaShortname
          ? response.records.find((record) => record.shortname === schemaShortname)
          : undefined;

        if (userSchemaRecord?.attributes?.payload?.body) {
          userSchema = userSchemaRecord.attributes.payload.body as Schema;
          profileData = (u.attributes?.payload?.body as Record<string, unknown> | undefined) ?? {};
        }
      }
    } catch (error) {
      console.error("Error loading user schema:", error);
      toasts.error($_("profile.errors.schema_load_failed"));
    } finally {
      loadingSchema = false;
    }
  }

  async function handlePasswordChange(event: Event) {
    event.preventDefault();
    if (!profile) return;

    if (newPassword !== confirmPassword) {
      toasts.error($_("profile.errors.passwords_do_not_match"));
      return;
    }

    if (newPassword.length < 8) {
      toasts.error($_("profile.errors.password_too_short"));
      return;
    }

    if (oldPassword === newPassword) {
      toasts.error($_("profile.errors.password_same"));
      return;
    }

    isChangingPassword = true;

    try {
      const response = await updatePassword({
        shortname: profile.shortname,
        password: newPassword,
        oldPassword: oldPassword,
      });

      if (response) {
        toasts.success($_("profile.password_changed"));
        oldPassword = "";
        newPassword = "";
        confirmPassword = "";
        showChangePassword = false;
      } else {
        toasts.error($_("profile.errors.password_change_failed"));
      }
    } catch (error) {
      console.error("Error changing password:", error);
      toasts.error($_("profile.errors.password_change_failed"));
    } finally {
      isChangingPassword = false;
    }
  }

  async function handleSubmit(event: Event) {
    event.preventDefault();
    if (!profile || isSaving) return;

    if (!displayname.trim()) {
      toasts.error($_("profile.errors.display_name_required", { values: { language: languageName } }));
      return;
    }

    if (!description.trim()) {
      toasts.error($_("profile.errors.description_required", { values: { language: languageName } }));
      return;
    }

    const updatedDisplayname: Localized = { ...profile.attributes.displayname };
    const updatedDescription: Localized = { ...profile.attributes.description };

    updatedDisplayname[currentLocale] = displayname.trim();
    updatedDescription[currentLocale] = description.trim();

    isSaving = true;
    try {
      // Include profile data in the update
      const response = await updateProfile({
        shortname: profile.shortname,
        displayname: updatedDisplayname,
        description: updatedDescription,
        email,
        msisdn,
        payload: {
          content_type: "json",
          body: profileData,
        },
      } as Parameters<typeof updateProfile>[0]);

      if (response) {
        toasts.success($_("profile.updated"));
        profile.attributes.displayname = updatedDisplayname;
        profile.attributes.description = updatedDescription;
        profile.attributes.payload = { ...(profile.attributes.payload ?? {}), body: profileData };
      } else {
        toasts.error($_("profile.errors.update_failed"));
      }
    } catch (error) {
      console.error("Error updating profile:", error);
      toasts.error($_("profile.errors.update_failed"));
    } finally {
      isSaving = false;
    }
  }

  async function handleAvatarChange(event: Event) {
    const target = event.target as HTMLInputElement;
    const file = target.files?.[0];

    if (!file || !profile) return;

    if (!file.type.startsWith("image/")) {
      toasts.error($_("profile.errors.invalid_image"));
      return;
    }

    const maxSize = 5 * 1024 * 1024;
    if (file.size > maxSize) {
      toasts.error($_("profile.errors.image_too_large"));
      return;
    }

    isUploadingAvatar = true;

    try {
      const success = await setAvatar(profile.shortname, file);

      if (success) {
        avatar = await getAvatar(profile.shortname);
        toasts.success($_("profile.photo_updated"));
      } else {
        toasts.error($_("profile.errors.photo_update_failed"));
      }
    } catch (error) {
      console.error("Error updating avatar:", error);
      toasts.error($_("profile.errors.photo_update_failed"));
    } finally {
      isUploadingAvatar = false;
      target.value = "";
    }
  }

  function cancelPasswordChange() {
    showChangePassword = false;
    oldPassword = "";
    newPassword = "";
    confirmPassword = "";
  }

  const inputClass =
    "w-full px-4 py-2.5 bg-surface border border-border rounded-control text-sm text-text placeholder:text-text-faint focus:bg-surface-2 focus:border-primary focus:ring-1 focus:ring-primary transition-colors";
  const labelClass = "block text-sm font-medium text-text";
</script>

<div class="profile-page">
  <div class="container">
    {#if isLoading}
      <LoadingState label={$_("profile.loading")} />
    {:else if profile}
      <PageHeader title={$_("profile.title")} description={"@" + profile.shortname} />

      <div class="space-y-6">
        <!-- Identity card -->
        <Card>
          <div class="flex flex-wrap items-center gap-5">
            <div class="relative shrink-0">
              <Avatar src={avatar} size={80} class="border border-border shadow-card" />
              <button
                type="button"
                class="avatar-overlay"
                aria-label={$_("profile.change_photo")}
                title={$_("profile.change_photo")}
                onclick={() => fileInput?.click()}
                disabled={isUploadingAvatar}
                aria-busy={isUploadingAvatar}
              >
                {#if isUploadingAvatar}
                  <span class="spinner spinner-sm spinner-white" aria-hidden="true"></span>
                {:else}
                  <CameraPhotoOutline size="md" aria-hidden="true" />
                {/if}
              </button>
              <input
                bind:this={fileInput}
                type="file"
                accept="image/*"
                class="sr-only"
                tabindex="-1"
                aria-hidden="true"
                onchange={handleAvatarChange}
                disabled={isUploadingAvatar}
              />
            </div>

            <div class="min-w-0 grow">
              <h2 class="text-xl font-semibold text-text truncate">
                {displayname || profile.shortname}
              </h2>
              <p class="text-sm text-text-muted font-medium">@{profile.shortname}</p>
            </div>

            {#if profile.attributes.is_active !== undefined}
              <Badge variant={profile.attributes.is_active ? "success" : "neutral"}>
                {profile.attributes.is_active ? $_("profile.active") : $_("profile.inactive")}
              </Badge>
            {/if}
          </div>
        </Card>

        <!-- Profile Info -->
        <Card>
          <h3 class="section-heading">
            <UserOutline size="md" class="text-primary" aria-hidden="true" />
            {$_("profile.info")}
          </h3>

          <form id="profile-form" onsubmit={handleSubmit} class="space-y-5" novalidate>
            <div class="grid grid-cols-1 md:grid-cols-2 gap-5">
              <div class="space-y-1.5">
                <label for="displayname" class={labelClass}>
                  {$_("profile.display_name")}
                  <span class="text-danger" aria-hidden="true">*</span>
                  <span class="sr-only">({$_("profile.required")})</span>
                </label>
                <input
                  id="displayname"
                  type="text"
                  required
                  bind:value={displayname}
                  class={inputClass}
                  placeholder={$_("DisplayNamePlaceholder")}
                  dir="auto"
                  autocomplete="name"
                />
              </div>

              <div class="space-y-1.5">
                <label for="email" class={labelClass}>{$_("profile.email")}</label>
                <input
                  id="email"
                  type="email"
                  bind:value={email}
                  class={inputClass}
                  placeholder={$_("EmailPlaceholder")}
                  autocomplete="email"
                  inputmode="email"
                />
              </div>

              <div class="space-y-1.5 md:col-span-2">
                <label for="msisdn" class={labelClass}>{$_("profile.mobile")}</label>
                <input
                  id="msisdn"
                  type="tel"
                  bind:value={msisdn}
                  class={inputClass}
                  placeholder={$_("MobileNumberPlaceholder")}
                  autocomplete="tel"
                />
              </div>

              <div class="space-y-1.5 md:col-span-2">
                <label for="description" class={labelClass}>
                  {$_("profile.bio")}
                  <span class="text-danger" aria-hidden="true">*</span>
                  <span class="sr-only">({$_("profile.required")})</span>
                </label>
                <textarea
                  id="description"
                  required
                  bind:value={description}
                  rows="4"
                  class="{inputClass} resize-y"
                  placeholder={$_("route_labels.placeholder_tell_us_about_yourself")}
                  dir="auto"
                ></textarea>
              </div>
            </div>
          </form>
        </Card>

        <!-- Dynamic Profile Fields -->
        {#if userSchema}
          <Card>
            <h3 class="section-heading">
              <EditOutline size="md" class="text-primary" aria-hidden="true" />
              {$_("profile.additional_details")}
            </h3>

            {#if loadingSchema}
              <LoadingState variant="skeleton" rows={4} />
            {:else}
              <DynamicSchemaBasedForms
                bind:content={profileData}
                schema={userSchema}
                space={MANAGEMENT_SPACE}
                subpath="users"
                resourceType="user"
              />
            {/if}
          </Card>
        {/if}

        <!-- Account -->
        <Card>
          <h3 class="section-heading">
            <LockOutline size="md" class="text-primary" aria-hidden="true" />
            {$_("profile.account")}
          </h3>

          <div class="space-y-4">
            <div class="flex flex-wrap items-center justify-between gap-3 p-4 bg-surface rounded-card border border-border">
              <div class="flex items-center gap-3 min-w-0">
                <LockOutline size="md" class="text-text-faint shrink-0" aria-hidden="true" />
                <div class="min-w-0">
                  <h4 class="text-sm font-semibold text-text">{$_("profile.password")}</h4>
                  <p class="text-xs text-text-muted mt-0.5">{$_("profile.password_hint")}</p>
                </div>
              </div>
              <button
                type="button"
                class="app-btn app-btn-secondary app-btn-sm"
                aria-expanded={showChangePassword}
                aria-controls="password-form"
                onclick={() => (showChangePassword ? cancelPasswordChange() : (showChangePassword = true))}
              >
                {showChangePassword ? $_("Cancel") : $_("profile.change")}
              </button>
            </div>

            {#if showChangePassword}
              <form
                id="password-form"
                onsubmit={handlePasswordChange}
                class="p-5 bg-surface border border-border rounded-card space-y-4"
              >
                <div class="space-y-1.5">
                  <label for="oldPassword" class={labelClass}>{$_("CurrentPassword")}</label>
                  <input
                    id="oldPassword"
                    type="password"
                    required
                    bind:value={oldPassword}
                    class={inputClass}
                    disabled={isChangingPassword}
                    autocomplete="current-password"
                  />
                </div>

                <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div class="space-y-1.5">
                    <label for="newPassword" class={labelClass}>{$_("NewPassword")}</label>
                    <input
                      id="newPassword"
                      type="password"
                      required
                      minlength="8"
                      bind:value={newPassword}
                      class={inputClass}
                      disabled={isChangingPassword}
                      autocomplete="new-password"
                    />
                  </div>

                  <div class="space-y-1.5">
                    <label for="confirmPassword" class={labelClass}>{$_("ConfirmNewPassword")}</label>
                    <input
                      id="confirmPassword"
                      type="password"
                      required
                      bind:value={confirmPassword}
                      class={inputClass}
                      disabled={isChangingPassword}
                      autocomplete="new-password"
                    />
                  </div>
                </div>

                <div class="pt-1 flex flex-wrap gap-3">
                  <button
                    type="submit"
                    disabled={isChangingPassword}
                    aria-busy={isChangingPassword}
                    class="app-btn app-btn-primary"
                  >
                    {#if isChangingPassword}
                      <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
                      {$_("ChangingPassword")}
                    {:else}
                      {$_("ChangePassword")}
                    {/if}
                  </button>
                  <button
                    type="button"
                    onclick={cancelPasswordChange}
                    class="app-btn app-btn-secondary"
                    disabled={isChangingPassword}
                  >
                    {$_("Cancel")}
                  </button>
                </div>
              </form>
            {/if}
          </div>
        </Card>

        <!-- Save -->
        <div class="flex justify-end">
          <button
            type="submit"
            form="profile-form"
            class="app-btn app-btn-primary app-btn-lg w-full sm:w-auto"
            disabled={isSaving}
            aria-busy={isSaving}
          >
            {#if isSaving}
              <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
              {$_("profile.saving")}
            {:else}
              {$_("profile.save_changes")}
            {/if}
          </button>
        </div>
      </div>
    {/if}
  </div>
</div>

<style>
  .profile-page {
    min-height: 100vh;
    padding: 1.5rem var(--space-page-x) 3rem;
  }

  .container {
    max-width: 48rem;
    margin: 0 auto;
  }

  .section-heading {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--color-text);
    margin-bottom: 1.25rem;
  }

  .avatar-overlay {
    position: absolute;
    inset: 0;
    border-radius: 50%;
    border: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    background: rgba(15, 23, 42, 0.55);
    color: var(--color-text-on-primary);
    cursor: pointer;
    opacity: 0;
    transition: opacity var(--duration-fast) ease;
  }

  .avatar-overlay:hover,
  .avatar-overlay:focus-visible,
  .avatar-overlay[aria-busy="true"] {
    opacity: 1;
  }

  .avatar-overlay:disabled {
    cursor: progress;
  }
</style>
