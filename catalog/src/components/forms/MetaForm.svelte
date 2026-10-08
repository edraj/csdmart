<script module lang="ts">
  /**
   * A translation as the form edits it: a cleared language reads as null, so
   * the server drops it instead of storing an empty string.
   */
  export type FormTranslation = Record<string, string | null | undefined>;

  /**
   * The meta fields the form edits; anything else on the entry rides along.
   * Callers start from `{}`: the form fills in the translations on mount.
   */
  export interface MetaFormData {
    shortname?: string | null;
    is_active?: boolean;
    slug?: string | null;
    displayname?: FormTranslation | null;
    description?: FormTranslation | null;
    [key: string]: unknown;
  }
</script>

<script lang="ts">
  import { goto as gotoStore, params } from "@roxi/routify";
  import { Dmart, RequestType, ResourceType } from "@edraj/tsdmart";
  import { _ } from "svelte-i18n";
  import { isJsonObject } from "@/lib/types";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  let {
    isCreate,
    fullWidth = false,
    formData = $bindable({}),
    // eslint-disable-next-line @typescript-eslint/no-unused-vars, no-useless-assignment -- $bindable() prop: assigned here, read by the parent through bind:validateFn
    validateFn = $bindable(),
  }: {
    isCreate: boolean;
    fullWidth?: boolean;
    formData: MetaFormData;
    validateFn?: (() => boolean) | null;
  } = $props();

  formData = {
    ...formData,
    shortname: formData.shortname || null,
    is_active: formData.is_active || true,
    slug: formData.slug || null,
    displayname: {
      en: formData.displayname?.en || null,
      ar: formData.displayname?.ar || null,
      ku: formData.displayname?.ku || null,
    },
    description: {
      en: formData.description?.en || null,
      ar: formData.description?.ar || null,
      ku: formData.description?.ku || null,
    },
  };

  let form: HTMLFormElement | undefined;
  $effect(() => {
    validateFn = validate;
  });

  /**
   * Writes one language of a translation. The inputs below used to bind
   * straight into `formData.displayname.en`; the translations are optional on
   * the way in (callers start from `{}`), so the write goes through here.
   */
  function setTranslation(field: "displayname" | "description", lang: string, value: string) {
    const current = formData[field] ?? {};
    current[lang] = value;
    formData[field] = current;
  }

  function validate(): boolean {
    // Check if there's a shortname validation error
    if (shortnameError) {
      return false;
    }

    // Also validate the shortname value directly
    if (formData.shortname && formData.shortname !== "auto") {
      if (!validateShortnameInput(formData.shortname)) {
        shortnameError = $_("validation.shortname_invalid");
        return false;
      }
    }

    if (!form) return true;
    const isValid = form.checkValidity();
    if (!isValid) {
      form.reportValidity();
    }
    return isValid;
  }

  let isShortnameUpdateOpen = $state(false);
  let newShortname = $state("");
  let isUpdatingShortname = $state(false);
  let shortnameUpdateError = $state<string | null>(null);
  let isTranslationsOpen = $state(false);

  // Shortname validation pattern
  const shortnamePattern = "^[\\u064b-\\u065fa-zA-Z\\u0621-\\u064a0-9\\u0660-\\u0669_]{1,64}$";
  let shortnameError = $state("");

  function validateShortnameInput(value: string): boolean {
    if (!value || value === "auto") return true;
    const pattern = new RegExp(shortnamePattern);
    return pattern.test(value);
  }

  function handleShortnameInput(event: Event) {
    const value = (event.target as HTMLInputElement).value;
    if (!validateShortnameInput(value)) {
      shortnameError = $_("validation.shortname_invalid");
    } else {
      shortnameError = "";
    }
  }

  function handleShortnameModalUpdate() {
    newShortname = formData.shortname ?? "";
    shortnameUpdateError = null;
    isShortnameUpdateOpen = true;
  }

  /**
   * The server's reason for a failed move: the first row's own error
   * (`error.info[0].failed[0].error`), then the error message.
   */
  function moveFailureMessage(error: unknown): string | undefined {
    if (!isJsonObject(error) || !isJsonObject(error.response) || !isJsonObject(error.response.data)) return undefined;
    const serverError = error.response.data.error;
    if (!isJsonObject(serverError)) return undefined;
    const info = Array.isArray(serverError.info) ? serverError.info[0] : undefined;
    const failed = isJsonObject(info) && Array.isArray(info.failed) ? info.failed[0] : undefined;
    const rowError = isJsonObject(failed) && typeof failed.error === "string" ? failed.error : undefined;
    return rowError || (typeof serverError.message === "string" ? serverError.message : undefined);
  }

  async function updateShortname() {
    const currentShortname = formData.shortname ?? "";
    if (!newShortname || newShortname === currentShortname) return;
    if (!newShortname.match(/^[a-zA-Z0-9_]+$/)) {
      shortnameUpdateError = $_("validation.shortname_format");
      return;
    }

    isUpdatingShortname = true;

    try {
      const resourceType =
        $params.resource_type ||
        ($params.subpath && ResourceType.folder) ||
        ResourceType.space;
      const newSubpath =
        resourceType === ResourceType.folder
          ? $params.subpath.split("/").slice(0, -1).join("-") || "/"
          : $params.subpath;

      const moveAttrb = {
        src_space_name: $params.space_name,
        src_subpath: newSubpath,
        src_shortname: currentShortname,
        dest_space_name: $params.space_name,
        dest_subpath: newSubpath,
        dest_shortname: newShortname,
      };

      await Dmart.request({
        space_name: $params.space_name,
        request_type: RequestType.move,
        records: [
          {
            resource_type: resourceType,
            shortname: currentShortname,
            subpath: newSubpath,
            attributes: moveAttrb,
          },
        ],
      });

      let url = "/management/content";
      let gotoPayload: Record<string, string> = {
        space_name: $params.space_name,
      };
      if (resourceType === ResourceType.space) {
        url += "/[space_name]";
      } else {
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
      }
      goto(`${url}`, gotoPayload);
    } catch (error) {
      shortnameUpdateError = moveFailureMessage(error) || $_("errors.shortname_update_failed");
    } finally {
      isUpdatingShortname = false;
    }
  }
</script>

<div class="form-card" class:form-card-full={fullWidth}>
  <form bind:this={form} class="form-container">
    <!-- Shortname Field -->
    <div class="field-group">
      <label for="shortname" class="field-label">
        {#if isCreate}<span class="required">*</span>{/if}
        {$_("fields.shortname")}
      </label>
      <div class="input-with-button">
        <label for="shortname"></label>
        <input
          required
          id="shortname"
          class="input-field input-left"
          class:input-error={shortnameError}
          pattern={shortnamePattern}
          placeholder={$_("placeholders.shortname")}
          bind:value={formData.shortname}
          disabled={!isCreate}
          oninput={handleShortnameInput}
        />
        <button
          type="button"
          class="button-secondary button-right"
          onclick={() =>
            isCreate
              ? (formData.shortname = "auto")
              : handleShortnameModalUpdate()}
        >
          {isCreate ? $_("buttons.auto") : $_("buttons.update")}
        </button>
      </div>
      {#if shortnameError}
        <p class="field-error">
          <svg class="error-icon" fill="currentColor" viewBox="0 0 20 20">
            <path fill-rule="evenodd" d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7 4a1 1 0 11-2 0 1 1 0 012 0zm-1-9a1 1 0 00-1 1v4a1 1 0 102 0V6a1 1 0 00-1-1z" clip-rule="evenodd"></path>
          </svg>
          {shortnameError}
        </p>
      {:else if isCreate}
        <p class="field-help">
          {$_("help.shortname")}
        </p>
      {/if}
    </div>

    <!-- Active Checkbox -->
    <div class="field-group">
      <div class="checkbox-group">
        <label for="is_active"></label>
        <input
          type="checkbox"
          id="is_active"
          class="checkbox"
          bind:checked={formData.is_active}
        />
        <label for="is_active" class="checkbox-label"
          >{$_("fields.active")}</label
        >
      </div>
      <p class="field-help">{$_("help.active")}</p>
    </div>

    <!-- Slug Field -->
    <div class="field-group">
      <label for="slug" class="field-label">{$_("fields.slug")}</label>
      <input
        id="slug"
        class="input-field"
        placeholder={$_("placeholders.slug")}
        bind:value={formData.slug}
      />
      <p class="field-help">{$_("help.slug")}</p>
    </div>

    <div class="accordion">
      <button
        aria-label={$_("labels.toggle_translations")}
        type="button"
        class="accordion-header"
        onclick={() => (isTranslationsOpen = !isTranslationsOpen)}
      >
        <span class="accordion-title">{$_("sections.translations")}</span>
        <svg
          class="accordion-icon {isTranslationsOpen ? 'rotated' : ''}"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            stroke-linecap="round"
            stroke-linejoin="round"
            stroke-width="2"
            d="M19 9l-7 7-7-7"
          ></path>
        </svg>
      </button>

      {#if isTranslationsOpen}
        <div class="accordion-content">
          <div class="field-group">
            <label for="displayname-en" class="field-label"
              >{$_("fields.displayname")}</label
            >
            <div class="translation-grid">
              <div>
                <label for="displayname-en" class="translation-label"
                  >{$_("languages.english")}</label
                >
                <input
                  id="displayname-en"
                  class="input-field"
                  value={formData.displayname?.en ?? ""}
                  oninput={(e) => setTranslation("displayname", "en", e.currentTarget.value)}
                />
              </div>
              <div>
                <label for="displayname-ar" class="translation-label"
                  >{$_("languages.arabic")}</label
                >
                <input
                  id="displayname-ar"
                  class="input-field"
                  value={formData.displayname?.ar ?? ""}
                  oninput={(e) => setTranslation("displayname", "ar", e.currentTarget.value)}
                />
              </div>
              <div>
                <label for="displayname-ku" class="translation-label"
                  >{$_("languages.kurdish")}</label
                >
                <input
                  id="displayname-ku"
                  class="input-field"
                  value={formData.displayname?.ku ?? ""}
                  oninput={(e) => setTranslation("displayname", "ku", e.currentTarget.value)}
                />
              </div>
            </div>
          </div>

          <!-- Descriptions -->
          <div class="field-group">
            <label for="description-en" class="field-label"
              >{$_("fields.description")}</label
            >
            <div class="translation-grid">
              <div>
                <label for="description-en" class="translation-label"
                  >{$_("languages.english")}</label
                >
                <textarea
                  id="description-en"
                  class="textarea-field"
                  value={formData.description?.en ?? ""}
                  oninput={(e) => setTranslation("description", "en", e.currentTarget.value)}
                  rows="3"
                ></textarea>
              </div>
              <div>
                <label for="description-ar" class="translation-label"
                  >{$_("languages.arabic")}</label
                >
                <textarea
                  id="description-ar"
                  class="textarea-field"
                  value={formData.description?.ar ?? ""}
                  oninput={(e) => setTranslation("description", "ar", e.currentTarget.value)}
                  rows="3"
                ></textarea>
              </div>
              <div>
                <label for="description-ku" class="translation-label"
                  >{$_("languages.kurdish")}</label
                >
                <textarea
                  id="description-ku"
                  class="textarea-field"
                  value={formData.description?.ku ?? ""}
                  oninput={(e) => setTranslation("description", "ku", e.currentTarget.value)}
                  rows="3"
                ></textarea>
              </div>
            </div>
          </div>
        </div>
      {/if}
    </div>
  </form>
</div>

<!-- Modal -->
{#if isShortnameUpdateOpen}
  <div
    class="modal-overlay"
    role="presentation"
    onclick={(e) => {
      if (e.target === e.currentTarget) isShortnameUpdateOpen = false;
    }}
    onkeydown={(e) => {
      if (e.key === "Escape") isShortnameUpdateOpen = false;
    }}
  >
    <div class="modal-content" role="dialog" aria-modal="true" tabindex="-1">
      <div class="modal-header">
        <h3 class="modal-title">{$_("modal.update_shortname.title")}</h3>
        <button
          class="modal-close"
          aria-label={$_("common.close")}
          onclick={() => (isShortnameUpdateOpen = false)}
        >
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M6 18L18 6M6 6l12 12"
            ></path>
          </svg>
        </button>
      </div>

      <div class="modal-body">
        <p class="modal-warning">
          {$_("modal.update_shortname.warning")}
        </p>

        {#if shortnameUpdateError}
          <div class="alert alert-error">
            <svg class="alert-icon" fill="currentColor" viewBox="0 0 20 20">
              <path
                fill-rule="evenodd"
                d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7 4a1 1 0 11-2 0 1 1 0 012 0zm-1-9a1 1 0 00-1 1v4a1 1 0 102 0V6a1 1 0 00-1-1z"
                clip-rule="evenodd"
              ></path>
            </svg>
            {shortnameUpdateError}
          </div>
        {/if}

        <div class="field-group">
          <label for="new-shortname" class="field-label"
            >{$_("modal.update_shortname.new_shortname")}</label
          >
          <input
            id="new-shortname"
            class="input-field"
            placeholder={formData.shortname ?? ""}
            bind:value={newShortname}
          />
        </div>
      </div>

      <div class="modal-footer">
        <button
          class="button-secondary"
          onclick={() => (isShortnameUpdateOpen = false)}
        >
          {$_("buttons.cancel")}
        </button>
        <button
          class="button-primary"
          disabled={!newShortname ||
            newShortname === formData.shortname ||
            isUpdatingShortname}
          onclick={updateShortname}
        >
          {isUpdatingShortname
            ? $_("buttons.updating")
            : $_("buttons.update_shortname")}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .form-card {
    background: var(--color-surface);
    border-radius: 12px;
    box-shadow:
      0 4px 6px -1px rgba(0, 0, 0, 0.1),
      0 2px 4px -1px rgba(0, 0, 0, 0.06);
    max-width: 56rem;
    margin: 0.5rem auto;
    padding: 1.5rem;
    border: 1px solid var(--color-border);
  }

  .form-card-full {
    max-width: 100%;
    margin: 0;
    padding: 0;
    border: none;
    box-shadow: none;
  }

  .form-container {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
  }

  .field-group {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .field-label {
    font-weight: 500;
    font-size: 0.875rem;
    color: var(--color-text);
    margin-bottom: 0.5rem;
  }

  .required {
    color: var(--color-danger);
    font-size: 1.125rem;
    vertical-align: middle;
    margin-inline-end: 0.25rem;
  }

  .field-help {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    margin-top: 0.25rem;
  }

  .input-field {
    padding: 0.625rem 0.75rem;
    border: 1px solid var(--color-border-strong);
    border-radius: 0.5rem;
    font-size: 0.875rem;
    transition: all 0.15s ease-in-out;
    background: var(--color-surface);
  }

  .input-field:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .input-field:disabled {
    background-color: var(--color-surface);
    color: var(--color-text-muted);
    cursor: not-allowed;
  }

  .textarea-field {
    padding: 0.625rem 0.75rem;
    border: 1px solid var(--color-border-strong);
    border-radius: 0.5rem;
    font-size: 0.875rem;
    transition: all 0.15s ease-in-out;
    background: var(--color-surface);
    resize: vertical;
    font-family: inherit;
  }

  .textarea-field:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .input-with-button {
    display: flex;
    margin-inline-end: 12px;
  }

  .input-left {
    border-start-end-radius: 0;
    border-end-end-radius: 0;
    border-inline-end: 0;
    flex: 1;
  }

  .button-right {
    border-start-start-radius: 0;
    border-end-start-radius: 0;
    border-inline-start: 1px solid var(--color-border-strong);
  }

  .button-primary {
    background-color: var(--color-primary);
    color: white;
    border: 1px solid var(--color-primary);
    padding: 0.625rem 1rem;
    border-radius: 0.5rem;
    font-size: 0.875rem;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.15s ease-in-out;
  }

  .button-primary:hover:not(:disabled) {
    background-color: var(--color-primary-hover);
    border-color: var(--color-primary-hover);
  }

  .button-primary:disabled {
    background-color: var(--color-text-faint);
    border-color: var(--color-text-faint);
    cursor: not-allowed;
  }

  .button-secondary {
    background-color: var(--color-surface);
    color: var(--color-text);
    border: 1px solid var(--color-border-strong);
    padding: 0.625rem 1rem;
    border-radius: 0.5rem;
    font-size: 0.875rem;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.15s ease-in-out;
  }

  .button-secondary:hover {
    background-color: var(--color-surface);
    border-color: var(--color-text-faint);
  }

  .checkbox-group {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .checkbox {
    width: 1rem;
    height: 1rem;
    border: 1px solid var(--color-border-strong);
    border-radius: 0.25rem;
    cursor: pointer;
  }

  .checkbox-label {
    font-size: 0.875rem;
    color: var(--color-text);
    cursor: pointer;
    margin: 0;
  }

  .translation-grid {
    display: grid;
    grid-template-columns: 1fr;
    gap: 1rem;
  }

  @media (min-width: 768px) {
    .translation-grid {
      grid-template-columns: repeat(3, 1fr);
    }
  }

  .translation-label {
    font-size: 0.875rem;
    color: var(--color-text-muted);
    font-weight: 500;
    margin-bottom: 0.25rem;
    display: block;
  }

  .accordion {
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    overflow: hidden;
  }

  .accordion-header {
    width: 100%;
    padding: 1rem;
    background-color: var(--color-surface);
    border: none;
    display: flex;
    justify-content: space-between;
    align-items: center;
    cursor: pointer;
    transition: background-color 0.15s ease-in-out;
  }

  .accordion-header:hover {
    background-color: var(--color-surface-3);
  }

  .accordion-title {
    font-weight: 500;
    color: var(--color-text);
  }

  .accordion-icon {
    width: 1.25rem;
    height: 1.25rem;
    transition: transform 0.15s ease-in-out;
  }

  .accordion-icon.rotated {
    transform: rotate(180deg);
  }

  .accordion-content {
    padding: 1.5rem;
    background-color: var(--color-surface);
    border-top: 1px solid var(--color-border);
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
  }

  .modal-overlay {
    position: fixed;
    top: 0;
    inset-inline-start: 0;
    inset-inline-end: 0;
    bottom: 0;
    background-color: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 50;
    padding: 1rem;
  }

  .modal-content {
    background: var(--color-surface);
    border-radius: 0.75rem;
    box-shadow:
      0 20px 25px -5px rgba(0, 0, 0, 0.1),
      0 10px 10px -5px rgba(0, 0, 0, 0.04);
    max-width: 28rem;
    width: 100%;
    max-height: 90vh;
    overflow-y: auto;
  }

  .modal-header {
    padding: 1.5rem;
    border-bottom: 1px solid var(--color-border);
    display: flex;
    align-items: center;
    justify-content: space-between;
  }

  .modal-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--color-text);
    margin: 0;
  }

  .modal-close {
    background: none;
    border: none;
    cursor: pointer;
    color: var(--color-text-muted);
    padding: 0.25rem;
    border-radius: 0.25rem;
    transition: color 0.15s ease-in-out;
  }

  .modal-close:hover {
    color: var(--color-text);
  }

  .modal-close svg {
    width: 1.25rem;
    height: 1.25rem;
  }

  .modal-body {
    padding: 1.5rem;
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .modal-warning {
    font-size: 0.875rem;
    color: var(--color-text-muted);
    margin: 0;
  }

  .modal-footer {
    padding: 1.5rem;
    border-top: 1px solid var(--color-border);
    display: flex;
    justify-content: flex-end;
    gap: 0.75rem;
  }

  .alert {
    padding: 0.75rem;
    border-radius: 0.5rem;
    display: flex;
    align-items: flex-start;
    gap: 0.75rem;
    font-size: 0.875rem;
  }

  .alert-error {
    background-color: var(--color-danger-soft);
    color: var(--color-danger);
    border: 1px solid var(--color-danger-soft);
  }

  .alert-icon {
    width: 1.25rem;
    height: 1.25rem;
    flex-shrink: 0;
    margin-top: 0.125rem;
  }

  .input-error {
    border-color: var(--color-danger) !important;
    box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.1) !important;
  }

  .input-error:focus {
    border-color: var(--color-danger) !important;
    box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.2) !important;
  }

  .field-error {
    font-size: 0.75rem;
    color: var(--color-danger);
    margin-top: 0.25rem;
    display: flex;
    align-items: center;
    gap: 0.375rem;
  }

  .error-icon {
    width: 0.875rem;
    height: 0.875rem;
    flex-shrink: 0;
  }
</style>
