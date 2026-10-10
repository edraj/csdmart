<script lang="ts">
    import { Accordion, AccordionItem, Checkbox, Input, Label, Select, Textarea } from "flowbite-svelte";
    import { canClearLockout, readFailedAttempts, resolveAttemptCount } from "@shared/user-lockout";
    import Badge from "@/components/ui/Badge.svelte";
    import ShortnamePicker from "./ShortnamePicker.svelte";
    import { _ } from "@/i18n";

    let {
        formData = $bindable(),
        // eslint-disable-next-line @typescript-eslint/no-unused-vars, no-useless-assignment -- $bindable() written back to the parent, never read here
        validateFn = $bindable(),
        isCreate = false,
    } = $props();

    const uid = $props.id();

    // Kept as script constants: a literal `{` in a markup attribute would
    // otherwise need `{'{'}` escapes.
    const EMAIL_PATTERN = "[a-z0-9._%+-]+@[a-z0-9.-]+\\.[a-z]{2,6}$";
    const MSISDN_PATTERN = "^\\+?\\d{7,15}$";

    let form: HTMLFormElement;

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
        msisdn: formData.msisdn || null,
        is_email_verified: formData.is_email_verified || false,
        is_msisdn_verified: formData.is_msisdn_verified || false,
        force_password_change: formData.force_password_change || false,
        type: formData.type || "mobile",
        language: formData.language || null,
        roles: formData.roles || [],
        groups: formData.groups || [],
        // Directory fields (docs/user-directory-fields.md). The server
        // normalizes them and checks them only when they change.
        mailbox: formData.mailbox || null,
        mail_aliases: formData.mail_aliases || [],
        services: formData.services || [],
        firebase_token: formData.firebase_token || null,
        google_id: formData.google_id || null,
        facebook_id: formData.facebook_id || null,
        apple_id: formData.apple_id || null,
        social_avatar_url: formData.social_avatar_url || null,
        // Admin UI never sets passwords (/managed/request rejects them); force
        // these undefined so neither a loaded $argon2id hash nor a typed value is
        // ever sent. Users set their own password via login OTP / password reset.
        password: undefined,
        old_password: undefined,
        // Same reason, different risk. attempt_count IS the lockout, and this
        // form round-trips whatever the API returned — so echoing it back on an
        // ordinary save would write a value read seconds ago, rolling back
        // increments an in-flight brute-force run landed in between. Stripped
        // unless the admin explicitly asks to clear it below (JSON.stringify
        // drops undefined keys, which is how `password` is handled too).
        attempt_count: undefined,
    };

    // The unlock gesture. Deliberately NOT is_active: a locked account is still
    // active (the lock is counter-only), and this form emits is_active on every
    // save — so keying an unlock off that flag would mean renaming a locked user
    // silently cancels their lockout. Sending an explicit 0 is unambiguous, and
    // the server records it in the audit history.
    function applyAttemptReset() {
        formData.attempt_count = resolveAttemptCount(failedAttempts, resetAttempts);
    }

    // Aliases are edited one per line and services comma-separated; both are
    // sent as arrays. Blank entries are dropped here, case and duplicates are
    // the server's to fold.
    let aliasesText = $state((formData.mail_aliases as string[]).join("\n"));
    let servicesText = $state((formData.services as string[]).join(", "));
    const splitList = (text: string, separator: RegExp) =>
        text.split(separator).map((s) => s.trim()).filter((s) => s.length > 0);
    $effect(() => {
        formData.mail_aliases = splitList(aliasesText, /\r?\n/);
    });
    $effect(() => {
        formData.services = splitList(servicesText, /,/);
    });

    // User types are server identifiers; shown as they are.
    const userTypeOptions = ["bot", "mobile", "web", "admin", "api"].map((type) => ({ name: type, value: type }));

    function validate() {
        const isValid = form.checkValidity();
        isEmailValid = validateEmail(formData.email);

        if (!isValid || !isEmailValid) {
            form.reportValidity();
            return false;
        }
        return isValid;
    }

    $effect(() => {
        validateFn = validate;
    });

    let isEmailValid = $state(true);
    let emailTouched = $state(false);
    function validateEmail(email: string | null): boolean {
        if (!email) return true; // Empty is allowed as the field isn't required
        const emailRegex = /^[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,6}$/;
        return emailRegex.test(email);
    }
    $effect(() => {
        if (emailTouched) {
            isEmailValid = validateEmail(formData.email);
        }
    });

    const help = "mt-1 text-xs text-text-muted";
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    <h2 class="text-lg font-semibold text-text mb-4">{$_("user_information")}</h2>

    <form bind:this={form} class="space-y-4" onsubmit={(e) => e.preventDefault()}>
        <div>
            <Label for="{uid}-email" class="mb-1.5">{$_("email")}</Label>
            <Input
                id="{uid}-email"
                type="email"
                placeholder="user@example.com"
                pattern={EMAIL_PATTERN}
                bind:value={formData.email}
                color={!isEmailValid && emailTouched ? "red" : undefined}
                aria-invalid={!isEmailValid && emailTouched}
                aria-describedby={!isEmailValid && emailTouched ? `${uid}-email-error` : undefined}
                onblur={() => (emailTouched = true)}
            />
            {#if !isEmailValid && emailTouched}
                <p id="{uid}-email-error" class="mt-1 text-sm text-danger" role="alert">{$_("invalid_email")}</p>
            {/if}
        </div>

        <div>
            <Label for="{uid}-msisdn" class="mb-1.5">{$_("msisdn_label")}</Label>
            <Input id="{uid}-msisdn" placeholder="+964723456789" bind:value={formData.msisdn} pattern={MSISDN_PATTERN} dir="ltr" />
        </div>

        {#if !isCreate}
            <div>
                <p class="text-sm font-medium text-text mb-1.5">{$_("failed_login_attempts")}</p>
                <div class="flex items-center gap-3">
                    <Badge variant={showClearLockout ? "danger" : "success"}>{failedAttempts}</Badge>
                    {#if showClearLockout}
                        <Checkbox id="{uid}-reset_attempt_count" bind:checked={resetAttempts} onchange={applyAttemptReset} />
                        <Label for="{uid}-reset_attempt_count" class="mb-0">{$_("clear_on_save")}</Label>
                    {/if}
                </div>
                <p class={help}>{$_("failed_login_attempts_help")}</p>
            </div>
        {/if}

        <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div class="flex items-center gap-2">
                <Checkbox id="{uid}-force_password_change" bind:checked={formData.force_password_change} />
                <Label for="{uid}-force_password_change" class="mb-0">{$_("force_password_change")}</Label>
            </div>
            <div class="flex items-center gap-2">
                <Checkbox id="{uid}-is_email_verified" bind:checked={formData.is_email_verified} />
                <Label for="{uid}-is_email_verified" class="mb-0">{$_("email_verified")}</Label>
            </div>
            <div class="flex items-center gap-2">
                <Checkbox id="{uid}-is_msisdn_verified" bind:checked={formData.is_msisdn_verified} />
                <Label for="{uid}-is_msisdn_verified" class="mb-0">{$_("phone_verified")}</Label>
            </div>
        </div>

        <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
                <Label for="{uid}-user_type" class="mb-1.5">{$_("user_type")}</Label>
                <Select id="{uid}-user_type" items={userTypeOptions} bind:value={formData.type} />
            </div>
            <div>
                <Label for="{uid}-language" class="mb-1.5">{$_("preferred_language")}</Label>
                <Input id="{uid}-language" bind:value={formData.language} placeholder="en" />
            </div>
        </div>

        <Accordion flush>
            <AccordionItem>
                {#snippet header()}{$_("roles_and_groups")}{/snippet}
                <div class="py-2 space-y-6">
                    <ShortnamePicker bind:selected={formData.roles} subpath="/roles" label={$_("roles")} emptyText={$_("no_roles_added")} />
                    <ShortnamePicker bind:selected={formData.groups} subpath="/groups" label={$_("groups")} emptyText={$_("no_groups_added")} />
                </div>
            </AccordionItem>

            <AccordionItem>
                {#snippet header()}{$_("mail_and_services")}{/snippet}
                <div class="py-2 space-y-4">
                    <div>
                        <Label for="{uid}-mailbox" class="mb-1.5">{$_("mailbox")}</Label>
                        <Input id="{uid}-mailbox" type="email" placeholder="user@example.org" bind:value={formData.mailbox} dir="ltr" />
                        <p class={help}>{$_("mailbox_help")}</p>
                    </div>
                    <div>
                        <Label for="{uid}-mail_aliases" class="mb-1.5">{$_("mail_aliases")}</Label>
                        <Textarea id="{uid}-mail_aliases" class="w-full" bind:value={aliasesText} rows={3} dir="ltr" />
                        <p class={help}>{$_("mail_aliases_help")}</p>
                    </div>
                    <div>
                        <Label for="{uid}-services" class="mb-1.5">{$_("services")}</Label>
                        <Input id="{uid}-services" placeholder="mail, matrix, gitea" bind:value={servicesText} dir="ltr" />
                        <p class={help}>{$_("services_help")}</p>
                    </div>
                </div>
            </AccordionItem>

            <AccordionItem>
                {#snippet header()}{$_("social_and_external_ids")}{/snippet}
                <div class="py-2 space-y-4">
                    <div>
                        <Label for="{uid}-firebase_token" class="mb-1.5">{$_("firebase_token")}</Label>
                        <Textarea id="{uid}-firebase_token" bind:value={formData.firebase_token} rows={2} dir="ltr" />
                    </div>

                    <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
                        <div>
                            <Label for="{uid}-google_id" class="mb-1.5">{$_("google_id")}</Label>
                            <Input id="{uid}-google_id" bind:value={formData.google_id} dir="ltr" />
                        </div>
                        <div>
                            <Label for="{uid}-facebook_id" class="mb-1.5">{$_("facebook_id")}</Label>
                            <Input id="{uid}-facebook_id" bind:value={formData.facebook_id} dir="ltr" />
                        </div>
                        <div>
                            <Label for="{uid}-apple_id" class="mb-1.5">{$_("apple_id")}</Label>
                            <Input id="{uid}-apple_id" bind:value={formData.apple_id} dir="ltr" />
                        </div>
                    </div>

                    <div>
                        <Label for="{uid}-social_avatar_url" class="mb-1.5">{$_("social_avatar_url")}</Label>
                        <Input id="{uid}-social_avatar_url" type="url" bind:value={formData.social_avatar_url} dir="ltr" />
                    </div>
                </div>
            </AccordionItem>
        </Accordion>
    </form>
</div>
