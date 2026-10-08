<script lang="ts">
  import { _ } from "@/i18n";
  import { createEntity } from "@/lib/dmart_services";
  import { ResourceType } from "@edraj/tsdmart";
  import { APPLICATIONS_SPACE } from "@/lib/constants";
  import { toasts } from "@/lib/toast";
  import { log } from "@/lib/logger";
  import Modal from "@/components/Modal.svelte";
  import IconButton from "@/components/ui/IconButton.svelte";
  import { ChartOutline, TrashBinOutline } from "flowbite-svelte-icons";

  let {
    onClose = () => {},
    onSuccess = () => {},
  }: { onClose?: () => void; onSuccess?: () => void } = $props();

  type ChoiceType = "single" | "multiple";

  let title = $state("");
  let description = $state("");
  let space = $state("");
  let choiceType = $state<ChoiceType>("single");
  let options = $state(["", "", ""]);
  let isSubmitting = $state(false);

  const uid = $props.id();
  const formId = `${uid}-poll-form`;

  const inputClass =
    "w-full px-3 py-2 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary";

  function addOption() {
    options = [...options, ""];
  }

  function removeOption(index: number) {
    if (options.length <= 2) {
      toasts.error($_("polls.min_options_error"));
      return;
    }
    options = options.filter((_, i) => i !== index);
  }

  async function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    if (!title.trim()) {
      toasts.error($_("polls.title_required"));
      return;
    }
    if (!space.trim()) {
      toasts.error($_("polls.space_required"));
      return;
    }

    const validOptions = options.filter((o) => o.trim() !== "");
    if (validOptions.length < 2) {
      toasts.error($_("polls.min_valid_options_error"));
      return;
    }

    isSubmitting = true;

    try {
      const candidates = validOptions.map((opt, i) => ({
        key: `opt${i + 1}`,
        value: opt.trim(),
      }));

      const attributes = {
        displayname: { en: title.trim() },
        description: { en: description.trim(), ar: "", ku: "" },
        is_active: true,
        tags: [space.trim()],
        relationships: [],
        payload: {
          content_type: "json",
          body: { candidates, choiceType },
        },
      };

      const response = await createEntity(APPLICATIONS_SPACE, "/polls", ResourceType.content, attributes, "auto");

      if (response) {
        toasts.success($_("polls.create_success"));
        onSuccess();
        onClose();
      } else {
        toasts.error($_("polls.create_error"));
      }
    } catch (error) {
      log.error("Error creating poll:", error);
      toasts.error($_("polls.create_error"));
    } finally {
      isSubmitting = false;
    }
  }
</script>

<Modal {onClose} title={$_("polls.create_poll")} size="lg" dismissable={!isSubmitting}>
  {#snippet icon()}
    <ChartOutline size="lg" />
  {/snippet}

  <form id={formId} class="space-y-5" onsubmit={handleSubmit}>
    <div>
      <label for="{uid}-title" class="block text-sm font-medium text-text mb-1.5">
        {$_("polls.form.title_label")}
      </label>
      <input id="{uid}-title" type="text" bind:value={title} class={inputClass} required data-autofocus />
    </div>

    <div>
      <label for="{uid}-description" class="block text-sm font-medium text-text mb-1.5">
        {$_("polls.form.description_label")}
      </label>
      <textarea id="{uid}-description" bind:value={description} rows="3" class="{inputClass} resize-none"></textarea>
    </div>

    <div>
      <label for="{uid}-space" class="block text-sm font-medium text-text mb-1.5">
        {$_("polls.form.space_label")}
      </label>
      <input id="{uid}-space" type="text" bind:value={space} class={inputClass} required />
    </div>

    <fieldset class="border-0 p-0 m-0 min-w-0">
      <legend class="block text-sm font-medium text-text mb-1.5">{$_("polls.form.choice_type_label")}</legend>
      <div class="inline-flex rounded-control border border-border bg-surface-2 p-0.5" role="radiogroup">
        {#each [["single", $_("polls.form.single_choice_button")], ["multiple", $_("polls.form.multiple_choice_button")]] as [value, label] (value)}
          <button
            type="button"
            role="radio"
            aria-checked={choiceType === value}
            class="px-4 h-8 rounded-control text-sm font-medium transition-colors cursor-pointer
 {choiceType === value ? 'bg-primary text-text-on-primary' : 'text-text-muted hover:text-text hover:bg-surface-3'}"
            onclick={() => (choiceType = value as ChoiceType)}
          >
            {label}
          </button>
        {/each}
      </div>
    </fieldset>

    <fieldset class="border-0 p-0 m-0 min-w-0">
      <legend class="block text-sm font-medium text-text mb-1.5">{$_("polls.form.options_label")}</legend>
      <div class="space-y-2">
        {#each options as _option, index (index)}
          <div class="flex items-center gap-2">
            <input
              type="text"
              bind:value={options[index]}
              class={inputClass}
              aria-label={$_("polls.form.option_n", { values: { n: index + 1 } })}
            />
            {#if options.length > 2}
              <IconButton
                label={$_("polls.form.remove_option")}
                variant="danger"
                size="sm"
                onclick={() => removeOption(index)}
              >
                <TrashBinOutline size="sm" />
              </IconButton>
            {/if}
          </div>
        {/each}
      </div>
      <button type="button" class="app-btn app-btn-ghost app-btn-sm mt-2" onclick={addOption}>
        {$_("polls.form.add_option")}
      </button>
    </fieldset>
  </form>

  {#snippet footer()}
    <button type="button" class="app-btn app-btn-secondary" onclick={onClose} disabled={isSubmitting}>
      {$_("polls.cancel")}
    </button>
    <button type="submit" form={formId} class="app-btn app-btn-primary" disabled={isSubmitting} aria-busy={isSubmitting}>
      {#if isSubmitting}
        <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
      {/if}
      {$_("polls.create_poll")}
    </button>
  {/snippet}
</Modal>
