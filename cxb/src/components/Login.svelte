<script lang="ts">
    import { Button, Input, Label, Spinner } from "flowbite-svelte";
    import { EyeOutline, EyeSlashOutline } from "flowbite-svelte-icons";
    import { url } from "@roxi/routify";
    import { signin } from "@/stores/user";
    import { _ } from "@/i18n";
    import { website } from "@/config";
    import { onMount } from "svelte";
    import { consumeResetDone } from "@/lib/password_reset";
    import { errorMessage } from "@/utils/errorMessage";
    import IconButton from "@/components/ui/IconButton.svelte";

    let username: string = $state("");
    let password: string = $state("");
    let errorText: string | null = $state(null);
    let showPassword: boolean = $state(false);
    let isLoginLoading: boolean = $state(false);
    // Set by reset-password/confirm.svelte just before it navigates here.
    let resetSuccess: boolean = $state(false);

    onMount(() => {
        resetSuccess = consumeResetDone();
    });

    async function handleSubmit(event: Event) {
        event.preventDefault();
        errorText = null;
        try {
            isLoginLoading = true;
            await signin(username, password);
            window.location.reload();
        } catch (error: unknown) {
            errorText = errorMessage(error, $_("login_failed"));
        }
        isLoginLoading = false;
    }
</script>

<!-- Phone: one column with a short brand strip on top. Desktop: form | brand. -->
<div class="flex flex-col md:flex-row min-h-svh bg-surface text-text">
    <aside
        class="order-first md:order-last md:w-5/12 lg:w-1/2 bg-primary text-text-on-primary
            flex items-center md:items-center justify-center px-6 py-5 md:py-12"
        aria-hidden="true"
    >
        <div class="flex md:flex-col items-center md:items-start gap-3 md:gap-4 max-w-md w-full md:w-auto">
            <div class="text-2xl md:text-5xl font-bold tracking-tight">{website.display_name || "dmart"}</div>
            <p class="hidden md:block text-base opacity-90">{website.description}</p>
            <span class="ms-auto md:ms-0 text-xs md:text-sm uppercase tracking-wider opacity-80">{$_("admin_console")}</span>
        </div>
    </aside>

    <main class="flex-1 flex items-center justify-center px-4 sm:px-8 py-10">
        <div class="w-full max-w-sm">
            <h1 class="text-2xl font-semibold text-text">{$_("welcome_back")}</h1>
            <p class="mt-1 text-sm text-text-muted">{$_("login_subtitle")}</p>

            {#if resetSuccess}
                <p class="mt-4 text-sm text-success" role="status">{$_("reset_password_success")}</p>
            {/if}

            <form onsubmit={handleSubmit} class="mt-8 space-y-5">
                <div>
                    <Label for="username" class="mb-2">{$_("shortname")}</Label>
                    <Input
                        id="username"
                        name="username"
                        placeholder={$_("shortname")}
                        type="text"
                        autocomplete="username"
                        bind:value={username}
                        color={errorText ? "red" : "default"}
                        required
                    />
                </div>
                <div>
                    <Label for="password" class="mb-2">{$_("password")}</Label>
                    <div class="relative">
                        <Input
                            id="password"
                            name="password"
                            placeholder={$_("password")}
                            type={showPassword ? "text" : "password"}
                            autocomplete="current-password"
                            bind:value={password}
                            color={errorText ? "red" : "default"}
                            class="pe-11"
                            required
                        />
                        <span class="absolute inset-y-0 end-1 flex items-center">
                            <IconButton
                                label={$_("toggle_password_visibility")}
                                pressed={showPassword}
                                controls="password"
                                onclick={() => (showPassword = !showPassword)}
                            >
                                {#if showPassword}<EyeSlashOutline size="sm" />{:else}<EyeOutline size="sm" />{/if}
                            </IconButton>
                        </span>
                    </div>
                </div>

                <Button type="submit" color="primary" class="w-full" disabled={isLoginLoading}>
                    {#if isLoginLoading}
                        <Spinner class="me-3" size="4" />
                        {$_("logging_in")}
                    {:else}
                        {$_("login")}
                    {/if}
                </Button>

                {#if errorText}
                    <p class="text-sm text-danger" role="alert">{errorText}</p>
                {/if}

                <p class="text-center text-sm">
                    <a href={$url("/reset-password")} class="text-primary hover:underline rounded-control">
                        {$_("forgot_password")}
                    </a>
                </p>
            </form>
        </div>
    </main>
</div>
