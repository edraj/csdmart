<script lang="ts">
    import { createEventDispatcher } from "svelte";
    import { _ } from "@/i18n";
    import { createEntity } from "@/lib/dmart_services";
    import { ResourceType } from "@edraj/tsdmart";
    import { APPLICATIONS_SPACE } from "@/lib/constants";
    import {
        errorToastMessage,
        successToastMessage,
    } from "@/lib/toasts_messages";
    import Modal from "@/components/Modal.svelte";
    import { ChartOutline } from "flowbite-svelte-icons";

    export let onClose = () => {};

    const dispatch = createEventDispatcher();

    let title = "";
    let description = "";
    let space = "";
    let choiceType = "single";
    let options = ["", "", ""];
    let isSubmitting = false;

    function addOption() {
        options = [...options, ""];
    }

    function removeOption(index: any) {
        if (options.length <= 2) {
            errorToastMessage($_("polls.min_options_error"));
            return;
        }
        options = options.filter((_, i) => i !== index);
    }

    async function handleSubmit() {
        if (!title.trim()) {
            errorToastMessage($_("polls.title_required"));
            return;
        }
        if (!space.trim()) {
            errorToastMessage($_("polls.space_required"));
            return;
        }

        const validOptions = options.filter((o) => o.trim() !== "");
        if (validOptions.length < 2) {
            errorToastMessage($_("polls.min_valid_options_error"));
            return;
        }

        isSubmitting = true;

        try {
            const candidates = validOptions.map((opt, i) => ({
                key: `opt${i + 1}`,
                value: opt.trim(),
            }));

            const attributes: any = {
                displayname: { en: title || "" },
                description: { en: description || "", ar: "", ku: "" },
                is_active: true,
                tags: [space],
                relationships: [],
                payload: {
                    content_type: "json",
                    body: {
                        candidates,
                        choiceType,
                    },
                },
            };

            const response = await createEntity(
                APPLICATIONS_SPACE,
                "/polls",
                ResourceType.content,
                attributes,
                "auto",
            );

            if (response) {
                successToastMessage($_("polls.create_success"));
                dispatch("success");
                onClose();
            } else {
                errorToastMessage($_("polls.create_error"));
            }
        } catch (error) {
            console.error("Error creating poll:", error);
            errorToastMessage($_("polls.create_error"));
        } finally {
            isSubmitting = false;
        }
    }
</script>

<Modal
    {onClose}
    title={$_("polls.create_poll")}
    ariaLabel={$_("polls.create_poll")}
    size="lg"
>
    {#snippet icon()}
        <ChartOutline class="w-6 h-6" />
    {/snippet}

    <div class="space-y-6">
        <div>
            <label
                for="poll-title"
                class="block text-sm font-semibold text-gray-700 mb-2"
                >{$_("polls.form.title_label")}</label
            >
            <input
                id="poll-title"
                type="text"
                bind:value={title}
                class="w-full px-4 py-3 bg-white border border-gray-200 rounded-xl text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-all shadow-sm"
            />
        </div>

        <div>
            <label
                for="poll-description"
                class="block text-sm font-semibold text-gray-700 mb-2"
                >{$_("polls.form.description_label")}</label
            >
            <textarea
                id="poll-description"
                bind:value={description}
                rows="3"
                class="w-full px-4 py-3 bg-white border border-gray-200 rounded-xl text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-all shadow-sm resize-none"
            ></textarea>
        </div>

        <div>
            <label
                for="poll-space"
                class="block text-sm font-semibold text-gray-700 mb-2"
                >{$_("polls.form.space_label")}</label
            >
            <input
                id="poll-space"
                type="text"
                bind:value={space}
                class="w-full px-4 py-3 bg-white border border-gray-200 rounded-xl text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-all shadow-sm"
            />
        </div>

        <div>
            <!-- svelte-ignore a11y_label_has_associated_control -->
            <label class="block text-sm font-semibold text-gray-700 mb-3"
                >{$_("polls.form.choice_type_label")}</label
            >
            <div class="flex items-center gap-4">
                <button
                    class="px-5 py-2 rounded-xl text-sm font-semibold transition-all shadow-sm {choiceType ===
                    'single'
                        ? 'bg-[#111827] text-white border border-transparent'
                        : 'bg-white text-gray-500 border border-gray-200 hover:bg-gray-50'}"
                    onclick={() => (choiceType = "single")}
                >
                    {$_("polls.form.single_choice_button")}
                </button>
                <button
                    class="px-5 py-2 rounded-xl text-sm font-semibold transition-all shadow-sm {choiceType ===
                    'multiple'
                        ? 'bg-[#111827] text-white border border-transparent'
                        : 'bg-white text-gray-500 border border-gray-200 hover:bg-gray-50'}"
                    onclick={() => (choiceType = "multiple")}
                >
                    {$_("polls.form.multiple_choice_button")}
                </button>
            </div>
        </div>

        <div>
            <!-- svelte-ignore a11y_label_has_associated_control -->
            <label class="block text-sm font-semibold text-gray-700 mb-3"
                >{$_("polls.form.options_label")}</label
            >
            <div class="space-y-3">
                {#each options as option, index}
                    <div class="relative flex items-center">
                        <input
                            type="text"
                            bind:value={options[index]}
                            class="w-full pl-4 pr-12 py-3 bg-white border border-gray-200 rounded-xl text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-all shadow-sm"
                        />
                        {#if options.length > 2}
                            <button
                                class="absolute right-3 p-1.5 text-gray-400 hover:text-red-500 hover:bg-red-50 rounded-lg transition-colors"
                                onclick={() => removeOption(index)}
                                title={$_("polls.form.remove_option")}
                                aria-label={$_("polls.form.remove_option")}
                            >
                                <svg
                                    class="w-[18px] h-[18px]"
                                    fill="none"
                                    stroke="currentColor"
                                    viewBox="0 0 24 24"
                                    ><path
                                        stroke-linecap="round"
                                        stroke-linejoin="round"
                                        stroke-width="2"
                                        d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                                    ></path></svg
                                >
                            </button>
                        {/if}
                    </div>
                {/each}
            </div>
            <button
                class="mt-4 text-[13px] font-semibold text-gray-500 hover:text-indigo-600 transition-colors"
                onclick={addOption}
            >
                {$_("polls.form.add_option")}
            </button>
        </div>
    </div>

    {#snippet footer()}
        <button
            class="px-5 py-2.5 rounded-xl text-sm font-semibold text-gray-600 hover:bg-gray-200 hover:text-gray-900 transition-colors"
            onclick={onClose}
        >
            {$_("polls.cancel")}
        </button>
        <button
            class="px-8 py-2.5 rounded-xl text-sm font-semibold text-white bg-indigo-500 hover:bg-indigo-600 transition-all disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-2 shadow-sm shadow-indigo-200"
            onclick={handleSubmit}
            disabled={isSubmitting}
        >
            {#if isSubmitting}
                <div
                    class="w-4 h-4 border-2 border-white/30 border-t-white rounded-full animate-spin"
                ></div>
            {/if}
            {$_("polls.create_poll")}
        </button>
    {/snippet}
</Modal>
