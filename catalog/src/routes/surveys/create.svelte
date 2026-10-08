<script lang="ts">
  import { goto as gotoStore } from "@roxi/routify";
  import SurveyForm, { type SurveyDraft } from "@/components/forms/SurveyForm.svelte";
  import { createEntity } from "@/lib/dmart_services";
  import { APPLICATIONS_SPACE } from "@/lib/constants";
  import { ResourceType } from "@edraj/tsdmart";
  import { _ } from "@/i18n";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { toasts } from "@/lib/toast";
  import { ClipboardListOutline } from "flowbite-svelte-icons";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  let survey = $state<SurveyDraft>({
    title: "",
    description: "",
    questions: [],
  });

  let isLoading = $state(false);
  let error = $state("");

  $effect(() => setTitle($_("create_survey.title")));

  function validate(): string {
    if (!survey.title.trim()) return $_("create_survey.survey_title_required");
    if (!survey.questions || survey.questions.length === 0) return $_("create_survey.one_question_required");

    for (let i = 0; i < survey.questions.length; i++) {
      const question = survey.questions[i];
      if (!question.question.trim()) {
        return $_("create_survey.question_text_required_number", { values: { number: i + 1 } });
      }
      if (["single", "multi", "select"].includes(question.type)) {
        if (!question.options || question.options.length === 0) {
          return $_("create_survey.question_options_required", { values: { number: i + 1 } });
        }
        for (let j = 0; j < question.options.length; j++) {
          const option = question.options[j];
          if (!option.value?.trim() || !option.label?.trim()) {
            return $_("create_survey.option_complete_required", { values: { number: i + 1, option: j + 1 } });
          }
        }
      }
    }
    return "";
  }

  async function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    error = validate();
    if (error) return;

    isLoading = true;

    try {
      const shortname =
        survey.title
          .toLowerCase()
          .replace(/[^a-z0-9\s-]/g, "")
          .replace(/\s+/g, "-")
          .substring(0, 50) +
        "_" +
        Date.now();

      const attributes = {
        displayname: { en: survey.title.trim() },
        description: { en: survey.description.trim(), ar: "", ku: "" },
        is_active: true,
        tags: [],
        relationships: [],
        payload: {
          content_type: "json",
          body: { questions: survey.questions },
        },
      };

      // Surveys live in the applications space (the list and manage pages
      // read /surveys there, and the admin settings page creates that folder).
      const result = await createEntity(APPLICATIONS_SPACE, "/surveys", ResourceType.content, attributes, shortname);

      if (result) {
        toasts.success($_("create_survey.success"));
        goto("/surveys");
      } else {
        error = $_("create_survey.error");
      }
    } catch (err) {
      log.error("Error creating survey:", err);
      error = $_("create_survey.error_creating");
    } finally {
      isLoading = false;
    }
  }

  function handleCancel() {
    goto("/surveys");
  }
</script>

<div class="mx-auto max-w-3xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader
    title={$_("create_survey.title")}
    description={$_("create_survey.subtitle")}
    icon={ClipboardListOutline}
    backHref="/surveys"
    backLabel={$_("survey_manage.back_to_surveys")}
  />

  {#if error}
    <ErrorState compact message={error} class="mb-6" />
  {/if}

  <form id="create-survey-form" onsubmit={handleSubmit} class="space-y-6">
    <SurveyForm bind:survey />

    <div class="flex flex-col-reverse sm:flex-row sm:items-center sm:justify-end gap-3 pt-2">
      <button type="button" class="app-btn app-btn-secondary" onclick={handleCancel} disabled={isLoading}>
        {$_("create_survey.cancel")}
      </button>
      <button type="submit" class="app-btn app-btn-primary" disabled={isLoading} aria-busy={isLoading}>
        {#if isLoading}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("create_survey.creating")}
        {:else}
          {$_("create_survey.create_button")}
        {/if}
      </button>
    </div>
  </form>
</div>
