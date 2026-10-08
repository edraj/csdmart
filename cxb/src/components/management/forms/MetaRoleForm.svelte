<script lang="ts">
    import ShortnamePicker from "./ShortnamePicker.svelte";
    import { _ } from "@/i18n";

    let {
        formData = $bindable(),
        // eslint-disable-next-line @typescript-eslint/no-unused-vars, no-useless-assignment -- $bindable() written back to the parent, never read here
        validateFn = $bindable(),
    } = $props();

    formData = {
        ...formData,
        permissions: formData.permissions || [],
    };

    let touched = $state(false);
    const invalid = $derived(touched && formData.permissions.length === 0);

    function validate() {
        touched = true;
        return formData.permissions.length !== 0;
    }

    $effect(() => {
        validateFn = validate;
    });
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    <h2 class="text-lg font-semibold text-text mb-4">{$_("role_permissions")}</h2>

    <ShortnamePicker
        bind:selected={formData.permissions}
        subpath="/permissions"
        label={$_("permissions")}
        required
        emptyText={$_("no_permissions_added")}
    />
    {#if invalid}
        <p class="mt-2 text-sm text-danger" role="alert">{$_("permissions_required")}</p>
    {/if}
</div>
