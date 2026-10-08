<script lang="ts">
  import { onMount } from "svelte";
  import { goto as gotoStore } from "@roxi/routify";
  import { getUserSurveys, getSurveys } from "@/lib/dmart_services";
  import { APPLICATIONS_SPACE } from "@/lib/constants";
  import { DmartScope } from "@edraj/tsdmart";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { localized } from "@/lib/catalogItems";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { user } from "@/stores/user";
  import {
    ChevronRightOutline,
    ClipboardListOutline,
    DownloadOutline,
    InboxOutline,
    PlusOutline,
    UsersOutline,
  } from "flowbite-svelte-icons";
  import Modal from "@/components/Modal.svelte";
  import Avatar from "@/components/Avatar.svelte";
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

  type Answer = string | string[];

  interface Question {
    id: string;
    question: string;
    type: string;
    required?: boolean;
  }

  interface ResponseRecord {
    shortname: string;
    respondent: string;
    submittedAt: string;
    answers: Record<string, Answer>;
  }

  interface MySurvey {
    kind: "mine";
    shortname: string;
    displayname: unknown;
    description: unknown;
    questions: Question[];
    created_at: string;
    responses: ResponseRecord[];
  }

  interface AnsweredSurvey {
    kind: "answered";
    shortname: string;
    displayname: unknown;
    description: unknown;
    questions: Question[];
    owner_shortname: string;
    created_at: string;
    myAnswers: Record<string, Answer>;
    submittedAt: string;
  }

  type Tab = "my-surveys" | "responded";

  let mySurveys = $state<MySurvey[]>([]);
  let respondedSurveys = $state<AnsweredSurvey[]>([]);
  let activeTab = $state<Tab>("my-surveys");
  let isLoading = $state(true);
  let error = $state<unknown>(null);
  let selected = $state<MySurvey | AnsweredSurvey | null>(null);
  let showAllResponses = $state(false);

  $effect(() => setTitle($_("survey_manage.title")));

  const tabs: Array<{ id: Tab; label: () => string }> = [
    { id: "my-surveys", label: () => $_("survey_manage.your_surveys") },
    { id: "responded", label: () => $_("survey_manage.surveys_replied_on") },
  ];

  onMount(async () => {
    await loadSurveys();
  });

  function titleOf(s: { displayname: unknown }): string {
    return localized(s.displayname as never, $locale) || $_("surveys.untitled_survey");
  }

  function descriptionOf(s: { description: unknown }): string {
    return localized(s.description as never, $locale);
  }

  type RawRecord = {
    shortname: string;
    attributes?: Record<string, unknown>;
    attachments?: { json?: unknown[] };
  };

  function questionsOf(record: RawRecord): Question[] {
    const body = (record.attributes?.payload as { body?: { questions?: unknown } } | undefined)?.body;
    return Array.isArray(body?.questions) ? (body.questions as Question[]) : [];
  }

  function responsesOf(record: RawRecord): ResponseRecord[] {
    const list = (record.attachments?.json ?? []) as Array<{
      shortname?: string;
      attributes?: { owner_shortname?: string; created_at?: string; payload?: { body?: Record<string, Answer> } };
    }>;
    return list.map((a, i) => ({
      shortname: a.shortname ?? String(i),
      respondent: a.attributes?.owner_shortname ?? "",
      submittedAt: a.attributes?.created_at ?? "",
      answers: a.attributes?.payload?.body ?? {},
    }));
  }

  async function loadSurveys() {
    try {
      isLoading = true;
      error = null;
      const me = $user?.shortname ?? "";

      // Both listings already carry the response attachments, so neither
      // needs a follow-up request per survey.
      const [mine, all] = await Promise.all([
        getUserSurveys(),
        getSurveys(APPLICATIONS_SPACE, DmartScope.managed, 100, 0, false),
      ]);

      mySurveys = (mine as RawRecord[]).map((record) => ({
        kind: "mine",
        shortname: record.shortname,
        displayname: record.attributes?.displayname,
        description: record.attributes?.description,
        questions: questionsOf(record),
        created_at: String(record.attributes?.created_at ?? ""),
        responses: responsesOf(record),
      }));

      respondedSurveys = [];
      for (const record of (all?.records ?? []) as RawRecord[]) {
        const owner = String(record.attributes?.owner_shortname ?? "");
        if (owner === me) continue;
        const mineResponse = responsesOf(record).find((r) => r.respondent === me);
        if (!mineResponse) continue;
        respondedSurveys.push({
          kind: "answered",
          shortname: record.shortname,
          displayname: record.attributes?.displayname,
          description: record.attributes?.description,
          questions: questionsOf(record),
          owner_shortname: owner,
          created_at: String(record.attributes?.created_at ?? ""),
          myAnswers: mineResponse.answers,
          submittedAt: mineResponse.submittedAt,
        });
      }
    } catch (err) {
      log.error("Error loading surveys:", err);
      error = err;
    } finally {
      isLoading = false;
    }
  }

  function open(survey: MySurvey | AnsweredSurvey) {
    selected = survey;
    showAllResponses = false;
  }

  function close() {
    selected = null;
  }

  function exportSurveyData(survey: MySurvey) {
    const exportData = {
      survey: {
        title: titleOf(survey),
        description: descriptionOf(survey),
        questions: survey.questions,
        createdAt: survey.created_at,
      },
      analytics: {
        totalResponses: survey.responses.length,
        responses: survey.responses,
      },
    };

    const blob = new Blob([JSON.stringify(exportData, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `survey-${survey.shortname}-data.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  function answerText(answer: Answer | undefined): string[] {
    if (answer === undefined || answer === null) return [];
    return Array.isArray(answer) ? answer.map(String) : [String(answer)];
  }
</script>

<div class="mx-auto max-w-5xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader
    title={$_("survey_manage.title")}
    description={$_("survey_manage.description")}
    icon={ClipboardListOutline}
    backHref="/surveys"
    backLabel={$_("survey_manage.back_to_surveys")}
  >
    {#snippet actions()}
      <button type="button" class="app-btn app-btn-primary" onclick={() => goto("/surveys/create")}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("surveys.create_button")}
      </button>
    {/snippet}
  </PageHeader>

  <div class="flex border-b border-border mb-6" role="tablist" aria-label={$_("survey_manage.title")}>
    {#each tabs as tab (tab.id)}
      <button
        type="button"
        role="tab"
        aria-selected={activeTab === tab.id}
        class="px-4 py-3 text-sm font-medium border-b-2 -mb-px transition-colors cursor-pointer
 {activeTab === tab.id ? 'border-primary text-primary' : 'border-transparent text-text-muted hover:text-text'}"
        onclick={() => (activeTab = tab.id)}
      >
        {tab.label()}
        {#if !isLoading}
          <span class="ms-1 text-xs text-text-faint tabular-nums">
            ({tab.id === "my-surveys" ? mySurveys.length : respondedSurveys.length})
          </span>
        {/if}
      </button>
    {/each}
  </div>

  {#if isLoading}
    <LoadingState label={$_("survey_manage.loading")} />
  {:else if error}
    <ErrorState title={$_("surveys.failed_to_load")} {error} onRetry={loadSurveys} />
  {:else if activeTab === "my-surveys"}
    {#if mySurveys.length === 0}
      <EmptyState icon={ClipboardListOutline} title={$_("survey_manage.no_surveys")} hint={$_("survey_manage.no_surveys_description")}>
        <button type="button" class="app-btn app-btn-primary app-btn-sm" onclick={() => goto("/surveys/create")}>
          <PlusOutline size="sm" aria-hidden="true" />
          {$_("survey_manage.create_first")}
        </button>
      </EmptyState>
    {:else}
      <ul class="space-y-3 list-none p-0 m-0">
        {#each mySurveys as survey (survey.shortname)}
          <li>
            <button type="button" class="survey-row" onclick={() => open(survey)}>
              <span class="flex-1 min-w-0">
                <span class="block text-lg font-semibold text-text">{titleOf(survey)}</span>
                {#if descriptionOf(survey)}
                  <span class="block text-sm text-text-muted mt-0.5 line-clamp-2">{descriptionOf(survey)}</span>
                {/if}
                <span class="flex flex-wrap items-center gap-x-4 gap-y-1 mt-2 text-sm text-text-faint tabular-nums">
                  <span>{$_("survey_manage.created")}: {formatDate(survey.created_at, "date", $locale)}</span>
                  <span>{$_("surveys.question_count", { values: { count: survey.questions.length } })}</span>
                  <Badge variant={survey.responses.length > 0 ? "primary" : "neutral"} size="sm">
                    {$_("survey_manage.response_count", { values: { count: survey.responses.length } })}
                  </Badge>
                </span>
              </span>
              <ChevronRightOutline size="md" class="shrink-0 text-text-faint rtl:rotate-180" aria-hidden="true" />
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  {:else if respondedSurveys.length === 0}
    <EmptyState icon={InboxOutline} title={$_("survey_manage.no_responses")} hint={$_("survey_manage.no_responses_description")} />
  {:else}
    <ul class="space-y-3 list-none p-0 m-0">
      {#each respondedSurveys as survey (survey.shortname)}
        <li>
          <button type="button" class="survey-row" onclick={() => open(survey)}>
            <span class="flex-1 min-w-0">
              <span class="block text-lg font-semibold text-text">{titleOf(survey)}</span>
              {#if descriptionOf(survey)}
                <span class="block text-sm text-text-muted mt-0.5 line-clamp-2">{descriptionOf(survey)}</span>
              {/if}
              <span class="flex flex-wrap items-center gap-x-4 gap-y-1 mt-2 text-sm text-text-faint tabular-nums">
                <span>{$_("surveys.author")}: <span class="text-text-muted">{survey.owner_shortname}</span></span>
                <span>{$_("survey_manage.responded_on")}: {formatDate(survey.submittedAt, "date", $locale)}</span>
                <span>{$_("surveys.question_count", { values: { count: survey.questions.length } })}</span>
              </span>
            </span>
            <ChevronRightOutline size="md" class="shrink-0 text-text-faint rtl:rotate-180" aria-hidden="true" />
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</div>

{#if selected}
  {@const survey = selected}
  <Modal title={titleOf(survey)} size="3xl" onClose={close}>
    {#if survey.kind === "mine"}
      {@const shown = showAllResponses ? survey.responses : survey.responses.slice(0, 5)}
      {#if descriptionOf(survey)}
        <p class="text-sm text-text-muted mb-4">{descriptionOf(survey)}</p>
      {/if}

      <div class="flex flex-wrap items-center justify-between gap-3 mb-5">
        <div class="flex items-center gap-3 rounded-card border border-border bg-surface px-4 py-3">
          <span class="text-primary" aria-hidden="true"><UsersOutline size="lg" /></span>
          <div>
            <div class="text-2xl font-semibold text-text tabular-nums">{survey.responses.length}</div>
            <div class="text-xs text-text-muted">{$_("survey_manage.total_responses")}</div>
          </div>
        </div>
        {#if survey.responses.length > 0}
          <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={() => exportSurveyData(survey)}>
            <DownloadOutline size="sm" aria-hidden="true" />
            {$_("survey_manage.export")}
          </button>
        {/if}
      </div>

      <section class="mb-6">
        <h4 class="text-sm font-semibold text-text mb-2">{$_("survey_manage.survey_questions")}</h4>
        <ol class="space-y-1 list-none p-0 m-0">
          {#each survey.questions as question, index (question.id)}
            <li class="text-sm text-text flex gap-2">
              <span class="text-text-faint tabular-nums">{index + 1}.</span>
              <span>
                {question.question}
                <span class="text-text-faint">({question.type})</span>
                {#if question.required}<span class="text-danger" aria-hidden="true">*</span>{/if}
              </span>
            </li>
          {/each}
        </ol>
      </section>

      {#if survey.responses.length > 0}
        <section>
          <div class="flex items-center justify-between mb-3">
            <h4 class="text-sm font-semibold text-text">{$_("survey_manage.recent_responses")}</h4>
            <span class="text-xs text-text-muted tabular-nums">
              {$_("survey_manage.response_count", { values: { count: survey.responses.length } })}
            </span>
          </div>
          <ul class="space-y-3 list-none p-0 m-0">
            {#each shown as response, idx (response.shortname)}
              <li class="rounded-card border border-border bg-surface p-4">
                <div class="flex flex-wrap items-center justify-between gap-2 mb-3">
                  <div class="flex items-center gap-2 min-w-0">
                    <Avatar alt={response.respondent} size="32" />
                    <div class="min-w-0">
                      <div class="text-sm font-medium text-text truncate">{response.respondent}</div>
                      <div class="text-xs text-text-faint tabular-nums">
                        {$_("survey_manage.response_number", { values: { number: idx + 1 } })}
                      </div>
                    </div>
                  </div>
                  <span class="text-xs text-text-muted tabular-nums">
                    {formatDate(response.submittedAt, "datetime", $locale)}
                  </span>
                </div>
                <dl class="space-y-2">
                  {#each survey.questions as question (question.id)}
                    {@const values = answerText(response.answers[question.id])}
                    <div>
                      <dt class="text-xs text-text-muted">{question.question}</dt>
                      <dd class="text-sm text-text mt-0.5">
                        {#if values.length === 0}
                          <span class="text-text-faint italic">{$_("surveys.not_answered")}</span>
                        {:else if values.length > 1}
                          <span class="flex flex-wrap gap-1">
                            {#each values as item, i (i)}
                              <Badge size="sm">{item}</Badge>
                            {/each}
                          </span>
                        {:else}
                          {values[0]}
                        {/if}
                      </dd>
                    </div>
                  {/each}
                </dl>
              </li>
            {/each}
          </ul>
          {#if survey.responses.length > 5 && !showAllResponses}
            <div class="text-center mt-3">
              <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={() => (showAllResponses = true)}>
                {$_("survey_manage.view_all")} ({$_("survey_manage.more_count", { values: { count: survey.responses.length - 5 } })})
              </button>
            </div>
          {/if}
        </section>
      {:else}
        <EmptyState icon={InboxOutline} title={$_("survey_manage.no_responses_yet")} hint={$_("survey_manage.no_responses_message")} />
      {/if}
    {:else}
      <div class="text-sm text-text-muted space-y-1 mb-5">
        <p><strong class="text-text">{$_("survey_manage.survey_by")}:</strong> {survey.owner_shortname}</p>
        <p class="tabular-nums">
          <strong class="text-text">{$_("survey_manage.submitted_on")}:</strong>
          {formatDate(survey.submittedAt, "datetime", $locale)}
        </p>
      </div>

      <ol class="space-y-4 list-none p-0 m-0">
        {#each survey.questions as question, index (question.id)}
          {@const values = answerText(survey.myAnswers[question.id])}
          <li class="rounded-card border border-border bg-surface p-4">
            <div class="flex gap-2 mb-2">
              <span class="text-text-faint tabular-nums">{index + 1}.</span>
              <h4 class="text-sm font-medium text-text">{question.question}</h4>
            </div>
            <div class="text-sm text-text ps-5">
              <span class="text-text-muted">{$_("survey_manage.your_answer")}:</span>
              {#if values.length === 0}
                <span class="text-text-faint italic">{$_("surveys.not_answered")}</span>
              {:else if values.length > 1}
                <span class="inline-flex flex-wrap gap-1 align-middle">
                  {#each values as item, i (i)}
                    <Badge size="sm">{item}</Badge>
                  {/each}
                </span>
              {:else}
                {values[0]}
              {/if}
            </div>
          </li>
        {/each}
      </ol>
    {/if}

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={close}>
        {$_("common.close")}
      </button>
    {/snippet}
  </Modal>
{/if}

<style>
  .survey-row {
    width: 100%;
    display: flex;
    align-items: center;
    gap: 1rem;
    padding: 1rem 1.25rem;
    text-align: start;
    border-radius: var(--radius-card);
    border: 1px solid var(--color-border);
    background: var(--color-surface-2);
    box-shadow: var(--shadow-card);
    cursor: pointer;
    transition: box-shadow var(--duration-normal) var(--ease-out), border-color var(--duration-normal) var(--ease-out);
  }

  .survey-row:hover {
    box-shadow: var(--shadow-modal);
    border-color: var(--color-border-strong);
  }
</style>
