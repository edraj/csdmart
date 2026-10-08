<script lang="ts">
  import { onMount } from "svelte";
  import { goto as gotoStore } from "@roxi/routify";
  import { getSurveys, submitSurveyResponse } from "@/lib/dmart_services";
  import { APPLICATIONS_SPACE } from "@/lib/constants";
  import { DmartScope } from "@edraj/tsdmart";
  import { _, locale } from "@/i18n";
  import { localized } from "@/lib/catalogItems";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { toasts } from "@/lib/toast";
  import { user } from "@/stores/user";
  import { ChevronRightOutline, ClipboardListOutline, PlusOutline } from "flowbite-svelte-icons";
  import Modal from "@/components/Modal.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  interface SurveyOption {
    id: string;
    label: string;
    value: string;
  }

  interface SurveyQuestion {
    id: string;
    question: string;
    type: "input" | "text" | "single" | "multi" | "select";
    required?: boolean;
    options?: SurveyOption[];
  }

  type Answer = string | string[];

  interface Survey {
    shortname: string;
    displayname: unknown;
    description: unknown;
    questions: SurveyQuestion[];
    owner_shortname: string;
    /** The current user's earlier answers, when they already responded. */
    myResponse: Record<string, Answer> | null;
  }

  let surveys = $state<Survey[]>([]);
  let isLoading = $state(true);
  let error = $state<unknown>(null);
  let responses = $state<Record<string, Record<string, Answer>>>({});
  let submitting = $state<Record<string, boolean>>({});
  let selectedSurvey = $state<Survey | null>(null);
  let showSurveyModal = $state(false);

  $effect(() => setTitle($_("surveys.title")));

  onMount(async () => {
    await loadSurveys();
  });

  function titleOf(survey: Survey): string {
    return localized(survey.displayname as never, $locale) || $_("surveys.untitled_survey");
  }

  function descriptionOf(survey: Survey): string {
    return localized(survey.description as never, $locale);
  }

  async function loadSurveys() {
    try {
      isLoading = true;
      error = null;

      // The listing already carries each survey's response attachments, so
      // whether I responded (and what I answered) needs no request per survey.
      const result = await getSurveys(APPLICATIONS_SPACE, DmartScope.managed, 100, 0, false);
      const me = $user?.shortname;

      surveys = (result?.records ?? []).map((record): Survey => {
        const attrs = (record.attributes ?? {}) as Record<string, unknown>;
        const body = ((attrs.payload as { body?: Record<string, unknown> } | undefined)?.body ?? {}) as Record<string, unknown>;
        const attachments = ((record as unknown as { attachments?: { json?: unknown[] } }).attachments?.json ?? []) as Array<{
          attributes?: { owner_shortname?: string; payload?: { body?: Record<string, Answer> } };
        }>;
        const mine = me ? attachments.find((a) => a.attributes?.owner_shortname === me) : undefined;
        return {
          shortname: record.shortname,
          displayname: attrs.displayname,
          description: attrs.description,
          questions: Array.isArray(body.questions) ? (body.questions as SurveyQuestion[]) : [],
          owner_shortname: typeof attrs.owner_shortname === "string" ? attrs.owner_shortname : "",
          myResponse: mine?.attributes?.payload?.body ?? null,
        };
      });
    } catch (err) {
      log.error("Error loading surveys:", err);
      error = err;
    } finally {
      isLoading = false;
    }
  }

  function answersFor(survey: Survey): Record<string, Answer> {
    if (!responses[survey.shortname]) {
      const initial: Record<string, Answer> = {};
      for (const q of survey.questions) initial[q.id] = q.type === "multi" ? [] : "";
      responses[survey.shortname] = { ...initial, ...(survey.myResponse ?? {}) };
    }
    return responses[survey.shortname];
  }

  function setAnswer(survey: Survey, questionId: string, value: Answer) {
    answersFor(survey)[questionId] = value;
  }

  function toggleMulti(survey: Survey, questionId: string, option: string, checked: boolean) {
    const current = answersFor(survey)[questionId];
    const list = Array.isArray(current) ? [...current] : [];
    const i = list.indexOf(option);
    if (checked && i < 0) list.push(option);
    if (!checked && i >= 0) list.splice(i, 1);
    setAnswer(survey, questionId, list);
  }

  function isEmpty(answer: Answer | undefined): boolean {
    return answer === undefined || (Array.isArray(answer) ? answer.length === 0 : answer.toString().trim() === "");
  }

  async function submitResponse(event: SubmitEvent) {
    event.preventDefault();
    const survey = selectedSurvey;
    if (!survey) return;
    const answers = answersFor(survey);

    for (const question of survey.questions) {
      if (question.required && isEmpty(answers[question.id])) {
        toasts.error(`${$_("surveys.answer_required")}: ${question.question}`);
        return;
      }
    }
    if (survey.questions.every((q) => isEmpty(answers[q.id]))) {
      toasts.error($_("surveys.answer_one_question"));
      return;
    }

    try {
      submitting[survey.shortname] = true;
      const result = await submitSurveyResponse(survey.shortname, answers);
      if (!result) throw new Error("Failed to submit response");
      toasts.success(survey.myResponse ? $_("surveys.response_updated") : $_("surveys.response_success"));
      survey.myResponse = { ...answers };
      closeSurveyModal();
    } catch (err) {
      log.error("Error submitting response:", err);
      toasts.error($_("surveys.response_error"));
    } finally {
      submitting[survey.shortname] = false;
    }
  }

  function openSurveyModal(survey: Survey) {
    selectedSurvey = survey;
    answersFor(survey);
    showSurveyModal = true;
  }

  function closeSurveyModal() {
    if (selectedSurvey && submitting[selectedSurvey.shortname]) return;
    showSurveyModal = false;
    selectedSurvey = null;
  }

  const inputClass =
    "w-full px-3 py-2 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary disabled:opacity-60";
</script>

<div class="mx-auto max-w-5xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("surveys.title")} description={$_("surveys.description")} icon={ClipboardListOutline}>
    {#snippet actions()}
      <button type="button" class="app-btn app-btn-secondary" onclick={() => goto("/surveys/manage")}>
        {$_("surveys.manage_button")}
      </button>
      <button type="button" class="app-btn app-btn-primary" onclick={() => goto("/surveys/create")}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("surveys.create_button")}
      </button>
    {/snippet}
  </PageHeader>

  {#if isLoading}
    <LoadingState label={$_("surveys.loading")} />
  {:else if error}
    <ErrorState title={$_("surveys.failed_to_load")} {error} onRetry={loadSurveys} />
  {:else if surveys.length === 0}
    <EmptyState icon={ClipboardListOutline} title={$_("surveys.no_surveys_available")} hint={$_("surveys.no_surveys_moment")}>
      <button type="button" class="app-btn app-btn-primary app-btn-sm" onclick={() => goto("/surveys/create")}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("surveys.create_first")}
      </button>
    </EmptyState>
  {:else}
    <ul class="space-y-3 list-none p-0 m-0">
      {#each surveys as survey (survey.shortname)}
        <li>
          <button
            type="button"
            class="w-full text-start flex items-center gap-4 p-4 sm:p-5 rounded-card border border-border bg-surface-2 shadow-card transition-[box-shadow,border-color] hover:shadow-modal hover:border-border-strong focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary cursor-pointer"
            onclick={() => openSurveyModal(survey)}
          >
            <span class="flex-1 min-w-0">
              <span class="block text-lg font-semibold text-text">{titleOf(survey)}</span>
              {#if descriptionOf(survey)}
                <span class="block text-sm text-text-muted mt-0.5 line-clamp-2">{descriptionOf(survey)}</span>
              {/if}
              <span class="flex flex-wrap items-center gap-x-4 gap-y-1 mt-2 text-sm text-text-faint">
                <span>{$_("surveys.author")}: <span class="text-text-muted">{survey.owner_shortname}</span></span>
                <span class="tabular-nums">{$_("surveys.question_count", { values: { count: survey.questions.length } })}</span>
                {#if survey.myResponse}
                  <Badge variant="success" size="sm">{$_("surveys.already_responded")}</Badge>
                {/if}
              </span>
            </span>
            <ChevronRightOutline size="md" class="shrink-0 text-text-faint rtl:rotate-180" aria-hidden="true" />
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</div>

{#if showSurveyModal && selectedSurvey}
  {@const survey = selectedSurvey}
  {@const answers = answersFor(survey)}
  {@const locked = !!survey.myResponse}
  {@const busy = !!submitting[survey.shortname]}
  <Modal title={titleOf(survey)} size="3xl" dismissable={!busy} onClose={closeSurveyModal}>
    {#snippet headerActions()}
      {#if locked}
        <Badge variant="success" size="sm">{$_("surveys.already_responded")}</Badge>
      {/if}
    {/snippet}

    {#if descriptionOf(survey)}
      <p class="text-sm text-text-muted mb-5">{descriptionOf(survey)}</p>
    {/if}

    <form id="survey-response-form" class="space-y-6" onsubmit={submitResponse}>
      {#each survey.questions as question, index (question.id)}
        {@const fieldId = `q-${survey.shortname}-${question.id}`}
        {#if question.type === "input" || question.type === "text" || question.type === "select"}
          <div>
            <label for={fieldId} class="block text-sm font-medium text-text mb-2">
              {index + 1}. {question.question}
              {#if question.required}<span class="text-danger" aria-hidden="true">*</span>{/if}
            </label>
            {#if question.type === "input"}
              <input
                id={fieldId}
                type="text"
                class={inputClass}
                placeholder={$_("surveys.type_your_answer")}
                value={typeof answers[question.id] === "string" ? answers[question.id] : ""}
                disabled={locked}
                required={question.required}
                oninput={(e) => setAnswer(survey, question.id, (e.currentTarget as HTMLInputElement).value)}
              />
            {:else if question.type === "text"}
              <textarea
                id={fieldId}
                class="{inputClass} resize-y min-h-24"
                placeholder={$_("surveys.type_your_answer")}
                rows="4"
                value={typeof answers[question.id] === "string" ? answers[question.id] : ""}
                disabled={locked}
                required={question.required}
                oninput={(e) => setAnswer(survey, question.id, (e.currentTarget as HTMLTextAreaElement).value)}
              ></textarea>
            {:else}
              <select
                id={fieldId}
                class={inputClass}
                value={typeof answers[question.id] === "string" ? answers[question.id] : ""}
                disabled={locked}
                required={question.required}
                onchange={(e) => setAnswer(survey, question.id, (e.currentTarget as HTMLSelectElement).value)}
              >
                <option value="">{$_("surveys.choose_an_option")}</option>
                {#each question.options ?? [] as option (option.id)}
                  <option value={option.label}>{option.label}</option>
                {/each}
              </select>
            {/if}
          </div>
        {:else}
          <fieldset class="border-0 p-0 m-0 min-w-0">
            <legend class="text-sm font-medium text-text mb-2">
              {index + 1}. {question.question}
              {#if question.required}<span class="text-danger" aria-hidden="true">*</span>{/if}
            </legend>
            <div class="flex flex-col gap-1">
              {#each question.options ?? [] as option (option.id)}
                {@const checked =
                  question.type === "multi"
                    ? Array.isArray(answers[question.id]) && (answers[question.id] as string[]).includes(option.label)
                    : answers[question.id] === option.label}
                <label class="flex items-center gap-2 p-2 rounded-control hover:bg-surface-3 cursor-pointer text-sm text-text">
                  <input
                    type={question.type === "multi" ? "checkbox" : "radio"}
                    name={fieldId}
                    value={option.label}
                    {checked}
                    disabled={locked}
                    class="accent-primary"
                    onchange={(e) =>
                      question.type === "multi"
                        ? toggleMulti(survey, question.id, option.label, (e.currentTarget as HTMLInputElement).checked)
                        : setAnswer(survey, question.id, option.label)}
                  />
                  <span>{option.label}</span>
                </label>
              {/each}
            </div>
          </fieldset>
        {/if}
      {/each}
    </form>

    {#snippet footer()}
      {#if locked}
        <span class="me-auto text-sm font-medium text-success">{$_("surveys.response_submitted")}</span>
        <button type="button" class="app-btn app-btn-secondary" onclick={closeSurveyModal}>
          {$_("common.close")}
        </button>
      {:else}
        <button type="button" class="app-btn app-btn-secondary" onclick={closeSurveyModal} disabled={busy}>
          {$_("common.cancel")}
        </button>
        <button type="submit" form="survey-response-form" class="app-btn app-btn-primary" disabled={busy} aria-busy={busy}>
          {#if busy}
            <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
            {$_("surveys.submitting")}
          {:else}
            {$_("surveys.submit_response")}
          {/if}
        </button>
      {/if}
    {/snippet}
  </Modal>
{/if}
