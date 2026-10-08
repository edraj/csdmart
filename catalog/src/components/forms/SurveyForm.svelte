<script module lang="ts">
  export type SurveyQuestionType = "input" | "text" | "single" | "multi" | "select";

  export interface SurveyDraftOption {
    id: string;
    text?: string;
    label: string;
    value: string;
  }

  export interface SurveyDraftQuestion {
    id: string;
    question: string;
    type: SurveyQuestionType;
    options: SurveyDraftOption[];
    required: boolean;
  }

  export interface SurveyDraft {
    title: string;
    description: string;
    questions: SurveyDraftQuestion[];
  }
</script>

<script lang="ts">
  import { _ } from "@/i18n";
  import { CloseOutline, FileLinesOutline, ListOutline, PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";
  import Card from "@/components/ui/Card.svelte";
  import IconButton from "@/components/ui/IconButton.svelte";

  let {
    survey = $bindable({ title: "", description: "", questions: [] }),
  }: {
    survey: SurveyDraft;
  } = $props();

  const uid = $props.id();

  const answerTypes = $derived<Array<{ value: SurveyQuestionType; name: string }>>([
    { value: "input", name: $_("survey_form.answer_type_input") },
    { value: "text", name: $_("survey_form.answer_type_text") },
    { value: "single", name: $_("survey_form.answer_type_single") },
    { value: "multi", name: $_("survey_form.answer_type_multi") },
    { value: "select", name: $_("survey_form.answer_type_select") },
  ]);

  const inputClass =
    "w-full px-3 py-2 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary";

  function addQuestion() {
    survey.questions.push({
      id: crypto.randomUUID(),
      question: "",
      type: "input",
      options: [],
      required: false,
    });
  }

  function removeQuestion(index: number) {
    survey.questions.splice(index, 1);
  }

  function addOption(questionIndex: number) {
    survey.questions[questionIndex].options.push({
      id: crypto.randomUUID(),
      text: "",
      label: "",
      value: "",
    });
  }

  function removeOption(questionIndex: number, optionIndex: number) {
    survey.questions[questionIndex].options.splice(optionIndex, 1);
  }

  function generateValue(text: string): string {
    return text
      .toLowerCase()
      .replace(/[^؀-ۿݐ-ݿࢠ-ࣿA-Za-z0-9\s]/g, "")
      .replace(/\s+/g, "_")
      .substring(0, 50);
  }

  function updateOptionText(questionIndex: number, optionIndex: number, text: string) {
    const option = survey.questions[questionIndex].options[optionIndex];
    option.text = text;
    option.label = text;
    option.value = generateValue(text);
  }

  function hasOptions(type: SurveyQuestionType): boolean {
    return type === "single" || type === "multi" || type === "select";
  }
</script>

<div class="space-y-6">
  <Card padding="none">
    {#snippet header()}
      <div class="flex items-center gap-3">
        <span class="w-9 h-9 rounded-control bg-primary-soft text-primary flex items-center justify-center" aria-hidden="true">
          <FileLinesOutline size="md" />
        </span>
        <h2 class="text-lg font-semibold text-text">{$_("survey_form.survey_details")}</h2>
      </div>
    {/snippet}

    <div class="p-4 sm:p-5 space-y-4">
      <div>
        <label for="{uid}-title" class="block text-sm font-medium text-text mb-1.5">{$_("survey_form.survey_title")}</label>
        <input
          id="{uid}-title"
          type="text"
          class={inputClass}
          placeholder={$_("survey_form.title_placeholder")}
          bind:value={survey.title}
          required
        />
      </div>

      <div>
        <label for="{uid}-description" class="block text-sm font-medium text-text mb-1.5">
          {$_("survey_form.survey_description")}
        </label>
        <textarea
          id="{uid}-description"
          class="{inputClass} resize-y min-h-20"
          placeholder={$_("survey_form.description_placeholder")}
          bind:value={survey.description}
          rows="3"
        ></textarea>
      </div>
    </div>
  </Card>

  <Card padding="none">
    {#snippet header()}
      <div class="flex flex-wrap items-center justify-between gap-3">
        <div class="flex items-center gap-3">
          <span class="w-9 h-9 rounded-control bg-primary-soft text-primary flex items-center justify-center" aria-hidden="true">
            <ListOutline size="md" />
          </span>
          <div>
            <h2 class="text-lg font-semibold text-text">{$_("survey_form.questions")}</h2>
            <p class="text-xs text-text-muted tabular-nums">
              {$_("surveys.question_count", { values: { count: survey.questions.length } })}
            </p>
          </div>
        </div>
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={addQuestion}>
          <PlusOutline size="sm" aria-hidden="true" />
          {$_("survey_form.add_question_button")}
        </button>
      </div>
    {/snippet}

    <div class="p-4 sm:p-5">
      {#if survey.questions.length > 0}
        <ol class="space-y-4 list-none p-0 m-0">
          {#each survey.questions as question, questionIndex (question.id)}
            {@const qid = `${uid}-q-${question.id}`}
            <li class="rounded-card border border-border bg-surface p-4 space-y-3">
              <div class="flex items-center gap-3">
                <span class="w-6 text-sm font-medium text-text-muted text-center tabular-nums" aria-hidden="true">
                  {questionIndex + 1}
                </span>
                <label for="{qid}-text" class="sr-only">
                  {$_("surveys.question_number", { values: { number: questionIndex + 1 } })}
                </label>
                <input
                  id="{qid}-text"
                  type="text"
                  class={inputClass}
                  placeholder={$_("survey_form.question_placeholder")}
                  bind:value={question.question}
                />
                <IconButton label={$_("survey_form.delete_question")} variant="danger" size="sm" onclick={() => removeQuestion(questionIndex)}>
                  <TrashBinOutline size="sm" />
                </IconButton>
              </div>

              <div class="flex flex-col sm:flex-row sm:items-center gap-3 sm:ps-9">
                <div class="sm:w-56">
                  <label for="{qid}-type" class="sr-only">{$_("survey_form.answer_type")}</label>
                  <select
                    id="{qid}-type"
                    class={inputClass}
                    bind:value={question.type}
                  >
                    {#each answerTypes as type (type.value)}
                      <option value={type.value}>{type.name}</option>
                    {/each}
                  </select>
                </div>

                <label class="inline-flex items-center gap-2 text-sm text-text-muted cursor-pointer sm:ms-auto">
                  <input type="checkbox" class="accent-primary" bind:checked={question.required} />
                  {$_("survey_form.required")}
                </label>
              </div>

              {#if hasOptions(question.type)}
                <div class="sm:ps-9 space-y-2">
                  {#each question.options as option, optionIndex (option.id)}
                    <div class="flex items-center gap-2">
                      <span class="w-4 h-4 rounded-full border-2 border-border-strong shrink-0" aria-hidden="true"></span>
                      <input
                        type="text"
                        class="{inputClass} max-w-md"
                        placeholder={$_("survey_form.option_placeholder")}
                        aria-label={$_("polls.form.option_n", { values: { n: optionIndex + 1 } })}
                        value={option.text || option.label || ""}
                        oninput={(e) => updateOptionText(questionIndex, optionIndex, (e.currentTarget as HTMLInputElement).value)}
                      />
                      <IconButton label={$_("survey_form.delete_option")} size="sm" onclick={() => removeOption(questionIndex, optionIndex)}>
                        <CloseOutline size="sm" />
                      </IconButton>
                    </div>
                  {/each}
                  <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={() => addOption(questionIndex)}>
                    <PlusOutline size="xs" aria-hidden="true" />
                    {$_("survey_form.add_option")}
                  </button>
                </div>
              {/if}
            </li>
          {/each}
        </ol>
      {:else}
        <p class="text-sm text-text-muted italic text-center py-6">{$_("survey_form.no_questions")}</p>
      {/if}
    </div>
  </Card>
</div>
