<script lang="ts">
    import { Button } from "flowbite-svelte";
    import { CheckOutline, ClipboardOutline, CloseOutline, PaletteOutline } from "flowbite-svelte-icons";
    import { clearNavbarTheme, navbarTheme, setNavbarTheme, type NavbarTheme } from "@/stores/navbar_theme";
    import { _ } from "@/i18n";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import IconButton from "@/components/ui/IconButton.svelte";

    type Preset = { key: string; value: string };

    const solidPresets: Preset[] = [
        { key: "preset_midnight_navy", value: "#1B2A4A" },
        { key: "preset_volcanic_slate", value: "#2D3436" },
        { key: "preset_royal_indigo", value: "#4A00E0" },
        { key: "preset_emerald_night", value: "#0D7377" },
        { key: "preset_deep_rosewood", value: "#6B0F1A" },
    ];

    const gradientPresets: Preset[] = [
        { key: "preset_aurora_borealis", value: "linear-gradient(135deg, #0F2027, #203A43, #2C5364)" },
        { key: "preset_cosmic_voyager", value: "linear-gradient(135deg, #141E30, #243B55)" },
        { key: "preset_ember_horizon", value: "linear-gradient(135deg, #1A1A2E, #16213E, #0F3460)" },
        { key: "preset_velvet_dusk", value: "linear-gradient(135deg, #2C003E, #512DA8, #7C4DFF)" },
        { key: "preset_neon_mirage", value: "linear-gradient(135deg, #0D0D0D, #1A237E, #00BCD4)" },
    ];

    const gradientDirections = [
        { labelKey: "direction_to_right", value: "to right" },
        { labelKey: "direction_to_bottom_right", value: "to bottom right" },
        { labelKey: "direction_to_bottom", value: "to bottom" },
        { labelKey: "direction_to_bottom_left", value: "to bottom left" },
        { labelKey: "direction_to_left", value: "to left" },
    ];

    let customSolidColor = $state("#3C54F0");
    let gradientStart = $state("#141E30");
    let gradientEnd = $state("#243B55");
    let gradientDirection = $state("to right");

    function isActive(theme: NavbarTheme | null, type: string, value: string): boolean {
        return theme?.type === type && theme?.value === value;
    }

    const customGradient = $derived(`linear-gradient(${gradientDirection}, ${gradientStart}, ${gradientEnd})`);

    let copiedPreset = $state<string | null>(null);
    let copyTimer: ReturnType<typeof setTimeout> | undefined;
    async function copyThemeConfig(type: string, value: string, presetKey: string) {
        const snippet = `"theme": ${JSON.stringify({ type, value }, null, 4)}`;
        try {
            await navigator.clipboard.writeText(snippet);
            copiedPreset = presetKey;
            clearTimeout(copyTimer);
            copyTimer = setTimeout(() => (copiedPreset = null), 1500);
        } catch {
            copiedPreset = null;
        }
    }
</script>

{#snippet swatches(type: "solid" | "gradient", presets: Preset[])}
    <ul class="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 gap-4" role="list">
        {#each presets as preset (preset.value)}
            {@const active = isActive($navbarTheme, type, preset.value)}
            <li class="relative group">
                <button
                    type="button"
                    class="block w-full aspect-[3/2] rounded-card shadow-card border-2 transition-all duration-200 hover:scale-105 hover:shadow-modal cursor-pointer overflow-hidden
                        {active ? 'border-primary ring-2 ring-primary/40 scale-105' : 'border-transparent'}"
                    style:background={preset.value}
                    aria-pressed={active}
                    onclick={() => setNavbarTheme(type, preset.value)}
                >
                    <span class="absolute bottom-0 start-0 end-0 text-center text-[11px] font-medium text-white py-1.5 bg-black/35 backdrop-blur-sm">
                        {$_(preset.key)}
                    </span>
                    {#if active}
                        <span class="absolute top-1.5 end-1.5 w-5 h-5 rounded-full bg-white text-primary flex items-center justify-center shadow" aria-hidden="true">
                            <CheckOutline size="xs" />
                        </span>
                    {/if}
                </button>
                <!-- Sibling of the swatch button, never nested inside it. -->
                <span class="absolute top-1.5 start-1.5 opacity-0 group-hover:opacity-100 focus-within:opacity-100 transition-opacity">
                    <IconButton
                        label={copiedPreset === preset.key ? $_("copied") : $_("copy_config_snippet")}
                        size="sm"
                        class="bg-white/85 hover:bg-white text-slate-700 shadow"
                        onclick={() => copyThemeConfig(type, preset.value, preset.key)}
                    >
                        {#if copiedPreset === preset.key}
                            <CheckOutline size="xs" class="text-success" />
                        {:else}
                            <ClipboardOutline size="xs" />
                        {/if}
                    </IconButton>
                </span>
            </li>
        {/each}
    </ul>
{/snippet}

<div class="container mx-auto px-4 sm:px-6 py-6 max-w-4xl">
    <PageHeader
        title={$_("theme")}
        description={$_("theme_description")}
        icon={PaletteOutline}
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    >
        {#snippet actions()}
            {#if $navbarTheme}
                <Button color="alternative" size="sm" onclick={() => clearNavbarTheme()}>
                    <CloseOutline size="sm" class="me-1.5" aria-hidden="true" />
                    {$_("reset_to_default")}
                </Button>
            {/if}
        {/snippet}
    </PageHeader>

    <section class="mb-8" aria-labelledby="solid-heading">
        <h2 id="solid-heading" class="text-lg font-semibold text-text mb-4">{$_("solid_colors")}</h2>
        {@render swatches("solid", solidPresets)}
    </section>

    <section class="mb-8" aria-labelledby="gradient-heading">
        <h2 id="gradient-heading" class="text-lg font-semibold text-text mb-4">{$_("gradient_colors")}</h2>
        {@render swatches("gradient", gradientPresets)}
    </section>

    <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
        <Card>
            <h2 class="text-base font-semibold text-text mb-4">{$_("custom_color")}</h2>
            <div class="flex items-center gap-4">
                <label class="relative cursor-pointer shrink-0">
                    <span class="sr-only">{$_("custom_color")}</span>
                    <input type="color" class="absolute inset-0 opacity-0 w-full h-full cursor-pointer" bind:value={customSolidColor} />
                    <span class="block w-12 h-12 rounded-card border-2 border-border shadow-card transition-transform hover:scale-105" style:background={customSolidColor}></span>
                </label>
                <span class="flex-1 text-sm font-mono text-text-muted" dir="ltr">{customSolidColor}</span>
                <Button size="sm" color="primary" outline onclick={() => setNavbarTheme("custom-solid", customSolidColor)}>
                    {$_("apply")}
                </Button>
            </div>
        </Card>

        <Card>
            <h2 class="text-base font-semibold text-text mb-4">{$_("custom_gradient")}</h2>
            <div class="flex items-center gap-3 mb-3">
                <label class="relative cursor-pointer shrink-0">
                    <span class="sr-only">{$_("gradient_start")}</span>
                    <input type="color" class="absolute inset-0 opacity-0 w-full h-full cursor-pointer" bind:value={gradientStart} />
                    <span class="block w-10 h-10 rounded-control border-2 border-border shadow-card transition-transform hover:scale-105" style:background={gradientStart}></span>
                </label>
                <span class="text-text-faint rtl:rotate-180" aria-hidden="true">→</span>
                <label class="relative cursor-pointer shrink-0">
                    <span class="sr-only">{$_("gradient_end")}</span>
                    <input type="color" class="absolute inset-0 opacity-0 w-full h-full cursor-pointer" bind:value={gradientEnd} />
                    <span class="block w-10 h-10 rounded-control border-2 border-border shadow-card transition-transform hover:scale-105" style:background={gradientEnd}></span>
                </label>
                <label class="flex-1 min-w-0">
                    <span class="sr-only">{$_("gradient_direction")}</span>
                    <select
                        class="w-full text-sm rounded-control border border-border bg-surface-2 text-text px-2 py-2"
                        bind:value={gradientDirection}
                    >
                        {#each gradientDirections as dir (dir.value)}
                            <option value={dir.value}>{$_(dir.labelKey)}</option>
                        {/each}
                    </select>
                </label>
            </div>
            <div class="flex items-center gap-3">
                <div class="flex-1 h-8 rounded-control border border-border" style:background={customGradient} aria-hidden="true"></div>
                <Button size="sm" color="primary" outline onclick={() => setNavbarTheme("custom-gradient", customGradient)}>
                    {$_("apply")}
                </Button>
            </div>
        </Card>
    </div>
</div>
