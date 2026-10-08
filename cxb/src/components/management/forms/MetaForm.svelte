<script lang="ts">
    import { Accordion, AccordionItem, Button, Checkbox, Input, Label, Textarea } from "flowbite-svelte";
    import { PenOutline } from "flowbite-svelte-icons";
    import { goto, params } from "@roxi/routify";
    import { Dmart, RequestType, ResourceType } from "@edraj/tsdmart";
    import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
    import IconButton from "@/components/ui/IconButton.svelte";
    import { errorMessage } from "@/utils/errorMessage";
    import { _ } from "@/i18n";

    let {
        isCreate,
        formData = $bindable(),
        // eslint-disable-next-line @typescript-eslint/no-unused-vars, no-useless-assignment -- $bindable() written back to the parent, never read here
        validateFn = $bindable(),
    } = $props();

    const uid = $props.id();

    function toStringOrNull(value: unknown): string | null {
        if (value === null || value === undefined) return null;
        if (typeof value === "string") {
            // Detect and unwrap stringified i18n objects from previous corrupted saves
            if (value.startsWith('{"en":')) {
                try {
                    const parsed: unknown = JSON.parse(value);
                    if (typeof parsed === "object" && parsed !== null && "en" in parsed) {
                        return toStringOrNull(parsed.en);
                    }
                } catch {}
            }
            return value || null;
        }
        if (typeof value === "object") {
            if ("en" in value) return toStringOrNull(value.en);
            return null;
        }
        return null;
    }

    formData = {
        ...formData,
        shortname: formData.shortname || null,
        is_active: formData.is_active,
        slug: formData.slug || null,
        displayname: {
            en: toStringOrNull(formData.displayname?.en),
            ar: toStringOrNull(formData.displayname?.ar),
            ku: toStringOrNull(formData.displayname?.ku),
        },
        description: {
            en: toStringOrNull(formData.description?.en),
            ar: toStringOrNull(formData.description?.ar),
            ku: toStringOrNull(formData.description?.ku),
        },
    };
    if (formData.is_active === undefined || formData.is_active === null) {
        formData.is_active = true;
    }

    let form: HTMLFormElement;
    $effect(() => {
        validateFn = validate;
    });
    function validate() {
        const isValid = form.checkValidity();

        if (!isValid) {
            form.reportValidity();
        }

        return isValid;
    }

    // ── Rename (a move), not a second save: lives behind a named icon button
    //    and its own dialog so it is never mistaken for the toolbar's Save. ──
    let isRenameOpen = $state(false);
    let newShortname = $state("");
    let isRenaming = $state(false);
    let renameError: unknown = $state(null);

    function askRename() {
        newShortname = formData.shortname;
        renameError = null;
        isRenameOpen = true;
    }

    async function rename() {
        if (!newShortname || newShortname === formData.shortname) return;
        if (!newShortname.match(/^[a-zA-Z0-9_]+$/)) {
            renameError = $_("shortname_pattern_hint");
            return;
        }

        isRenaming = true;
        renameError = null;

        try {
            const resourceType = $params.resource_type || ($params.subpath && ResourceType.folder) || ResourceType.space;
            let newSubpath =
                resourceType === ResourceType.folder
                    ? $params.subpath.split("-").slice(0, -1).join("/") || "/"
                    : $params.subpath;

            if (resourceType === ResourceType.space) {
                newSubpath = "/";
            }

            newSubpath = newSubpath.replaceAll("-", "/");

            const moveAttrb = {
                src_space_name: $params.space_name,
                src_subpath: newSubpath,
                src_shortname: formData.shortname,
                dest_space_name: resourceType === ResourceType.space ? newShortname : $params.space_name,
                dest_subpath: newSubpath,
                dest_shortname: newShortname,
            };

            await Dmart.request({
                space_name: $params.space_name,
                request_type: RequestType.move,
                records: [
                    {
                        resource_type: resourceType,
                        shortname: formData.shortname,
                        subpath: newSubpath,
                        attributes: moveAttrb,
                    },
                ],
            });

            isRenameOpen = false;
            let url = "/management/content";
            let gotoPayload: Record<string, string> = {
                space_name: $params.space_name,
            };
            if (resourceType === ResourceType.space) {
                $goto("/management/content/[space_name]", { space_name: newShortname });
                return;
            }
            if (resourceType === ResourceType.folder) {
                url += "/[space_name]/[subpath]";
                gotoPayload = {
                    ...gotoPayload,
                    subpath: `${newSubpath.replaceAll("-", "/")}-${newShortname}`,
                };
            } else {
                url += `/[space_name]/[subpath]/[shortname]/[resource_type]`;
                gotoPayload = {
                    ...gotoPayload,
                    subpath: newSubpath.replaceAll("-", "/"),
                    shortname: newShortname,
                    resource_type: resourceType,
                };
            }
            $goto(url, gotoPayload);
        } catch (error: unknown) {
            const e = error as { response?: { data?: { error?: { info?: Array<{ failed?: Array<{ error?: string }> }> } } } };
            renameError = e?.response?.data?.error?.info?.[0]?.failed?.[0]?.error ?? errorMessage(error, $_("rename_failed"));
        } finally {
            isRenaming = false;
        }
    }

    const help = "mt-1 text-xs text-text-muted";
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    <form bind:this={form} class="space-y-4" onsubmit={(e) => e.preventDefault()}>
        <h2 class="text-lg font-semibold text-text">{$_("meta_information")}</h2>

        <div>
            <Label for="{uid}-shortname" class="mb-1.5">
                {#if isCreate}<span class="text-danger" aria-hidden="true">*</span>{/if}
                {$_("shortname")}
            </Label>
            <div class="flex items-center gap-2">
                <Input
                    required
                    id="{uid}-shortname"
                    class="grow"
                    placeholder={$_("shortname")}
                    bind:value={formData.shortname}
                    disabled={!isCreate}
                    pattern={isCreate ? "[a-zA-Z0-9_]+" : undefined}
                />
                {#if isCreate}
                    <Button color="alternative" size="sm" onclick={() => (formData.shortname = "auto")}>{$_("auto")}</Button>
                {:else}
                    <IconButton label={$_("rename_shortname")} variant="outline" onclick={askRename}>
                        <PenOutline size="sm" />
                    </IconButton>
                {/if}
            </div>
            {#if isCreate}
                <p class={help}>{$_("shortname_help")}</p>
            {/if}
        </div>

        <div>
            <div class="flex items-center gap-2">
                <Checkbox id="{uid}-is_active" bind:checked={formData.is_active} />
                <Label for="{uid}-is_active" class="mb-0">{$_("active_label")}</Label>
            </div>
            <p class={help}>{$_("is_active_help")}</p>
        </div>

        <div>
            <Label for="{uid}-slug" class="mb-1.5">{$_("slug")}</Label>
            <Input id="{uid}-slug" placeholder="url-friendly-name" bind:value={formData.slug} />
            <p class={help}>{$_("slug_help")}</p>
        </div>

        <Accordion flush>
            <AccordionItem>
                {#snippet header()}{$_("translations")}{/snippet}
                <div class="py-2 space-y-4">
                    <fieldset>
                        <legend class="text-sm font-medium text-text mb-2">{$_("displayname")}</legend>
                        <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
                            <div>
                                <Label for="{uid}-displayname-en" class="text-sm mb-1">{$_("english")}</Label>
                                <Input id="{uid}-displayname-en" bind:value={formData.displayname.en} />
                            </div>
                            <div>
                                <Label for="{uid}-displayname-ar" class="text-sm mb-1">{$_("arabic")}</Label>
                                <Input id="{uid}-displayname-ar" dir="auto" bind:value={formData.displayname.ar} />
                            </div>
                            <div>
                                <Label for="{uid}-displayname-ku" class="text-sm mb-1">{$_("kurdish")}</Label>
                                <Input id="{uid}-displayname-ku" dir="auto" bind:value={formData.displayname.ku} />
                            </div>
                        </div>
                    </fieldset>

                    <fieldset>
                        <legend class="text-sm font-medium text-text mb-2">{$_("description")}</legend>
                        <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
                            <div>
                                <Label for="{uid}-description-en" class="text-sm mb-1">{$_("english")}</Label>
                                <Textarea id="{uid}-description-en" bind:value={formData.description.en} rows={3} />
                            </div>
                            <div>
                                <Label for="{uid}-description-ar" class="text-sm mb-1">{$_("arabic")}</Label>
                                <Textarea id="{uid}-description-ar" dir="auto" bind:value={formData.description.ar} rows={3} />
                            </div>
                            <div>
                                <Label for="{uid}-description-ku" class="text-sm mb-1">{$_("kurdish")}</Label>
                                <Textarea id="{uid}-description-ku" dir="auto" bind:value={formData.description.ku} rows={3} />
                            </div>
                        </div>
                    </fieldset>
                </div>
            </AccordionItem>
        </Accordion>
    </form>
</div>

<ConfirmDialog
    bind:open={isRenameOpen}
    variant="primary"
    title={$_("rename_shortname")}
    body={$_("rename_shortname_help")}
    confirmLabel={$_("rename")}
    loading={isRenaming}
    loadingLabel={$_("renaming")}
    error={renameError}
    onConfirm={rename}
>
    <div>
        <Label for="{uid}-new-shortname" class="mb-1.5">{$_("new_shortname")}</Label>
        <Input id="{uid}-new-shortname" placeholder={formData.shortname} bind:value={newShortname} pattern="[a-zA-Z0-9_]+" />
        <p class="mt-1 text-xs text-text-muted">{$_("shortname_pattern_hint")}</p>
    </div>
</ConfirmDialog>
