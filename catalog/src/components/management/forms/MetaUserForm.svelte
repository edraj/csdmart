<script module lang="ts">
  import type { MetaFormData } from "@/components/forms/MetaForm.svelte";

  /**
   * A user's attributes as this form edits them, on top of the common meta
   * fields (the same object is bound to MetaForm beside this one). Lists
   * are optional on the way in — the server strips empty ones — and present
   * once the form has normalized its data.
   */
  export interface UserFormData extends MetaFormData {
    email?: string | null;
    password?: string;
    old_password?: string;
    msisdn?: string | null;
    is_email_verified?: boolean;
    is_msisdn_verified?: boolean;
    force_password_change?: boolean;
    type?: string;
    language?: string | null;
    roles?: string[];
    groups?: string[];
    firebase_token?: string | null;
    google_id?: string | null;
    facebook_id?: string | null;
    apple_id?: string | null;
    social_avatar_url?: string | null;
    attempt_count?: unknown;
  }
</script>

<script lang="ts">
  import { log } from "@/lib/logger";
    import { onMount } from 'svelte';
    import { Dmart, QueryType, type ApiResponseRecord } from '@edraj/tsdmart';
    import { _ } from 'svelte-i18n';
    import FieldGate from '@/components/access/FieldGate.svelte';
    import { permissions } from '@/stores/permissions';
    import { constrainEnumOptions } from '@/lib/access-fields';
    import { canClearLockout, readFailedAttempts, resolveAttemptCount } from '@shared/user-lockout';

    /** One choice in the roles / groups pickers. */
    interface PickerOption {
        key: string;
        value: string;
    }

    let {
        formData = $bindable({}),
        // eslint-disable-next-line @typescript-eslint/no-unused-vars, no-useless-assignment -- $bindable() prop: assigned here, read by the parent through bind:validateFn
        validateFn = $bindable(),
        isCreate = false,
        fullWidth = false
    }: {
        formData: UserFormData;
        validateFn?: (() => boolean) | null;
        isCreate?: boolean;
        fullWidth?: boolean;
    } = $props();

    let form = $state<HTMLFormElement | null>(null);

    let availableRoles = $state<ApiResponseRecord[]>([]);
    let loadingRoles = $state(true);
    let filteredRoles = $state<PickerOption[]>([]);
    let rolesSearchTerm = $state('');
    let showRolesDropdown = $state(false);
    let rolesDropdownRef = $state<HTMLDivElement | null>(null);

    let availableGroups = $state<ApiResponseRecord[]>([]);
    let loadingGroups = $state(true);
    let filteredGroups = $state<PickerOption[]>([]);
    let groupsSearchTerm = $state('');
    let showGroupsDropdown = $state(false);
    let groupsDropdownRef = $state<HTMLDivElement | null>(null);

    let isRolesOpen = $state(false);
    let isSocialOpen = $state(false);

    // Read the counter BEFORE it is stripped below. It is the account lockout:
    // the lock leaves is_active set, so this is the only thing that says an
    // account is locked out. The rules live in @shared/user-lockout, where they
    // are tested — both failure modes here are silent saves.
    const failedAttempts: number = readFailedAttempts(formData.attempt_count);
    const showClearLockout: boolean = canClearLockout(formData.attempt_count);
    let resetAttempts = $state(false);

    formData = {
        ...formData,
        email: formData.email || null,
        // Passwords always start EMPTY — never seeded from loaded user
        // attributes (which may carry the stored hash; resubmitting it would
        // corrupt the credential). Empty values are stripped before submit.
        password: "",
        old_password: "",
        msisdn: formData.msisdn || null,
        is_email_verified: formData.is_email_verified || false,
        is_msisdn_verified: formData.is_msisdn_verified || false,
        force_password_change: formData.force_password_change || false,
        type: formData.type || 'mobile',
        language: formData.language || null,
        roles: formData.roles || [],
        groups: formData.groups || [],
        firebase_token: formData.firebase_token || null,
        google_id: formData.google_id || null,
        facebook_id: formData.facebook_id || null,
        apple_id: formData.apple_id || null,
        social_avatar_url: formData.social_avatar_url || null,
        // Same reasoning as the passwords above, different risk. attempt_count IS
        // the account lockout, and this form round-trips whatever the API
        // returned — echoing it back on an ordinary save would write a value read
        // seconds ago, rolling back increments an in-flight brute-force run
        // landed in between. Stripped unless the admin explicitly asks to clear
        // it below (undefined keys are dropped on serialize).
        attempt_count: undefined
    }

    // The lists the pickers edit; the normalization above has made them arrays.
    const roles = $derived(formData.roles ?? []);
    const groups = $derived(formData.groups ?? []);

    // The unlock gesture. Deliberately NOT is_active: a locked account is still
    // active (the lock is counter-only), and this form emits is_active on every
    // save — so keying an unlock off that flag would mean renaming a locked user
    // silently cancels their lockout. Sending an explicit 0 is unambiguous, and
    // the server records it in the audit history.
    function applyAttemptReset() {
        formData.attempt_count = resolveAttemptCount(failedAttempts, resetAttempts);
    }

    // The same allowed_fields_values whitelist DynamicSchemaBasedForms applies
    // via constrainEnumOptions must constrain this hand-written select too.
    const userTypeOptions = $derived(
        constrainEnumOptions(
            ["bot", "mobile", "web", "admin", "api"],
            $permissions,
            "type",
            "management",
            "users",
            "user",
            formData.type,
        ).map(type => ({ name: type.charAt(0).toUpperCase() + type.slice(1), value: type })));

    async function getRoles() {
        try {
            const rolesResponse = await Dmart.query({
                space_name: 'management',
                subpath: '/roles',
                type: QueryType.search,
                search: '',
                limit: 100,
            });
            if (rolesResponse) {
                availableRoles = rolesResponse.records;
                updateFilteredRoles();
            }
        } catch (error) {
            log.error('Failed to load roles:', error);
        } finally {
            loadingRoles = false;
        }
    }

    async function getGroups() {
        try {
            const groupsResponse = await Dmart.query({
                space_name: 'management',
                subpath: '/groups',
                type: QueryType.search,
                search: '',
                limit: 100,
            });
            if (groupsResponse) {
                availableGroups = groupsResponse.records;
                updateFilteredGroups();
            }
        } catch (error) {
            log.error('Failed to load groups:', error);
        } finally {
            loadingGroups = false;
        }
    }

    onMount(() => {
        getRoles();
        getGroups();

        const handleClickOutside = (event: MouseEvent) => {
            const target = event.target as Node;
            if (rolesDropdownRef && !rolesDropdownRef.contains(target)) {
                showRolesDropdown = false;
            }
            if (groupsDropdownRef && !groupsDropdownRef.contains(target)) {
                showGroupsDropdown = false;
            }
        };
        document.addEventListener('click', handleClickOutside);
        return () => {
            document.removeEventListener('click', handleClickOutside);
        };
    });

    function updateFilteredRoles() {
        filteredRoles = availableRoles
            .filter(role => role.shortname.toLowerCase().includes(rolesSearchTerm.toLowerCase()))
            .map(role => ({ key: role.shortname, value: role.shortname }));
    }

    function toggleRole(event: MouseEvent, role: PickerOption) {
        event.stopPropagation();
        const index = roles.indexOf(role.value);
        if (index === -1) {
            formData.roles = [...roles, role.value];
        } else {
            formData.roles = roles.filter((r) => r !== role.value);
        }
    }

    function removeRole(role: string) {
        formData.roles = roles.filter((r) => r !== role);
    }

    function updateFilteredGroups() {
        filteredGroups = availableGroups
            .filter(group => group.shortname.toLowerCase().includes(groupsSearchTerm.toLowerCase()))
            .map(group => ({ key: group.shortname, value: group.shortname }));
    }

    function toggleGroup(event: MouseEvent, group: PickerOption) {
        event.stopPropagation();
        const index = groups.indexOf(group.value);
        if (index === -1) {
            formData.groups = [...groups, group.value];
        } else {
            formData.groups = groups.filter((g) => g !== group.value);
        }
    }

    function removeGroup(group: string) {
        formData.groups = groups.filter((g) => g !== group);
    }

    function validate() {
        if (!form) return false;
        const isValid = form.checkValidity();
        isEmailValid = validateEmail(formData.email ?? null)

        if (!isValid || !isEmailValid) {
            form.reportValidity();
            return false;
        }
        return isValid;
    }

    $effect(() => {
        validateFn = validate;
    });

    $effect(() => {
        if(rolesSearchTerm){
            updateFilteredRoles();
        } else {
            filteredRoles = availableRoles.map(role => ({ key: role.shortname, value: role.shortname }));
        }
    });

    $effect(() => {
        if(groupsSearchTerm){
            updateFilteredGroups();
        } else {
            filteredGroups = availableGroups.map(group => ({ key: group.shortname, value: group.shortname }));
        }
    });


    let isEmailValid = $state(true);
    let emailTouched = $state(false);
    function validateEmail(email: string | null): boolean {
        if (!email) return true;
        const emailRegex = /^[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,6}$/;
        return emailRegex.test(email);
    }
    $effect(() => {
        if (emailTouched) {
            isEmailValid = validateEmail(formData.email ?? null);
        }
    });
</script>

<div class={fullWidth ? 'form-card-full' : 'form-card'}>
    <div class="form-container">
        <h2 class="section-title">{$_("view_user.user_information")}</h2>

        <form bind:this={form} class="form-body">
            <!-- Passwords are OPTIONAL: an admin must be able to edit any other
                 attribute without knowing/replacing the user's password. When
                 left blank they are stripped from the payload before submit;
                 minlength only applies once a value is typed. -->
            {#if !isCreate}
                <div class="field-group">
                    <label for="old_password" class="field-label">{$_("meta_user_form.old_password")}</label>
                    <input
                        id="old_password"
                        type="password"
                        class="input-field"
                        placeholder="••••••••"
                        bind:value={formData.old_password}
                        minlength={8}
                    />
                    <p class="field-help">{$_("meta_user_form.old_password_help")}</p>
                </div>
            {/if}

            <div class="field-group">
                <label for="password" class="field-label">
                    {isCreate ? "Password" : "New Password"}
                </label>
                <input
                    id="password"
                    type="password"
                    class="input-field"
                    placeholder="••••••••"
                    bind:value={formData.password}
                    minlength={8}
                />
                <p class="field-help">{$_("meta_user_form.password_help")}</p>
            </div>

            <FieldGate field="email" space="management" subpath="/users" resourceType="user">
            <div class="field-group">
                <label for="email" class="field-label">{$_("view_user.email")}</label>
                <input
                    id="email"
                    type="email"
                    class="input-field"
                    class:input-error={!isEmailValid && emailTouched}
                    placeholder={$_("labels.email_example")}
                    bind:value={formData.email}
                    onblur={() => emailTouched = true}
                />
                {#if !isEmailValid && emailTouched}
                    <p class="error-text">{$_("InvalidEmail")}</p>
                {/if}
            </div>
            </FieldGate>

            <FieldGate field="msisdn" space="management" subpath="/users" resourceType="user">
            <div class="field-group">
                <label for="msisdn" class="field-label">{$_("view_user.mobile_number")}</label>
                <input
                    id="msisdn"
                    class="input-field"
                    placeholder="+964723456789 / 0712345678"
                    bind:value={formData.msisdn}
                />
            </div>
            </FieldGate>

            {#if !isCreate}
                <div class="field-group" role="group" aria-labelledby="failed-attempts-label">
                    <span class="field-label" id="failed-attempts-label">{$_("meta_user_form.failed_login_attempts")}</span>
                    <div class="checkbox-row compact">
                        <span class="attempt-count" class:attempt-count-warn={showClearLockout}>
                            {failedAttempts}
                        </span>
                        {#if showClearLockout}
                            <div class="checkbox-group">
                                <input type="checkbox" id="reset_attempt_count" class="checkbox" bind:checked={resetAttempts} onchange={applyAttemptReset} />
                                <label for="reset_attempt_count" class="checkbox-label">{$_("meta_user_form.clear_on_save")}</label>
                            </div>
                        {/if}
                    </div>
                    <p class="field-hint">
                        The account lockout is this counter — once it reaches the server's
                        MAX_FAILED_LOGIN_ATTEMPTS the user cannot log in until it is cleared
                        or the cool-down elapses. Saving other fields leaves it alone; tick
                        the box to unlock the account.
                    </p>
                </div>
            {/if}

            <div class="checkbox-row compact">
                <div class="checkbox-group">
                    <input type="checkbox" id="force_password_change" class="checkbox" bind:checked={formData.force_password_change} />
                    <label for="force_password_change" class="checkbox-label">{$_("view_user.force_password_change")}</label>
                </div>
                <div class="checkbox-group">
                    <input type="checkbox" id="is_email_verified" class="checkbox" bind:checked={formData.is_email_verified} />
                    <label for="is_email_verified" class="checkbox-label">{$_("view_user.email_verified")}</label>
                </div>
                <div class="checkbox-group">
                    <input type="checkbox" id="is_msisdn_verified" class="checkbox" bind:checked={formData.is_msisdn_verified} />
                    <label for="is_msisdn_verified" class="checkbox-label">{$_("view_user.phone_verified")}</label>
                </div>
            </div>

            <div class="grid-row">
                <div class="field-group">
                    <label for="user_type" class="field-label">{$_("meta_user_form.user_type")}</label>
                    <select id="user_type" class="input-field" bind:value={formData.type}>
                        {#each userTypeOptions as option (option.value)}
                            <option value={option.value}>{option.name}</option>
                        {/each}
                    </select>
                </div>
                <div class="field-group">
                    <label for="language" class="field-label">{$_("meta_user_form.preferred_language")}</label>
                    <input id="language" class="input-field" bind:value={formData.language} placeholder={$_("labels.language_example")} />
                </div>
            </div>

            <div class="accordion">
                <button
                    type="button"
                    class="accordion-header"
                    onclick={() => (isRolesOpen = !isRolesOpen)}
                >
                    <span class="accordion-title">{$_("meta_user_form.roles_and_groups")}</span>
                    <svg class="accordion-icon" class:rotated={isRolesOpen} viewBox="0 0 24 24" fill="none" stroke="currentColor">
                        <path d="M19 9l-7 7-7-7" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" />
                    </svg>
                </button>

                {#if isRolesOpen}
                    <div class="accordion-content">
                        <div class="field-group">
                            <p class="field-label">{$_("roles")}</p>
                            {#if loadingRoles}
                                <div class="loading-pulse"></div>
                            {:else}
                                <div class="relative-container" bind:this={rolesDropdownRef}>
                                    <div class="search-input-wrapper">
                                        <svg class="search-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor">
                                            <path d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" />
                                        </svg>
                                        <input
                                            class="input-field search-input"
                                            placeholder={$_("search_roles")}
                                            bind:value={rolesSearchTerm}
                                            onfocus={() => showRolesDropdown = true}
                                        />
                                    </div>

                                    {#if showRolesDropdown && filteredRoles.length > 0}
                                        <div class="dropdown-menu">
                                            {#each filteredRoles as role (role.value)}
                                                <button
                                                    type="button"
                                                    class="dropdown-item"
                                                    onclick={(e) => toggleRole(e, role)}
                                                >
                                                    <span>{role.key}</span>
                                                    {#if roles.includes(role.value)}
                                                        <span class="badge">{$_("selected")}</span>
                                                    {/if}
                                                </button>
                                            {/each}
                                        </div>
                                    {/if}
                                </div>

                                <div class="tags-container">
                                    {#if roles.length > 0}
                                        {#each roles as role (role)}
                                            <span class="tag">
                                                {role}
                                                <button type="button" class="tag-remove" onclick={() => removeRole(role)}>×</button>
                                            </span>
                                        {/each}
                                    {:else}
                                        <p class="empty-text">{$_("no_roles_assigned")}</p>
                                    {/if}
                                </div>
                            {/if}
                        </div>

                        <div class="field-group">
                            <p class="field-label">{$_("view_user.groups")}</p>
                            {#if loadingGroups}
                                <div class="loading-pulse"></div>
                            {:else}
                                <div class="relative-container" bind:this={groupsDropdownRef}>
                                    <div class="search-input-wrapper">
                                        <svg class="search-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor">
                                            <path d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" />
                                        </svg>
                                        <input
                                            class="input-field search-input"
                                            placeholder={$_("labels.search_groups")}
                                            bind:value={groupsSearchTerm}
                                            onfocus={() => showGroupsDropdown = true}
                                        />
                                    </div>

                                    {#if showGroupsDropdown && filteredGroups.length > 0}
                                        <div class="dropdown-menu">
                                            {#each filteredGroups as group (group.value)}
                                                <button
                                                    type="button"
                                                    class="dropdown-item"
                                                    onclick={(e) => toggleGroup(e, group)}
                                                >
                                                    <span>{group.key}</span>
                                                    {#if groups.includes(group.value)}
                                                        <span class="badge">{$_("selected")}</span>
                                                    {/if}
                                                </button>
                                            {/each}
                                        </div>
                                    {/if}
                                </div>

                                <div class="tags-container">
                                    {#if groups.length > 0}
                                        {#each groups as group (group)}
                                            <span class="tag tag-gray">
                                                {group}
                                                <button type="button" class="tag-remove" onclick={() => removeGroup(group)}>×</button>
                                            </span>
                                        {/each}
                                    {:else}
                                        <p class="empty-text">{$_("meta_user_form.no_groups")}</p>
                                    {/if}
                                </div>
                            {/if}
                        </div>
                    </div>
                {/if}
            </div>

            <div class="accordion">
                <button
                    type="button"
                    class="accordion-header"
                    onclick={() => (isSocialOpen = !isSocialOpen)}
                >
                    <span class="accordion-title">{$_("meta_user_form.social_ids")}</span>
                    <svg class="accordion-icon" class:rotated={isSocialOpen} viewBox="0 0 24 24" fill="none" stroke="currentColor">
                        <path d="M19 9l-7 7-7-7" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" />
                    </svg>
                </button>

                {#if isSocialOpen}
                    <div class="accordion-content">
                        <div class="field-group">
                            <label for="firebase_token" class="field-label">{$_("meta_user_form.firebase_token")}</label>
                            <textarea id="firebase_token" class="textarea-field" placeholder={$_("labels.firebase_token")} bind:value={formData.firebase_token} rows={2}></textarea>
                        </div>

                        <div class="grid-row">
                            <div class="field-group">
                                <label for="google_id" class="field-label">{$_("meta_user_form.google_id")}</label>
                                <input id="google_id" class="input-field" bind:value={formData.google_id} />
                            </div>
                            <div class="field-group">
                                <label for="facebook_id" class="field-label">{$_("meta_user_form.facebook_id")}</label>
                                <input id="facebook_id" class="input-field" bind:value={formData.facebook_id} />
                            </div>
                            <div class="field-group">
                                <label for="apple_id" class="field-label">{$_("meta_user_form.apple_id")}</label>
                                <input id="apple_id" class="input-field" bind:value={formData.apple_id} />
                            </div>
                        </div>

                        <div class="field-group">
                            <label for="social_avatar_url" class="field-label">{$_("meta_user_form.social_avatar_url")}</label>
                            <input id="social_avatar_url" type="url" class="input-field" bind:value={formData.social_avatar_url} placeholder={$_("labels.url_placeholder")} />
                        </div>
                    </div>
                {/if}
            </div>
        </form>
    </div>
</div>

<style>
    .form-card {
        background: var(--color-surface);
        border-radius: 12px;
        box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06);
        width: 100%;
        max-width: 56rem;
        margin: 0.5rem auto;
        padding: 1.5rem;
        border: 1px solid var(--color-border);
    }

    .form-card-full {
        width: 100%;
        padding: 0;
        background: transparent;
        border: none;
        box-shadow: none;
    }

    .form-container {
        display: flex;
        flex-direction: column;
        gap: 1.5rem;
    }

    .section-title {
        font-size: 1.25rem;
        font-weight: 700;
        color: var(--color-text);
        margin: 0;
    }

    .form-body {
        display: flex;
        flex-direction: column;
        gap: 1.25rem;
    }

    .field-group {
        display: flex;
        flex-direction: column;
        gap: 0.375rem;
    }

    .field-label {
        font-weight: 600;
        font-size: 0.8125rem;
        color: var(--color-text);
        display: flex;
        align-items: center;
    }

.input-field {
        width: 100%;
        padding: 0.5rem 0.75rem;
        border: 1.5px solid var(--color-border);
        border-radius: 8px;
        font-size: 0.875rem;
        background: var(--color-surface-2);
        color: var(--color-text);
        transition: all 0.2s ease;
    }

    .input-field:focus {
        outline: none;
        border-color: var(--color-primary);
        background: var(--color-surface);
        box-shadow: 0 0 0 3px rgba(79, 70, 229, 0.1);
    }

    .input-field::placeholder {
        color: var(--color-text-faint);
    }

    .input-error {
        border-color: var(--color-danger);
    }

    .textarea-field {
        width: 100%;
        padding: 0.5rem 0.75rem;
        border: 1.5px solid var(--color-border);
        border-radius: 8px;
        font-size: 0.875rem;
        background: var(--color-surface-2);
        color: var(--color-text);
        resize: vertical;
        min-height: 80px;
    }

    .textarea-field:focus {
        outline: none;
        border-color: var(--color-primary);
        background: var(--color-surface);
        box-shadow: 0 0 0 3px rgba(79, 70, 229, 0.1);
    }

    .field-help {
        font-size: 0.75rem;
        color: var(--color-text-muted);
    }

    .error-text {
        font-size: 0.75rem;
        color: var(--color-danger);
        margin: 0;
    }

    .checkbox-row {
        display: flex;
        flex-wrap: wrap;
        gap: 1.5rem;
        padding: 0.25rem 0;
    }

    .checkbox-group {
        display: flex;
        align-items: center;
        gap: 0.5rem;
    }

    .checkbox {
        width: 1rem;
        height: 1rem;
        border: 1.5px solid var(--color-border-strong);
        border-radius: 4px;
        cursor: pointer;
    }

    .checkbox-label {
        font-size: 0.8125rem;
        color: var(--color-text-muted);
        font-weight: 500;
        cursor: pointer;
    }

    .attempt-count {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-width: 1.75rem;
        padding: 0.125rem 0.5rem;
        border-radius: 9999px;
        font-size: 0.8125rem;
        font-weight: 600;
        background: var(--color-success-soft);
        color: var(--color-success);
    }

    .attempt-count-warn {
        background: var(--color-danger-soft);
        color: var(--color-danger);
    }

    .field-hint {
        font-size: 0.75rem;
        color: var(--color-text-muted);
        margin: 0.25rem 0 0;
    }

    .grid-row {
        display: grid;
        grid-template-columns: 1fr;
        gap: 1rem;
    }

    @media (min-width: 640px) {
        .grid-row {
            grid-template-columns: 1fr 1fr;
        }
    }

    .accordion {
        border: 1px solid var(--color-border);
        border-radius: 10px;
        overflow: hidden;
        margin-top: 0.25rem;
        background: var(--color-surface-2);
    }

    .accordion-header {
        width: 100%;
        padding: 0.75rem 1rem;
        background-color: transparent;
        border: none;
        display: flex;
        justify-content: space-between;
        align-items: center;
        cursor: pointer;
        transition: all 0.2s ease;
    }

    .accordion-header:hover {
        background-color: var(--color-surface-3);
    }

    .accordion-title {
        font-weight: 600;
        color: var(--color-text);
        font-size: 0.875rem;
    }

    .accordion-icon {
        width: 1.125rem;
        height: 1.125rem;
        color: var(--color-text-faint);
        transition: transform 0.2s ease;
    }

    .accordion-icon.rotated {
        transform: rotate(180deg);
        color: var(--color-primary);
    }

    .accordion-content {
        padding: 1.25rem;
        background-color: var(--color-surface);
        border-top: 1px solid var(--color-border);
        display: flex;
        flex-direction: column;
        gap: 1.25rem;
    }

    .relative-container {
        position: relative;
    }

    .search-input-wrapper {
        position: relative;
    }

    .search-icon {
        position: absolute;
        inset-inline-start: 0.75rem;
        top: 50%;
        transform: translateY(-50%);
        width: 1rem;
        height: 1rem;
        color: var(--color-text-faint);
    }

    .search-input {
        padding-inline-start: 2.25rem;
    }

    .dropdown-menu {
        position: absolute;
        top: 100%;
        inset-inline-start: 0;
        inset-inline-end: 0;
        margin-top: 0.25rem;
        background: var(--color-surface);
        border: 1px solid var(--color-border);
        border-radius: 8px;
        box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.08);
        z-index: 20;
        max-height: 200px;
        overflow-y: auto;
    }

    .dropdown-item {
        width: 100%;
        padding: 0.5rem 1rem;
        text-align: start;
        background: none;
        border: none;
        font-size: 0.875rem;
        color: var(--color-text-muted);
        cursor: pointer;
        display: flex;
        justify-content: space-between;
        align-items: center;
    }

    .dropdown-item:hover {
        background-color: var(--color-surface);
        color: var(--color-text);
    }

    .badge {
        background-color: var(--color-primary-soft);
        color: var(--color-primary);
        font-size: 0.7rem;
        padding: 0.125rem 0.5rem;
        border-radius: 9999px;
        font-weight: 600;
    }

    .tags-container {
        display: flex;
        flex-wrap: wrap;
        gap: 0.5rem;
        margin-top: 0.75rem;
        min-height: 2.25rem;
        padding: 0.5rem;
        background-color: var(--color-surface);
        border-radius: 8px;
        border: 1px dashed var(--color-border-strong);
        align-items: center;
    }

    .tag {
        display: inline-flex;
        align-items: center;
        background-color: var(--color-primary);
        color: white;
        padding: 0.2rem 0.75rem;
        border-radius: 6px;
        font-size: 0.75rem;
        font-weight: 600;
    }

    .tag-gray {
        background-color: var(--color-text-muted);
        color: white;
    }

    .tag-remove {
        background: none;
        border: none;
        margin-inline-start: 0.375rem;
        cursor: pointer;
        color: white;
        opacity: 0.8;
        font-size: 1rem;
        line-height: 1;
        display: flex;
        align-items: center;
    }

    .tag-remove:hover {
        opacity: 1;
    }

    .empty-text {
        color: var(--color-text-faint);
        font-size: 0.75rem;
        margin: 0;
        width: 100%;
        text-align: center;
        font-style: italic;
    }

    .loading-pulse {
        height: 2.5rem;
        background-color: var(--color-surface-3);
        border-radius: 8px;
        animation: pulse 2s cubic-bezier(0.4, 0, 0.6, 1) infinite;
    }

    @keyframes pulse {
        0%, 100% { opacity: 1; }
        50% { opacity: .5; }
    }
</style>
