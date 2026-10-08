<script lang="ts">
    import { Dmart, type ApiResponseRecord } from "@edraj/tsdmart";
    import { Avatar } from "flowbite-svelte";
    import { EnvelopeOutline, PhoneOutline } from "flowbite-svelte-icons";
    import { getAvatar } from "@/lib/dmart_services";
    import { _ } from "@/i18n";
    import { localizedText } from "@/utils/localized";
    import { formatDate } from "@/utils/format";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import Badge from "@/components/ui/Badge.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";

    type ProfileAttrs = {
        displayname?: Record<string, string>;
        email?: string;
        msisdn?: string;
        is_email_verified?: boolean;
        is_msisdn_verified?: boolean;
        roles?: string[];
        groups?: string[];
        type?: string;
        language?: string;
        force_password_change?: boolean;
        created_at?: string;
        updated_at?: string;
    };

    let profile = $state<ApiResponseRecord | null>(null);
    let avatarUrl = $state<string | null>(null);
    let loading = $state(true);
    let error: unknown = $state(null);

    async function load() {
        loading = true;
        error = null;
        try {
            const response = await Dmart.getProfile();
            const record = response?.records?.[0];
            if (response?.status !== "success" || !record) {
                throw new Error($_("profile_load_failed"));
            }
            profile = record;
            avatarUrl = await getAvatar(record.shortname).catch(() => null);
        } catch (e) {
            error = e;
        } finally {
            loading = false;
        }
    }

    void load();

    const attrs = $derived((profile?.attributes ?? {}) as ProfileAttrs);
</script>

<div class="container mx-auto px-4 sm:px-6 py-6 max-w-4xl">
    <PageHeader title={$_("profile")} />

    {#if loading}
        <LoadingState variant="skeleton" rows={6} />
    {:else if error || !profile}
        <ErrorState title={$_("profile_load_failed")} {error} onRetry={load} />
    {:else}
        <div class="space-y-4">
            <Card>
                <div class="flex flex-col sm:flex-row items-center sm:items-start gap-5">
                    <Avatar size="xl" class="w-24 h-24 rounded-card shrink-0" src={avatarUrl ?? undefined}>
                        {profile.shortname?.charAt(0).toUpperCase() || "?"}
                    </Avatar>
                    <div class="flex flex-col items-center sm:items-start text-center sm:text-start min-w-0">
                        <h2 class="text-lg font-semibold text-text break-words">
                            {localizedText(attrs.displayname, profile.shortname)}
                        </h2>
                        <p class="text-sm text-text-muted">@{profile.shortname}</p>
                        {#if attrs.roles?.length}
                            <div class="flex flex-wrap justify-center sm:justify-start gap-1.5 mt-3">
                                {#each attrs.roles as role (role)}
                                    <Badge variant="primary" size="sm">{role}</Badge>
                                {/each}
                            </div>
                        {/if}
                        {#if attrs.force_password_change}
                            <div class="mt-3"><Badge variant="danger">{$_("password_change_required")}</Badge></div>
                        {/if}
                    </div>
                </div>
            </Card>

            <Card>
                <h3 class="text-base font-semibold text-text mb-3">{$_("contact_information")}</h3>
                {#if attrs.email || attrs.msisdn}
                    <ul class="space-y-3 text-sm">
                        {#if attrs.email}
                            <li class="flex items-center gap-3">
                                <EnvelopeOutline size="md" class="text-text-faint shrink-0" aria-hidden="true" />
                                <span class="flex-1 break-all" dir="ltr">{attrs.email}</span>
                                <Badge variant={attrs.is_email_verified ? "success" : "warning"} size="sm">
                                    {attrs.is_email_verified ? $_("verified") : $_("not_verified")}
                                </Badge>
                            </li>
                        {/if}
                        {#if attrs.msisdn}
                            <li class="flex items-center gap-3">
                                <PhoneOutline size="md" class="text-text-faint shrink-0" aria-hidden="true" />
                                <span class="flex-1 tabular-nums" dir="ltr">{attrs.msisdn}</span>
                                <Badge variant={attrs.is_msisdn_verified ? "success" : "warning"} size="sm">
                                    {attrs.is_msisdn_verified ? $_("verified") : $_("not_verified")}
                                </Badge>
                            </li>
                        {/if}
                    </ul>
                {:else}
                    <p class="text-sm text-text-muted">{$_("not_applicable")}</p>
                {/if}
            </Card>

            <Card>
                <h3 class="text-base font-semibold text-text mb-3">{$_("account_details")}</h3>
                <dl class="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-4 text-sm">
                    <div>
                        <dt class="text-xs text-text-muted">{$_("user_type")}</dt>
                        <dd class="mt-0.5 text-text">{attrs.type ?? $_("not_applicable")}</dd>
                    </div>
                    <div>
                        <dt class="text-xs text-text-muted">{$_("language")}</dt>
                        <dd class="mt-0.5 text-text">{attrs.language ?? $_("not_applicable")}</dd>
                    </div>
                    <div>
                        <dt class="text-xs text-text-muted">{$_("created_at")}</dt>
                        <dd class="mt-0.5 text-text tabular-nums">{formatDate(attrs.created_at, "datetime") || $_("not_applicable")}</dd>
                    </div>
                    <div>
                        <dt class="text-xs text-text-muted">{$_("updated")}</dt>
                        <dd class="mt-0.5 text-text tabular-nums">{formatDate(attrs.updated_at, "datetime") || $_("not_applicable")}</dd>
                    </div>
                </dl>
            </Card>

            {#if attrs.groups?.length}
                <Card>
                    <h3 class="text-base font-semibold text-text mb-3">{$_("groups")}</h3>
                    <div class="flex flex-wrap gap-1.5">
                        {#each attrs.groups as group (group)}
                            <Badge size="sm">{group}</Badge>
                        {/each}
                    </div>
                </Card>
            {/if}
        </div>
    {/if}
</div>
