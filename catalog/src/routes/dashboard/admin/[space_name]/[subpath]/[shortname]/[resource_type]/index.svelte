<script lang="ts">
  import { log } from "@/lib/logger";
  import { onMount, onDestroy } from "svelte";
  import { sanitizeHtml } from "@/lib/utils/sanitize";
  import { goto as gotoStore, params } from "@roxi/routify";
  import {
    deleteEntity,
    getEntity,
    getMyEntities,
    replaceEntity,
  } from "@/lib/dmart_services";
  import { can } from "@/stores/permissions";
  import { errorToastMessage } from "@/lib/toasts_messages";
  import { ContentType, ResourceType, DmartScope } from "@edraj/tsdmart";
  import { _, locale } from "@/i18n";
  import {
    isJsonObject,
    type EntryDetail,
    type EntryPayload,
    type EntryRecord,
    type JsonObject,
    type LocalizedText,
    type Relationship,
    type Schema,
  } from "@/lib/types";
  import { asFormSchema } from "@/lib/formUtils";
  import { errorMessage } from "@/lib/apiError";
  import { isJsonValue } from "@/components/json-table/types";
  import type { Breadcrumb } from "@/lib/paths";
  import { setTitle } from "@/lib/title";
  import { formatDate } from "@/lib/format";
  import { writable, type Readable } from "svelte/store";
  import Attachment from "@/components/Attachments.svelte";
  import HtmlEditor from "@/components/editors/HtmlEditor.svelte";
  import MarkdownEditor from "@/components/editors/MarkdownEditor.svelte";
  import { formatNumberInText } from "@/lib/helpers";
  import { renderMarkdown } from "@/lib/markdown";
  import JsonEditor from "@/components/editors/JsonEditor.svelte";
  import SchemaForm from "@/components/forms/SchemaForm.svelte";
  import DynamicSchemaBasedForms from "@/components/forms/DynamicSchemaBasedForms.svelte";
  import SchemaViewer from "@/components/forms/SchemaViewer.svelte";
  import JsonViewer from "@/components/JsonViewer.svelte";
  import RelationshipModal from "@/components/management/RelationshipModal.svelte";
  import AttachmentModal from "@/components/management/AttachmentModal.svelte";
  import { PlusOutline } from "flowbite-svelte-icons";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;


  const isLoading = writable(false);
  const itemData = writable<EntryDetail | null>(null);
  const error = writable<string | null>(null);
  const spaceName = writable("");
  const subpath = writable("");
  const itemShortname = writable("");
  const actualSubpath = writable("");
  let unsubscribeParams: () => void;
  const breadcrumbs = writable<Breadcrumb[]>([]);
  let spaceNameValue = $state("");
  let subpathValue = "";
  let itemShortnameValue = $state("");
  let actualSubpathValue = $state("");
  let breadcrumbsValue: Breadcrumb[] = [];
  const authorRelatedEntries = writable<EntryRecord[]>([]);
  let authorRelatedEntriesValue: EntryRecord[] = $state([]);
  let itemDataValue = $state<EntryDetail | null>(null);

  $effect(() => setTitle(itemDataValue ? getDisplayName(itemDataValue) : itemShortnameValue, spaceNameValue));
  const activeTab = writable("content");
  const showEditModal = writable(false);
  const showRelationshipModal = writable(false);
  const showAttachmentModal = writable(false);
  const showDeleteModal = writable(false);
  let isDeleting = writable(false);
  let htmlEditor: string = $state("");
  let markdownContent: string = $state("");
  let jsonEditorContent = $state<JsonObject>({});
  let isSchemaBasedItem = $state(false);
  // The schema document the SchemaForm edits (a schema entry's JSON body).
  let schemaEditorContent = $state<JsonObject>({});

  /** The schema a schema-based entry is edited with. */
  interface SelectedSchema {
    shortname: string;
    title: string;
    schema: Schema | null;
    description: string;
  }

  let isDynamicSchemaItem = $state(false);
  let selectedDynamicSchema = $state<SelectedSchema | null>(null);
  let dynamicSchemaFormData = $state<JsonObject>({});
  let loadingDynamicSchema = $state(false);

  async function loadDynamicSchema(schemaShortname: string) {
    loadingDynamicSchema = true;
    try {
      const response = await getEntity(
        schemaShortname,
        spaceNameValue,
        "/schema",
        ResourceType.content,
        DmartScope.managed,
        true,
        true,
      );
      if (response) {
        // A retrieved entry is flat: its displayname and description sit at
        // the top level (there is no `attributes` wrapper on a retrieve).
        selectedDynamicSchema = {
          shortname: response.shortname,
          title: response.displayname?.en || response.shortname,
          schema: asFormSchema(response.payload?.body),
          description: response.description?.en || "",
        };
        return true;
      }
    } catch (error) {
      log.error("Error loading dynamic schema:", error);
    } finally {
      loadingDynamicSchema = false;
    }
    return false;
  }

  /** A translation as the edit modal binds it: every language present. */
  type EditTranslation = { en: string; ar: string; ku: string };

  /** The edit modal's fields. */
  interface EditForm {
    displayname: EditTranslation;
    description: EditTranslation;
    content: string;
    tags: string[];
    newTag: string;
    is_active: boolean;
  }

  const emptyEditForm = (): EditForm => ({
    displayname: { en: "", ar: "", ku: "" },
    description: { en: "", ar: "", ku: "" },
    content: "",
    tags: [],
    newTag: "",
    is_active: true,
  });

  const editForm = writable<EditForm>(emptyEditForm());

  let editFormValue = $state<EditForm>(emptyEditForm());

  const jsonEditForm = writable<JsonObject>({});

  let jsonEditFormValue = $state<JsonObject>({});
  let relationshipsValue = $state<Relationship[]>([]);

  /** The editable content of an entry: its text body, or its JSON body as an object. */
  function getItemContent(item: EntryDetail | null): string | JsonObject {
    if (!item?.payload) return "";

    const contentType = item.payload.content_type;
    const body = item.payload.body;

    if (contentType === "json") {
      return isJsonObject(body) ? body : {};
    }

    return typeof body === "string" ? body : "";
  }

  function prepareContentForSave(
    content: string | JsonObject | undefined,
    originalContentType: string | undefined,
  ): string | JsonObject {
    if (originalContentType === "json") {
      if (isSchemaBasedItem) {
        return schemaEditorContent;
      }
      if (isDynamicSchemaItem && selectedDynamicSchema) {
        const originalContent = getItemContent(itemDataValue);
        if (isJsonObject(originalContent) && originalContent.schema_data) {
          return {
            ...originalContent,
            schema_data: dynamicSchemaFormData,
          };
        } else {
          return dynamicSchemaFormData;
        }
      }
      return jsonEditFormValue;
    }

    return content || "";
  }

  function handleJsonContentChange(newContent: JsonObject) {
    jsonEditorContent = newContent;
    jsonEditFormValue = jsonEditorContent;
    jsonEditForm.update((form) => ({
      ...form,
      content: jsonEditFormValue,
    }));
  }

  onMount(async () => {
    await initializeContent();
  });

  onDestroy(() => {
    if (unsubscribeParams) unsubscribeParams();
  });

  function subscribeStore<T>(store: Readable<T>, callback: (value: T) => void) {
    return store.subscribe(callback);
  }

  async function initializeContent() {
    unsubscribeParams = subscribeStore(params, async (value) => {
      spaceNameValue = value.space_name;
      subpathValue = value.subpath;
      itemShortnameValue = value.shortname;

      spaceName.set(spaceNameValue);
      subpath.set(subpathValue);
      itemShortname.set(itemShortnameValue);

      if (!subpathValue) return;

      actualSubpathValue = subpathValue.replace(/-/g, "/");
      actualSubpath.set(actualSubpathValue);

      const pathParts = actualSubpathValue
        .split("/")
        .filter((part) => part.length > 0);
      breadcrumbsValue = [
        {
          name: $_("admin_item_detail.breadcrumb.admin"),
          path: "/dashboard/admin",
        },
        { name: spaceNameValue, path: `/dashboard/admin/${spaceNameValue}` },
      ];

      let currentUrlPath = "";
      pathParts.forEach((part, index) => {
        currentUrlPath += (index === 0 ? "" : "-") + part;
        breadcrumbsValue.push({
          name: part,
          path: `/dashboard/admin/${spaceNameValue}/${currentUrlPath}`,
        });
      });

      breadcrumbsValue.push({
        name: itemShortnameValue,
        path: null,
      });

      breadcrumbs.set(breadcrumbsValue);

      if (actualSubpathValue === "authors") {
        await loadAuthorRelatedEntries();
      }
    });

    await loadItemData();
  }

  async function loadAuthorRelatedEntries() {
    try {
      const entries = await getMyEntities(itemShortnameValue);
      authorRelatedEntriesValue = entries;
      authorRelatedEntries.set(entries);
    } catch (err) {
      log.error("Error fetching author related entries:", err);
    }
  }

  async function loadItemData() {
    isLoading.set(true);
    error.set(null);

    try {
      const response = await getEntity(
        itemShortnameValue,
        spaceNameValue,
        actualSubpathValue,
        $params.resource_type || ResourceType.content,
        DmartScope.managed, // Default scope for admin
        true,
        true,
      );

      if (response) {
        itemDataValue = response;
        itemData.set(response);
        relationshipsValue = response.relationships || [];

        if (!response.payload?.body) {
          activeTab.set("overview");
        }

        const content = getItemContent(response);

        // Check if this is a schema-based item
        isSchemaBasedItem =
          response.payload?.schema_shortname === "meta_schema";

        const schemaShortname = response.payload?.schema_shortname;
        isDynamicSchemaItem = !!(
          response.payload?.content_type === "json" &&
          schemaShortname &&
          schemaShortname !== "templates" &&
          schemaShortname !== "meta_schema"
        );

        if (isDynamicSchemaItem && schemaShortname) {
          const schemaLoaded = await loadDynamicSchema(schemaShortname);
          if (schemaLoaded) {
            if (isJsonObject(content)) {
              const schemaData = content.schema_data;
              dynamicSchemaFormData = isJsonObject(schemaData) ? schemaData : content;
            } else {
              dynamicSchemaFormData = {};
            }
          }
        }

        if (response.payload?.content_type === "json") {
          const jsonContent = isJsonObject(content) ? content : {};
          if (isSchemaBasedItem) {
            schemaEditorContent = jsonContent;
          } else {
            jsonEditorContent = jsonContent;
            jsonEditFormValue = jsonContent;
          }
        }

        const tags = response.tags || [];

        editFormValue = {
          displayname: {
            en: response.displayname?.en || "",
            ar: response.displayname?.ar || "",
            ku: response.displayname?.ku || "",
          },
          description: {
            en: response.description?.en || "",
            ar: response.description?.ar || "",
            ku: response.description?.ku || "",
          },
          content:
            typeof content === "string"
              ? content || getDescription(response)
              : JSON.stringify(content),
          tags: [...tags],
          newTag: "",
          is_active: response.is_active ?? true,
        };
        editForm.set(editFormValue);

        const ct = response.payload?.content_type;
        htmlEditor = typeof content === "string" ? content : "";
        markdownContent = ct === ContentType.markdown && typeof content === "string" ? content : "";
      } else {
        log.error("No valid response found for item:", itemShortnameValue);
        error.set($_("admin_item_detail.error.item_not_found"));
      }
    } catch (err) {
      log.error("Error fetching admin item data:", err);
      error.set(
        errorMessage(err) || $_("admin_item_detail.error.failed_load_item"),
      );
    } finally {
      isLoading.set(false);
    }
  }

  async function handleUpdateItem(event: Event) {
    event.preventDefault();

    try {
      let htmlContent: string | JsonObject | undefined;

      if (itemDataValue?.payload?.content_type === "json") {
        if (isSchemaBasedItem) {
          htmlContent = JSON.stringify(schemaEditorContent);
        } else if (isDynamicSchemaItem && selectedDynamicSchema) {
          htmlContent = JSON.stringify(dynamicSchemaFormData);
        } else {
          htmlContent = JSON.stringify(jsonEditorContent);
        }
      } else {
        const ct = itemDataValue?.payload?.content_type;
        if (ct === "markdown" || ct === "md") {
          htmlContent = markdownContent || editFormValue.content || "";
        } else {
          htmlContent = htmlEditor || editFormValue.content;
        }
      }

      const contentType = itemDataValue?.payload?.content_type;
      let preparedContent = prepareContentForSave(htmlContent, contentType);

      const entityData = {
        displayname: editFormValue.displayname,
        description: editFormValue.description,
        tags: editFormValue.tags,
        is_active: editFormValue.is_active,
        payload: {
          content_type: contentType,
          body: preparedContent,
        },
      };

      const response = await replaceEntity(
        itemShortnameValue,
        spaceNameValue,
        actualSubpathValue,
        $params.resource_type || ResourceType.content,
        entityData,
      );

      if (response) {
        showEditModal.set(false);
        await loadItemData();
      } else {
        log.error("Update failed: No response received");
        error.set($_("admin_item_detail.error.failed_update_item"));
      }
    } catch (err) {
      log.error("Error updating item:", err);
      error.set(
        errorMessage(err) || $_("admin_item_detail.error.failed_update_item"),
      );
    }
  }

  function handleDeleteItem() {
    showDeleteModal.set(true);
  }

  async function confirmDeleteItem() {
    isDeleting.set(true);
    try {
      const success = await deleteEntity(
        itemShortnameValue,
        spaceNameValue,
        actualSubpathValue,
        $params.resource_type,
      );

      if (success) {
        showDeleteModal.set(false);
        goto("/dashboard/admin/[space_name]/[subpath]", {
          space_name: spaceNameValue,
          subpath: actualSubpathValue,
        });
      }
    } catch (err) {
      log.error("Error deleting item:", err);
      errorToastMessage($_("admin_item_detail.error.delete_failed"));
    } finally {
      isDeleting.set(false);
    }
  }

  /** What the page has a name for: a retrieved entry, or a query record (which keeps its name under `attributes`). */
  type Named = { shortname?: string; displayname?: LocalizedText | null; description?: LocalizedText | null };

  function getDisplayName(item: Named | null): string {
    if (item?.displayname) {
      const localeDisplay = item.displayname[$locale ?? ""]?.trim();
      const enDisplay = item.displayname.en?.trim();
      const arDisplay = item.displayname.ar?.trim();

      return localeDisplay || enDisplay || arDisplay || item.shortname || "";
    }
    return item?.shortname || $_("admin_item_detail.unnamed_item");
  }

  function getDescription(item: Named | null): string {
    if (item?.description) {
      return (
        item.description[$locale ?? ""] ||
        item.description.en ||
        item.description.ar ||
        $_("admin_item_detail.no_description")
      );
    }
    return $_("admin_item_detail.no_description");
  }

  /** Who a share attachment names, read off its payload. */
  function sharedWith(payload: EntryPayload | undefined): unknown {
    const bag: JsonObject = payload ?? {};
    return bag.shared_with;
  }

  function navigateToBreadcrumb(path: string | null) {
    if (!path) return;
    const pathSegments = path
      .split("/")
      .filter((segment) => segment !== "");

    if (
      pathSegments.length === 2 &&
      pathSegments[0] === "dashboard" &&
      pathSegments[1] === "admin"
    ) {
      goto("/dashboard/admin");
    } else if (
      pathSegments.length === 3 &&
      pathSegments[0] === "dashboard" &&
      pathSegments[1] === "admin"
    ) {
      const spaceName = pathSegments[2];
      goto(`/dashboard/admin/[space_name]`, {
        space_name: spaceName,
      });
    } else if (
      pathSegments.length === 4 &&
      pathSegments[0] === "dashboard" &&
      pathSegments[1] === "admin"
    ) {
      const spaceName = pathSegments[2];
      const subpath = pathSegments[3];
      goto(`/dashboard/admin/[space_name]/[subpath]`, {
        space_name: spaceName,
        subpath: subpath,
      });
    } else if (
      pathSegments.length === 5 &&
      pathSegments[0] === "dashboard" &&
      pathSegments[1] === "admin"
    ) {
      const spaceName = pathSegments[2];
      const subpath = pathSegments[3];
      const shortname = pathSegments[4];
      goto(
        `/dashboard/admin/[space_name]/[subpath]/[shortname]/[resource_type]`,
        {
          space_name: spaceName,
          subpath: subpath,
          shortname: shortname,
          resource_type: $params.resource_type,
        },
      );
    }
  }

  function goBack() {
    goto("/dashboard/admin/[space_name]/[subpath]", {
      space_name: spaceNameValue,
      subpath: subpathValue,
    });
  }

  function setActiveTab(tab: string) {
    activeTab.set(tab);
  }

  // Tags handlers
  function addTag() {
    if (editFormValue.newTag.trim() !== "") {
      editFormValue.tags = [...editFormValue.tags, editFormValue.newTag.trim()];
      editFormValue.newTag = "";
    }
  }

  function removeTag(index: number) {
    editFormValue.tags = editFormValue.tags.filter(
      (_, i) => i !== index,
    );
  }
</script>

<div class="min-h-screen bg-surface">
  <div class="bg-surface">
    <div class="container mx-auto px-6 py-6 max-w-7xl">
      <div
        class="flex flex-col md:flex-row md:items-center justify-between gap-4"
      >
        <div class="flex items-center gap-4">
          <button
            onclick={goBack}
            class="w-10 h-10 bg-primary-soft hover:bg-primary-soft text-primary rounded-xl flex items-center justify-center transition-colors shadow-sm"
            aria-label={$_("admin_space.navigation.go_back")}
          >
            <svg
              class="w-5 h-5 shrink-0"
              class:rtl:rotate-180={true}
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M15 19l-7-7 7-7"
              ></path>
            </svg>
          </button>

          <div>
            <nav
              class="flex text-sm text-text-muted font-medium mb-1"
              aria-label={$_("ui.breadcrumb")}
            >
              <ol class="inline-flex items-center space-x-2">
                {#each $breadcrumbs as crumb, index (index)}
                  <li class="inline-flex items-center">
                    {#if index > 0}
                      <svg
                        class="w-4 h-4 mx-1 text-text-faint"
                        class:rtl:rotate-180={true}
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M9 5l7 7-7 7"
                        ></path>
                      </svg>
                    {/if}
                    {#if crumb.path}
                      <button
                        onclick={() => navigateToBreadcrumb(crumb.path)}
                        class="hover:text-primary transition-colors"
                      >
                        {crumb.name}
                      </button>
                    {:else}
                      <span class="text-text">{crumb.name}</span>
                    {/if}
                  </li>
                {/each}
              </ol>
            </nav>
            <h1 class="text-2xl font-bold text-text">
              {itemDataValue
                ? getDisplayName(itemDataValue)
                : itemShortnameValue}
            </h1>
          </div>
        </div>

        <div class="flex items-center gap-3">
          {#if $can("update", $params.space_name, actualSubpathValue, $params.resource_type || ResourceType.content)}
            <button
              onclick={() => showEditModal.set(true)}
              class="bg-surface-2 hover:bg-surface border border-border text-text px-3 py-1.5 rounded-xl font-medium transition-colors shadow-sm flex items-center gap-2"
            >
              <svg
                class="w-4 h-4 text-text-muted"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
                ></path>
              </svg>
              {$_("admin_item_detail.actions.edit_item")}
            </button>
          {/if}
          {#if $can("delete", $params.space_name, actualSubpathValue, $params.resource_type || ResourceType.content)}
            <button
              onclick={handleDeleteItem}
              class="bg-danger-soft hover:bg-danger-soft text-danger px-3 py-1.5 rounded-xl font-medium transition-colors shadow-sm flex items-center gap-2"
            >
              <svg
                class="w-4 h-4"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                ></path>
              </svg>
              {$_("admin_item_detail.actions.delete_item")}
            </button>
          {/if}
        </div>
      </div>
    </div>
  </div>

  <div class="container mx-auto px-6 py-6 max-w-7xl">
    {#if $isLoading}
      <div class="flex justify-center py-16">
        <div class="spinner spinner-lg"></div>
      </div>
    {:else if $error}
      <div class="text-center py-16">
        <div
          class="mx-auto w-24 h-24 bg-danger-soft rounded-full flex items-center justify-center mb-6"
        >
          <svg
            class="w-12 h-12 text-danger"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
            ></path>
          </svg>
        </div>
        <h3 class="text-xl font-semibold text-text mb-2">
          {$_("admin_item_detail.error.title")}
        </h3>
        <p class="text-text-muted">{$error}</p>
      </div>
    {:else if itemDataValue}
      <div
        class="bg-surface-2 rounded-3xl shadow-[0_2px_8px_rgba(0,0,0,0.04)] border border-border mb-6 overflow-hidden"
      >
        <div class="border-b border-border bg-surface/30">
          <nav
            class="flex px-6 overflow-x-auto hide-scrollbar"
          >
            {#if itemDataValue?.payload?.body}
              <button
                onclick={() => setActiveTab("content")}
                class="py-4 px-4 font-medium text-sm whitespace-nowrap border-b-2 transition-colors {$activeTab ===
                'content'
                  ? 'border-primary text-primary'
                  : 'border-transparent text-text-muted hover:text-text hover:border-border-strong'}"
              >
                {$_("admin_item_detail.tabs.content")}
              </button>
            {/if}
            <button
              onclick={() => setActiveTab("overview")}
              class="py-4 px-4 font-medium text-sm whitespace-nowrap border-b-2 transition-colors {$activeTab ===
              'overview'
                ? 'border-primary text-primary'
                : 'border-transparent text-text-muted hover:text-text hover:border-border-strong'}"
            >
              {$_("admin_item_detail.tabs.overview")}
            </button>
            <button
              onclick={() => setActiveTab("attachments")}
              class="py-4 px-4 font-medium text-sm whitespace-nowrap border-b-2 transition-colors {$activeTab ===
              'attachments'
                ? 'border-primary text-primary'
                : 'border-transparent text-text-muted hover:text-text hover:border-border-strong'}"
            >
              {$_("admin_item_detail.tabs.attachments")}
            </button>
            {#if actualSubpathValue === "authors"}
              <button
                onclick={() => setActiveTab("author-entries")}
                class="py-4 px-4 font-medium text-sm whitespace-nowrap border-b-2 transition-colors {$activeTab ===
                'author-entries'
                  ? 'border-primary text-primary'
                  : 'border-transparent text-text-muted hover:text-text hover:border-border-strong'}"
              >
                {$_("admin_item_detail.tabs.author_entries")}
              </button>
            {/if}
          </nav>
        </div>

        <div class="p-8">
          {#if $activeTab === "content"}
            <div class="space-y-4">
              {#if itemDataValue.payload}
                {@const ct = itemDataValue.payload.content_type}
                {@const body = itemDataValue.payload.body}

                <div class="rounded-2xl border border-border overflow-hidden">
                  <!-- content-type badge -->
                  <div
                    class="bg-surface/60 px-5 py-3 border-b border-border flex items-center gap-2"
                  >
                    <span class="text-xs font-medium text-text-muted"
                      >Content type:</span
                    >
                    <span
                      class="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-info-soft text-info"
                      >{ct}</span
                    >
                  </div>

                  <div class="p-6">
                    {#if ct === "html"}
                      <div class="html-preview">
                        {@html sanitizeHtml(typeof body === "string" ? body : String(body))}
                      </div>
                    {:else if ct === "json"}
                      {#if isSchemaBasedItem}
                        <div class="p-6">
                          <SchemaViewer content={body} />
                        </div>
                      {:else}
                        <div class="p-6">
                          <JsonViewer
                            data={isJsonValue(body) ? body : null}
                            title={itemDataValue?.displayname?.en ||
                              "JSON Content"}
                            isAdmin={true}
                            editable={true}
                            schemaShortname={itemDataValue?.payload
                              ?.schema_shortname}
                            spaceName={$params.space_name}
                            subpath={actualSubpathValue}
                            shortname={$params.shortname}
                            onSaved={(d) => {
                              if (itemDataValue?.payload) itemDataValue.payload.body = d;
                            }}
                          />
                        </div>
                      {/if}
                    {:else}
                      <!-- Default parse string as Markdown (covers "markdown", "md", or missing type) -->
                      {#if typeof body === "string"}
                        <div class="markdown-preview">
                          {@html renderMarkdown(body)}
                        </div>
                      {:else}
                        <!-- Fallback for unexpected non-string bodies without a known type -->
                        <pre
                          class="bg-surface rounded-xl p-4 text-xs whitespace-pre-wrap text-text">{JSON.stringify(
                            body,
                          )}</pre>
                      {/if}
                    {/if}
                  </div>
                </div>
              {:else}
                <div
                  class="text-center py-8 text-text-muted"
                >
                  <svg
                    class="mx-auto h-12 w-12 text-text-faint"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
                    />
                  </svg>
                  <p class="mt-2">
                    {$_("admin_item_detail.content.no_content")}
                  </p>
                </div>
              {/if}
            </div>
          {/if}
          {#if $activeTab === "overview"}
            <div class="space-y-6">
              <div>
                <h3
                  class="text-lg font-semibold text-text mb-4"
                >
                  {$_("admin_item_detail.overview.basic_info")}
                </h3>
                <div
                  class="bg-surface-2 border border-border rounded-2xl overflow-hidden"
                >
                  <table
                    class="min-w-full divide-y divide-border"
                  >
                    <tbody class="bg-surface-2 divide-y divide-border">
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50 w-1/4"
                          >{$_("admin_item_detail.fields.uuid")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted font-mono">{itemDataValue.uuid}</td
                        >
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.shortname")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >{itemDataValue.shortname}</td
                        >
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.display_name")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                        >
                          {#if itemDataValue.displayname}
                            <div class="space-y-1">
                              {#each Object.entries(itemDataValue.displayname) as [lang, name] (lang)}
                                <div
                                  class="flex items-center space-x-2"
                                >
                                  <span
                                    class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-info-soft text-info"
                                    >{lang}</span
                                  >
                                  <span>{name}</span>
                                </div>
                              {/each}
                            </div>
                          {:else}
                            <span class="text-text-faint"
                              >{$_("admin_item_detail.not_set")}</span
                            >
                          {/if}
                        </td>
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.description")}</td
                        >
                        <td
                          class="px-3 py-1.5 text-sm text-text-muted"
                        >
                          {#if itemDataValue.description}
                            <div class="space-y-1">
                              {#each Object.entries(itemDataValue.description) as [lang, desc] (lang)}
                                <div
                                  class="flex items-start space-x-2"
                                >
                                  <span
                                    class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-info-soft text-info mt-0.5"
                                    >{lang}</span
                                  >
                                  <span class="flex-1"
                                    >{desc ||
                                      $_("admin_item_detail.empty")}</span
                                  >
                                </div>
                              {/each}
                            </div>
                          {:else}
                            <span class="text-text-faint"
                              >{$_("admin_item_detail.not_set")}</span
                            >
                          {/if}
                        </td>
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.status")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                        >
                          <span
                            class="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium {itemDataValue.is_active
                              ? 'bg-success-soft text-success'
                              : 'bg-danger-soft text-danger'}"
                          >
                            <div
                              class="w-1.5 h-1.5 rounded-full me-1.5 {itemDataValue.is_active
                                ? 'bg-success'
                                : 'bg-danger'}"
                              class:me-1.5={true}
                              
                            ></div>
                            {itemDataValue.is_active
                              ? $_("admin_item_detail.status.active")
                              : $_("admin_item_detail.status.inactive")}
                          </span>
                        </td>
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.content_type")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >{itemDataValue.payload?.content_type ||
                            $_("admin_item_detail.not_set")}</td
                        >
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.resource_type")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >{$params.resource_type ||
                            $_("admin_item_detail.not_set")}</td
                        >
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.schema_shortname")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >{itemDataValue.payload?.schema_shortname ||
                            $_("admin_item_detail.not_set")}</td
                        >
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.tags")}</td
                        >
                        <td
                          class="px-3 py-1.5 text-sm text-text-muted"
                        >
                          {#if itemDataValue.tags && itemDataValue.tags.length > 0}
                            <div
                              class="flex flex-wrap gap-1"
                            >
                              {#each itemDataValue.tags as tag (tag)}
                                {#if tag.trim()}
                                  <span
                                    class="inline-flex items-center px-2 py-1 rounded-md text-xs font-medium bg-surface-3 text-text"
                                    >{tag}</span
                                  >
                                {/if}
                              {/each}
                            </div>
                          {:else}
                            <span class="text-text-faint"
                              >{$_("admin_item_detail.no_tags")}</span
                            >
                          {/if}
                        </td>
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.owner")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >{itemDataValue.owner_shortname}</td
                        >
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.created")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >{formatDate(itemDataValue.created_at, "datetime", $locale)}</td
                        >
                      </tr>
                      <tr>
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text bg-surface/50"
                          >{$_("admin_item_detail.fields.updated")}</td
                        >
                        <td
                          class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >{formatDate(itemDataValue.updated_at, "datetime", $locale)}</td
                        >
                      </tr>
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          {/if}
          {#if $activeTab === "attachments"}
            <div class="space-y-6">
              <div
                class="flex items-center justify-between"
              >
                <h3
                  class="text-lg font-semibold text-text"
                >
                  {$_("admin_item_detail.attachments.title")}
                </h3>
                <div
                  class="flex items-center gap-3"
                >
                  <button
                    onclick={() => showAttachmentModal.set(true)}
                    class="bg-primary hover:bg-primary-hover text-text-on-primary px-3 py-1.5 rounded-xl font-medium transition-colors shadow-sm flex items-center gap-2"
                  >
                    <PlusOutline class="w-4 h-4" />
                    {$_("admin_item_detail.attachments.upload")}
                  </button>
                </div>
              </div>

              {#if itemDataValue.attachments && typeof itemDataValue.attachments === "object"}
                {#each Object.entries(itemDataValue.attachments) as [type, attachmentsArrRaw] (type)}
                  {#if Array.isArray(attachmentsArrRaw) && attachmentsArrRaw.length > 0}
                    {@const attachmentsArr = attachmentsArrRaw ?? []}
                    <div
                      class="bg-surface-2 border border-border rounded-2xl overflow-hidden"
                    >
                      <div
                        class="bg-surface/50 px-3 py-1.5 border-b border-border"
                      >
                        <h4
                          class="text-md font-medium text-text capitalize flex items-center gap-2"
                        >
                          {#if type === "share"}
                            <svg
                              class="w-5 h-5 text-primary"
                              fill="none"
                              stroke="currentColor"
                              viewBox="0 0 24 24"
                            >
                              <path
                                stroke-linecap="round"
                                stroke-linejoin="round"
                                stroke-width="2"
                                d="M8.684 13.342C8.886 12.938 9 12.482 9 12c0-.482-.114-.938-.316-1.342m0 2.684a3 3 0 110-2.684m0 2.684l6.632 3.316m-6.632-6l6.632-3.316m0 0a3 3 0 105.367-2.684 3 3 0 00-5.367 2.684zm0 9.316a3 3 0 105.367 2.684 3 3 0 00-5.367-2.684z"
                              />
                            </svg>
                          {:else if type === "media"}
                            <svg
                              class="w-5 h-5 text-success"
                              fill="none"
                              stroke="currentColor"
                              viewBox="0 0 24 24"
                            >
                              <path
                                stroke-linecap="round"
                                stroke-linejoin="round"
                                stroke-width="2"
                                d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z"
                              />
                            </svg>
                          {:else}
                            <svg
                              class="w-5 h-5 text-text-muted"
                              fill="none"
                              stroke="currentColor"
                              viewBox="0 0 24 24"
                            >
                              <path
                                stroke-linecap="round"
                                stroke-linejoin="round"
                                stroke-width="2"
                                d="M15.172 7l-6.586 6.586a2 2 0 102.828 2.828l6.414-6.586a4 4 0 00-5.656-5.656l-6.415 6.585a6 6 0 108.486 8.486L20.5 13"
                              />
                            </svg>
                          {/if}

                          <span
                            class="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium"
                            class:bg-success-soft={type === "media"}
                            class:text-success={type === "media"}
                            class:bg-surface-3={type !== "media"}
                            class:text-text={type !== "media"}
                          >
                            {formatNumberInText(
                              attachmentsArr.length,
                              $locale ?? "",
                            )}
                          </span>
                          {$_("admin_item_detail.attachments.type_count", {
                            values: {
                              type: type,
                              count: formatNumberInText(
                                attachmentsArr.length,
                                $locale ?? "",
                              ),
                            },
                          })}
                        </h4>
                      </div>

                      <div class="p-6">
                        {#if type === "share"}
                          <div class="space-y-3">
                            {#each attachmentsArr as share (share.shortname)}
                              <div
                                class="bg-primary-soft rounded-lg p-4 border border-primary/30"
                              >
                                <div class="flex items-center justify-between">
                                  <div
                                    class="flex items-center space-x-3"
                                  >
                                    <div
                                      class="w-10 h-10 bg-primary rounded-full flex items-center justify-center"
                                    >
                                      <svg
                                        class="w-5 h-5 text-primary"
                                        fill="none"
                                        stroke="currentColor"
                                        viewBox="0 0 24 24"
                                      >
                                        <path
                                          stroke-linecap="round"
                                          stroke-linejoin="round"
                                          stroke-width="2"
                                          d="M8.684 13.342C8.886 12.938 9 12.482 9 12c0-.482-.114-.938-.316-1.342m0 2.684a3 3 0 110-2.684m0 2.684l6.632 3.316m-6.632-6l6.632-3.316m0 0a3 3 0 105.367-2.684 3 3 0 00-5.367 2.684zm0 9.316a3 3 0 105.367 2.684 3 3 0 00-5.367-2.684z"
                                        />
                                      </svg>
                                    </div>

                                    <div>
                                      <div
                                        class="flex items-center space-x-2 mb-1"
                                      >
                                        <span
                                          class="text-sm font-medium text-text"
                                        >
                                          {share.attributes.owner_shortname ||
                                            "Anonymous"}
                                        </span>
                                        <span class="text-xs text-text-muted">
                                          shared
                                        </span>
                                      </div>
                                      <p class="text-xs text-text-muted">
                                        {new Date(
                                          share.attributes.created_at ?? "",
                                        ).toLocaleDateString()} at {new Date(
                                          share.attributes.created_at ?? "",
                                        ).toLocaleTimeString()}
                                      </p>
                                    </div>
                                  </div>

                                  <div class="text-end">
                                    <p class="text-xs text-text-faint mb-1">
                                      ID: {share.shortname}
                                    </p>
                                    <span
                                      class="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-primary-soft text-primary"
                                    >
                                      {share.resource_type}
                                    </span>
                                  </div>
                                </div>

                                {#if sharedWith(share.attributes.payload)}
                                  <div
                                    class="mt-2 pt-2 border-t border-primary/30"
                                  >
                                    <p class="text-xs text-text-muted">
                                      Shared with: {sharedWith(
                                        share.attributes.payload,
                                      )}
                                    </p>
                                  </div>
                                {/if}

                                {#if share.attributes.updated_at !== share.attributes.created_at}
                                  <div
                                    class="mt-2 pt-2 border-t border-primary/30"
                                  >
                                    <p class="text-xs text-text-muted">
                                      Last updated: {new Date(
                                        share.attributes.updated_at ?? "",
                                      ).toLocaleDateString()}
                                    </p>
                                  </div>
                                {/if}
                              </div>
                            {/each}
                          </div>
                        {:else}
                          <Attachment
                            attachments={Object.values(attachmentsArr ?? [])}
                            resource_type={$params.resource_type}
                            space_name={spaceNameValue}
                            subpath={actualSubpathValue}
                            parent_shortname={itemShortnameValue}
                            isOwner={true}
                          />
                        {/if}
                      </div>
                    </div>
                  {/if}
                {/each}
              {:else}
                <div
                  class="text-center py-8 text-text-muted"
                >
                  <svg
                    class="mx-auto h-12 w-12 text-text-faint"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M15.172 7l-6.586 6.586a2 2 0 102.828 2.828l6.414-6.586a4 4 0 00-5.656-5.656l-6.415 6.585a6 6 0 108.486 8.486L20.5 13"
                    />
                  </svg>
                  <p class="mt-2">
                    {$_("admin_item_detail.attachments.no_attachments")}
                  </p>
                </div>
              {/if}
            </div>
          {/if}

          {#if $activeTab === "author-entries"}
            <div class="space-y-6">
              <h3
                class="text-lg font-semibold text-text"
              >
                {$_("admin_item_detail.author_entries.title")}
              </h3>

              {#if authorRelatedEntriesValue && authorRelatedEntriesValue.length > 0}
                <div
                  class="bg-surface-2 border border-border rounded-2xl overflow-hidden"
                >
                  <table
                    class="min-w-full divide-y divide-border"
                  >
                    <thead class="bg-surface/50">
                      <tr>
                        <th
                          class="px-6 py-3 text-start text-xs font-medium text-text-muted uppercase tracking-wider"
                        >
                          {$_(
                            "admin_item_detail.author_entries.headers.shortname",
                          )}
                        </th>
                        <th
                          class="px-6 py-3 text-start text-xs font-medium text-text-muted uppercase tracking-wider"
                        >
                          {$_(
                            "admin_item_detail.author_entries.headers.display_name",
                          )}
                        </th>
                        <th
                          class="px-6 py-3 text-start text-xs font-medium text-text-muted uppercase tracking-wider"
                        >
                          {$_("admin_item_detail.author_entries.headers.space")}
                        </th>
                        <th
                          class="px-6 py-3 text-start text-xs font-medium text-text-muted uppercase tracking-wider"
                        >
                          {$_("admin_item_detail.author_entries.headers.type")}
                        </th>
                        <th
                          class="px-6 py-3 text-start text-xs font-medium text-text-muted uppercase tracking-wider"
                        >
                          {$_(
                            "admin_item_detail.author_entries.headers.status",
                          )}
                        </th>
                        <th
                          class="px-6 py-3 text-start text-xs font-medium text-text-muted uppercase tracking-wider"
                        >
                          {$_(
                            "admin_item_detail.author_entries.headers.created",
                          )}
                        </th>
                      </tr>
                    </thead>
                    <tbody class="bg-surface-2 divide-y divide-border">
                      {#each authorRelatedEntriesValue as entry (entry.shortname)}
                        <tr>
                          <td
                            class="px-3 py-1.5 whitespace-nowrap text-sm font-medium text-text"
                          >
                            {entry.shortname}
                          </td>
                          <td
                            class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >
                            {getDisplayName(entry)}
                          </td>
                          <td
                            class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >
                            {entry.space_name || $_("common.not_available")}
                          </td>
                          <td
                            class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >
                            <span
                              class="inline-flex items-center px-2 py-1 rounded-md text-xs font-medium bg-info-soft text-info"
                            >
                              {entry.resource_type || "content"}
                            </span>
                          </td>
                          <td
                            class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >
                            <span
                              class="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium {entry.attributes?.is_active
                                ? 'bg-success-soft text-success'
                                : 'bg-danger-soft text-danger'}"
                            >
                              <div
                                class="w-1.5 h-1.5 rounded-full me-1.5 {entry.attributes?.is_active
                                  ? 'bg-success'
                                  : 'bg-danger'}"
                                class:me-1.5={true}

                              ></div>
                              {entry.attributes?.is_active
                                ? $_("admin_item_detail.status.active")
                                : $_("admin_item_detail.status.inactive")}
                            </span>
                          </td>
                          <td
                            class="px-3 py-1.5 whitespace-nowrap text-sm text-text-muted"
                          >
                            {formatDate(entry.attributes?.created_at, "datetime", $locale)}
                          </td>
                        </tr>
                      {/each}
                    </tbody>
                  </table>
                </div>
              {:else}
                <div
                  class="text-center py-8 text-text-muted"
                >
                  <svg
                    class="mx-auto h-12 w-12 text-text-faint"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 2 0 01-2 2z"
                    />
                  </svg>
                  <p class="mt-2">
                    {$_("admin_item_detail.author_entries.no_entries")}
                  </p>
                </div>
              {/if}
            </div>
          {/if}
        </div>
      </div>
    {:else}
      <div class="text-center py-16">
        <div
          class="mx-auto w-24 h-24 bg-surface-3 rounded-full flex items-center justify-center mb-6"
        >
          <svg
            class="w-12 h-12 text-text-faint"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 2 0 01-2 2z"
            ></path>
          </svg>
        </div>
        <h3 class="text-xl font-semibold text-text mb-2">
          {$_("admin_item_detail.not_found.title")}
        </h3>
        <p class="text-text-muted">
          {$_("admin_item_detail.not_found.description")}
        </p>
      </div>
    {/if}
  </div>
</div>

{#if $showEditModal}
  <div
    class="modal-overlay"
    role="dialog"
    aria-modal="true"
    tabindex="-1"
    onclick={(e) => {
      if (e.target === e.currentTarget) {
        showEditModal.set(false);
      }
    }}
    onkeydown={(e) => {
      if (e.key === "Escape") {
        showEditModal.set(false);
      }
    }}
  >
    <section class="modal-container" role="document">
      <div class="modal-header">
        <div class="header-content">
          <div class="header-text">
            <h3 class="modal-title">
              {$_("admin_item_detail.edit_modal.title")}
            </h3>
          </div>
          <button
            onclick={() => showEditModal.set(false)}
            class="close-button"
            aria-label={$_("admin_item_detail.edit_modal.actions.cancel")}
          >
            <svg
              class="close-icon"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M6 18L18 6M6 6l12 12"
              />
            </svg>
          </button>
        </div>
      </div>

      <div class="modal-content">
        <form class="modal-form" onsubmit={handleUpdateItem}>
          <div class="form-grid-vertical">
            <!-- Display Name Row: 3 columns (EN, AR, KU) -->
            <div class="form-section">
              <p class="form-label section-label">
                <svg
                  class="label-icon"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M7 7h.01M7 3h5c.512 0 1.024.195 1.414.586l7 7a2 2 0 010 2.828l-7 7a2 2 0 01-2.828 0l-7-7A1.99 1.99 0 013 12V7a4 4 0 014-4z"
                  />
                </svg>
                {$_("admin_item_detail.edit_modal.fields.displayname")}
              </p>
              <div class="localized-inputs-3col">
                <div class="localized-field-col">
                  <span class="lang-badge">EN</span>
                  <input
                    type="text"
                    bind:value={editFormValue.displayname.en}
                    class="form-input"
                    placeholder={$_("labels.displayname_lang", { values: { language: $_("english") } })}
                  />
                </div>
                <div class="localized-field-col">
                  <span class="lang-badge">AR</span>
                  <input
                    type="text"
                    bind:value={editFormValue.displayname.ar}
                    class="form-input"
                    placeholder={$_("labels.displayname_lang", { values: { language: $_("arabic") } })}
                  />
                </div>
                <div class="localized-field-col">
                  <span class="lang-badge">KU</span>
                  <input
                    type="text"
                    bind:value={editFormValue.displayname.ku}
                    class="form-input"
                    placeholder={$_("labels.displayname_lang", { values: { language: $_("kurdish") } })}
                  />
                </div>
              </div>
            </div>

            <!-- Description Row: 3 columns (EN, AR, KU) -->
            <div class="form-section">
              <p class="form-label section-label">
                <svg
                  class="label-icon"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M4 6h16M4 12h16M4 18h7"
                  />
                </svg>
                {$_("admin_item_detail.edit_modal.fields.description")}
              </p>
              <div class="localized-inputs-3col">
                <div class="localized-field-col">
                  <span class="lang-badge">EN</span>
                  <textarea
                    bind:value={editFormValue.description.en}
                    class="form-input form-textarea"
                    placeholder={$_("labels.description_lang", { values: { language: $_("english") } })}
                    rows="2"
                  ></textarea>
                </div>
                <div class="localized-field-col">
                  <span class="lang-badge">AR</span>
                  <textarea
                    bind:value={editFormValue.description.ar}
                    class="form-input form-textarea"
                    placeholder={$_("labels.description_lang", { values: { language: $_("arabic") } })}
                    rows="2"
                  ></textarea>
                </div>
                <div class="localized-field-col">
                  <span class="lang-badge">KU</span>
                  <textarea
                    bind:value={editFormValue.description.ku}
                    class="form-input form-textarea"
                    placeholder={$_("labels.description_lang", { values: { language: $_("kurdish") } })}
                    rows="2"
                  ></textarea>
                </div>
              </div>
            </div>

            <!-- Tags Section with Badge Behavior -->
            <div class="form-section">
              <p class="form-label section-label">
                <svg
                  class="label-icon"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M7 7h.01M7 3h5c.512 0 1.024.195 1.414.586l7 7a2 2 0 010 2.828l-7 7a2 2 0 01-2.828 0l-7-7A1.99 1.99 0 013 12V7a4 4 0 014-4z"
                  />
                </svg>
                {$_("admin_item_detail.edit_modal.fields.tags")}
              </p>
              <div class="tag-input-wrapper">
                <div class="tag-input-row">
                  <input
                    type="text"
                    bind:value={editFormValue.newTag}
                    class="form-input tag-input"
                    placeholder={$_(
                      "admin_item_detail.edit_modal.placeholders.tags",
                    )}
                    onkeydown={(e) => {
                      if (e.key === "Enter") {
                        e.preventDefault();
                        addTag();
                      }
                    }}
                  />
                  <button
                    type="button"
                    class="add-tag-btn"
                    onclick={addTag}
                    disabled={!editFormValue.newTag.trim()}
                  >
                    <svg
                      class="btn-icon"
                      fill="none"
                      stroke="currentColor"
                      viewBox="0 0 24 24"
                    >
                      <path
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        stroke-width="2"
                        d="M12 4v16m8-8H4"
                      />
                    </svg>
                    Add
                  </button>
                </div>
                {#if editFormValue.tags.length > 0}
                  <div class="tags-badges-container">
                    {#each editFormValue.tags as tag, index (index)}
                      <span class="tag-badge">
                        {tag}
                        <button
                          type="button"
                          class="tag-remove-btn"
                          onclick={() => removeTag(index)}
                          aria-label={$_("route_labels.search_remove_tag")}
                        >
                          <svg
                            class="remove-icon"
                            fill="none"
                            stroke="currentColor"
                            viewBox="0 0 24 24"
                          >
                            <path
                              stroke-linecap="round"
                              stroke-linejoin="round"
                              stroke-width="2"
                              d="M6 18L18 6M6 6l12 12"
                            />
                          </svg>
                        </button>
                      </span>
                    {/each}
                  </div>
                {/if}
              </div>
            </div>

            <!-- Active Status -->
            <div class="form-section status-section">
              <div class="status-toggle-simple">
                <button
                  type="button"
                  class="toggle-switch {editFormValue.is_active
                    ? 'active'
                    : ''}"
                  onclick={() => {
                    editFormValue.is_active = !editFormValue.is_active;
                  }}
                  aria-label={$_("labels.toggle_active")}
                  aria-pressed={editFormValue.is_active}
                >
                  <div class="toggle-slider"></div>
                </button>
                <div class="status-info">
                  <span class="status-label">
                    {$_("admin_item_detail.edit_modal.fields.active")}
                  </span>
                  <span
                    class="status-value {editFormValue.is_active
                      ? 'active'
                      : 'inactive'}"
                  >
                    {editFormValue.is_active ? "Active" : "Inactive"}
                  </span>
                </div>
              </div>
            </div>

            <!-- Content Editor -->
            <div class="editor-column">
              <div class="editor-group">
                <label
                  for="editContent"
                  class="form-label"
                >
                  <svg
                    class="label-icon"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 2 0 01-2 2z"
                    />
                  </svg>
                  {$_("admin_item_detail.edit_modal.fields.content")}
                </label>
                <div class="editor-container">
                  {#if isSchemaBasedItem}
                    <SchemaForm bind:content={schemaEditorContent} />
                  {:else if isDynamicSchemaItem}
                    {#if loadingDynamicSchema}
                      <div class="schema-loading p-4 text-center">
                        <div class="spinner spinner-md"></div>
                        <p class="mt-2">Loading schema...</p>
                      </div>
                    {:else if selectedDynamicSchema}
                      <div class="schema-form-wrapper">
                        <div
                          class="schema-info-bar mb-4 pb-2 border-b border-border"
                        >
                          <span class="schema-label font-medium text-text-muted"
                            >Schema:</span
                          >
                          <span
                            class="schema-name font-semibold text-text ms-2"
                            >{selectedDynamicSchema.title}</span
                          >
                        </div>
                        <DynamicSchemaBasedForms
                          bind:content={dynamicSchemaFormData}
                          schema={selectedDynamicSchema.schema}
                          space={$params.space_name}
                          subpath={actualSubpathValue}
                          resourceType={$params.resource_type ||
                            ResourceType.content}
                        />
                      </div>
                    {/if}
                  {:else if itemDataValue?.payload?.content_type === "json"}
                    <div class="json-editor-with-preview">
                      <div class="json-editor-pane">
                        <JsonEditor
                          content={jsonEditorContent}
                          isEditMode={true}
                          onContentChange={handleJsonContentChange}
                        />
                      </div>
                      <div class="json-preview-pane">
                        <h4 class="preview-title">Preview</h4>
                        <JsonViewer
                          data={isJsonValue(jsonEditFormValue) ? jsonEditFormValue : null}
                          title={$_("labels.json_preview")}
                          type="json"
                          isAdmin={true}
                          schemaShortname={itemDataValue?.payload
                            ?.schema_shortname}
                          spaceName={$params.space_name}
                          subpath={actualSubpathValue}
                          shortname={$params.shortname}
                          onSaved={(d) => {
                            if (isJsonObject(d)) jsonEditFormValue = d;
                          }}
                        />
                      </div>
                    </div>
                  {:else if itemDataValue?.payload?.content_type === "markdown" || itemDataValue?.payload?.content_type === "md"}
                    <MarkdownEditor
                      bind:content={markdownContent}
                      space_name={spaceNameValue}
                      subpath={actualSubpathValue}
                      parent_shortname={itemShortnameValue}
                      isEditMode={true}
                      attachments={itemDataValue?.attachments ?? null}
                    />
                  {:else}
                    <HtmlEditor
                      bind:content={htmlEditor}
                      space_name={spaceNameValue}
                      subpath={actualSubpathValue}
                      parent_shortname={itemShortnameValue}
                      uid="main-editor"
                      isEditMode={true}
                      attachments={itemDataValue?.attachments ?? null}
                      changed={() => {
                      }}
                    />
                  {/if}
                </div>
              </div>
            </div>
          </div>

          <div class="modal-actions">
            <div class="actions-container">
              <button
                type="button"
                onclick={() => showEditModal.set(false)}
                class="cancel-button"
                aria-label={$_("labels.cancel_editing")}
              >
                <svg
                  class="button-icon"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M6 18L18 6M6 6l12 12"
                  />
                </svg>
                {$_("admin_item_detail.edit_modal.actions.cancel")}
              </button>
              <!-- Removed onclick handler from submit button to rely on form onsubmit -->
              <button
                aria-label={$_("common.save_changes")}
                type="submit"
                class="save-button"
              >
                <svg
                  class="button-icon"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M5 13l4 4L19 7"
                  />
                </svg>
                {$_("admin_item_detail.edit_modal.actions.save")}
              </button>
            </div>
          </div>
        </form>
      </div>
    </section>
  </div>
{/if}

<!-- Relationship Management Modal -->
{#if $showRelationshipModal}
  <RelationshipModal
    bind:isOpen={$showRelationshipModal}
    bind:relationships={relationshipsValue}
    space_name={spaceNameValue}
    subpath={actualSubpathValue}
    resource_type={$params.resource_type}
    parent_shortname={itemShortnameValue}
  />
{/if}

<!-- Attachment Upload Modal -->
{#if $showAttachmentModal}
  <AttachmentModal
    bind:isOpen={$showAttachmentModal}
    space_name={spaceNameValue}
    subpath={actualSubpathValue}
    resource_type={$params.resource_type}
    parent_shortname={itemShortnameValue}
    onAttachmentCreated={loadItemData}
  />
{/if}

<!-- Delete Confirmation Modal -->
{#if $showDeleteModal}
  <div
    class="fixed inset-0 bg-[var(--surface-overlay)] backdrop-blur-sm z-50 flex items-center justify-center p-4"
    onclick={(e) => {
      if (e.target === e.currentTarget) showDeleteModal.set(false);
    }}
    onkeydown={(e) => {
      if (e.key === "Escape") showDeleteModal.set(false);
    }}
    role="dialog"
    aria-modal="true"
    aria-labelledby="delete-modal-title"
    tabindex="-1"
  >
    <div
      class="bg-surface-2 rounded-2xl shadow-2xl max-w-md w-full transform transition-all"
      role="document"
    >
      <!-- Modal Header -->
      <div class="bg-danger-soft px-3 py-1.5 border-b border-danger/30 rounded-t-2xl">
        <div class="flex items-center gap-3">
          <div
            class="w-10 h-10 bg-danger-soft rounded-full flex items-center justify-center"
          >
            <svg
              class="w-5 h-5 text-danger"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
              />
            </svg>
          </div>
          <h3
            id="delete-modal-title"
            class="text-lg font-semibold text-text"
          >
            {$_("admin_item_detail.delete_modal.title")}
          </h3>
        </div>
      </div>

      <!-- Modal Body -->
      <div class="px-6 py-5">
        <p class="text-text-muted">
          {$_("admin_item_detail.delete_modal.message", {
            values: { name: itemShortnameValue },
          })}
        </p>
        <div class="mt-4 p-3 bg-warning-soft border border-warning/30 rounded-lg">
          <div class="flex items-start gap-2">
            <svg
              class="w-5 h-5 text-warning mt-0.5 shrink-0"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"
              />
            </svg>
            <p class="text-sm text-warning">
              {$_("admin_item_detail.delete_modal.warning")}
            </p>
          </div>
        </div>
      </div>

      <!-- Modal Footer -->
      <div class="px-3 py-1.5 bg-surface rounded-b-2xl flex justify-end gap-3">
        <button
          onclick={() => showDeleteModal.set(false)}
          disabled={$isDeleting}
          class="px-3 py-1.5 text-text bg-surface-2 border border-border-strong rounded-xl font-medium hover:bg-surface transition-colors disabled:opacity-50"
        >
          {$_("common.cancel")}
        </button>
        <button
          onclick={confirmDeleteItem}
          disabled={$isDeleting}
          class="px-3 py-1.5 bg-danger hover:bg-danger-hover text-text-on-primary rounded-xl font-medium transition-colors shadow-sm flex items-center gap-2 disabled:opacity-50"
        >
          {#if $isDeleting}
            <svg
              class="animate-spin h-4 w-4 text-text-on-primary"
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
            >
              <circle
                class="opacity-25"
                cx="12"
                cy="12"
                r="10"
                stroke="currentColor"
                stroke-width="4"
              ></circle>
              <path
                class="opacity-75"
                fill="currentColor"
                d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
              ></path>
            </svg>
            {$_("common.deleting")}
          {:else}
            <svg
              class="w-4 h-4"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
              />
            </svg>
            {$_("common.delete")}
          {/if}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .markdown-preview,
  .html-preview {
    height: 100%;
    padding: 1rem;
    overflow-y: auto;
    background: var(--color-surface);
    font-family:
      "uthmantn",
      -apple-system,
      BlinkMacSystemFont,
      "Segoe UI",
      Roboto,
      "Helvetica Neue",
      Arial,
      sans-serif;
    line-height: 1.6;
    color: var(--color-text);
    white-space: pre-wrap;
    word-wrap: break-word;
  }

  /* Headings */
  .markdown-preview :global(h1),
  .html-preview :global(h1),
  .markdown-preview :global(h2),
  .html-preview :global(h2),
  .markdown-preview :global(h3),
  .html-preview :global(h3),
  .markdown-preview :global(h4),
  .html-preview :global(h4),
  .markdown-preview :global(h5),
  .html-preview :global(h5),
  .markdown-preview :global(h6),
  .html-preview :global(h6) {
    margin-top: 1.5em;
    margin-bottom: 0.5em;
    font-weight: 600;
    line-height: 1.25;
    color: var(--color-text);
  }

  .markdown-preview :global(h1),
  .html-preview :global(h1) {
    font-size: 2em;
    padding-bottom: 0.3em;
    border-bottom: 1px solid var(--color-border);
  }
  .markdown-preview :global(h2),
  .html-preview :global(h2) {
    font-size: 1.5em;
    padding-bottom: 0.3em;
    border-bottom: 1px solid var(--color-border);
  }
  .markdown-preview :global(h3),
  .html-preview :global(h3) {
    font-size: 1.25em;
  }
  .markdown-preview :global(h4),
  .html-preview :global(h4) {
    font-size: 1em;
  }
  .markdown-preview :global(h5),
  .html-preview :global(h5) {
    font-size: 0.875em;
  }
  .markdown-preview :global(h6),
  .html-preview :global(h6) {
    font-size: 0.85em;
    color: var(--color-text-muted);
  }

  /* Paragraphs and Inline Text */
  .markdown-preview :global(p),
  .html-preview :global(p) {
    margin-top: 0;
    margin-bottom: 1rem;
  }

  .markdown-preview :global(a),
  .html-preview :global(a) {
    color: var(--color-primary-hover);
    text-decoration: none;
  }
  .markdown-preview :global(a:hover),
  .html-preview :global(a:hover) {
    text-decoration: underline;
  }

  .markdown-preview :global(strong),
  .html-preview :global(strong) {
    font-weight: 600;
  }

  /* Lists */
  .markdown-preview :global(ul),
  .html-preview :global(ul),
  .markdown-preview :global(ol),
  .html-preview :global(ol) {
    margin-top: 0;
    margin-bottom: 1rem;
    padding-inline-start: 2em;
  }
  .markdown-preview :global(ul),
  .html-preview :global(ul) {
    list-style-type: disc;
  }
  .markdown-preview :global(ol),
  .html-preview :global(ol) {
    list-style-type: decimal;
  }

  .markdown-preview :global(li),
  .html-preview :global(li) {
    margin-top: 0.25em;
  }

  /* Blockquotes */
  .markdown-preview :global(blockquote),
  .html-preview :global(blockquote) {
    margin: 0 0 1rem;
    padding: 0 1em;
    color: var(--color-text-muted);
    border-inline-start: 0.25em solid var(--color-border);
  }

  /* Code and Preformatted Text */
  .markdown-preview :global(code),
  .html-preview :global(code) {
    padding: 0.2em 0.4em;
    margin: 0;
    font-size: 85%;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas,
      "Liberation Mono", "Courier New", monospace;
    background-color: var(--color-surface-3);
    border-radius: 6px;
  }

  .markdown-preview :global(pre),
  .html-preview :global(pre) {
    padding: 1rem;
    overflow: auto;
    font-size: 85%;
    line-height: 1.45;
    background-color: var(--color-surface-3);
    border-radius: 6px;
    margin-bottom: 1rem;
  }
  .markdown-preview :global(pre code),
  .html-preview :global(pre code) {
    padding: 0;
    background-color: transparent;
    border-radius: 0;
  }

  /* Tables */
  .markdown-preview :global(table),
  .html-preview :global(table) {
    display: block;
    width: 100%;
    width: max-content;
    max-width: 100%;
    overflow: auto;
    margin-top: 0;
    margin-bottom: 1rem;
    border-spacing: 0;
    border-collapse: collapse;
  }

  .markdown-preview :global(table th),
  .html-preview :global(table th),
  .markdown-preview :global(table td),
  .html-preview :global(table td) {
    padding: 6px 13px;
    border: 1px solid var(--color-border);
  }

  .markdown-preview :global(table tr:nth-child(2n)),
  .html-preview :global(table tr:nth-child(2n)) {
    background-color: var(--color-surface);
  }

  /* RTL Support */



  .modal-overlay {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.6);
    backdrop-filter: blur(4px);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 50;
    padding: 1rem;
  }

  .modal-container {
    background: var(--color-surface);
    border-radius: 1rem;
    box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.25);
    width: 100%;
    max-width: 80rem;
    max-height: 90vh;
    overflow: hidden;
    overflow: hidden;
    animation: modalSlideIn 0.3s ease-out;
  }

  @keyframes modalSlideIn {
    from {
      opacity: 0;
      transform: scale(0.95) translateY(-20px);
    }
    to {
      opacity: 1;
      transform: scale(1) translateY(0);
    }
  }

  .modal-header {
    background: linear-gradient(135deg, var(--color-primary-hover) 0%, var(--color-primary-hover) 100%);
    padding: 0.6rem 1.5rem;
    color: white;
  }

  .header-content {
    display: flex;
    justify-content: space-between;
    align-items: center;
  }

  .modal-title {
    font-size: 1.125rem;
    font-weight: 700;
    margin: 0;
  }

  .close-button {
    background: rgba(255, 255, 255, 0.1);
    border: none;
    color: rgba(255, 255, 255, 0.8);
    border-radius: 50%;
    padding: 0.35rem;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .close-button:hover {
    background: rgba(255, 255, 255, 0.2);
    color: white;
  }

  .close-icon {
    width: 1.5rem;
    height: 1.5rem;
  }

  .modal-content {
    overflow-y: auto;
    max-height: calc(90vh - 60px);
  }

  .modal-form {
    padding: 1.25rem 1.5rem;
  }

  .editor-column {
    grid-column: span 1;
    width: 100%;
  }

  .editor-group {
    position: relative;
  }

  .form-label {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--color-text);
    margin-bottom: 0.75rem;
    cursor: pointer;
  }

  .label-icon {
    width: 1rem;
    height: 1rem;
    color: var(--color-text-muted);
  }

  .form-input {
    width: 100%;
    padding: 0.75rem 1rem;
    border: 2px solid var(--color-border);
    border-radius: 0.75rem;
    background: rgba(249, 250, 251, 0.5);
    font-size: 0.875rem;
    transition: all 0.2s ease;
    outline: none;
  }

  .form-input:hover {
    background: var(--color-surface);
    border-color: var(--color-border-strong);
  }

  .form-input:focus {
    background: var(--color-surface);
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }


  .toggle-switch {
    width: 3rem;
    height: 1.5rem;
    background: var(--color-border-strong);
    border-radius: 9999px;
    box-shadow: inset 0 2px 4px rgba(0, 0, 0, 0.1);
    transition: background-color 0.2s ease;
    cursor: pointer;
    position: relative;
    border: none;
    outline: none;
  }

  .toggle-switch.active {
    background: var(--color-primary);
  }

  .toggle-switch:focus {
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .toggle-slider {
    position: absolute;
    top: 2px;
    inset-inline-start: 2px;
    width: 1.25rem;
    height: 1.25rem;
    background: var(--color-surface);
    border-radius: 50%;
    box-shadow: 0 2px 4px rgba(0, 0, 0, 0.2);
    transition: transform 0.2s ease;
  }

  .toggle-switch.active .toggle-slider {
    transform: translateX(1.5rem);
  }

  .status-info {
    flex: 1;
  }

  .status-label {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--color-text);
    cursor: pointer;
    margin-bottom: 0.25rem;
  }

  .editor-container {
    padding: 12px;
    border: 2px solid var(--color-border);
    border-radius: 0.75rem;
    background: var(--color-surface);
    box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
    transition: border-color 0.2s ease;
    min-height: 400px;
    height: auto;
  }

  .editor-container:hover {
    border-color: var(--color-border-strong);
  }

  .modal-actions {
    margin-top: 2.5rem;
    padding-top: 2rem;
    border-top: 1px solid var(--color-border);
  }

  .actions-container {
    display: flex;
    justify-content: flex-end;
    gap: 1rem;
  }

  .cancel-button,
  .save-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    font-size: 0.875rem;
    font-weight: 600;
    border-radius: 0.75rem;
    cursor: pointer;
    transition: all 0.2s ease;
    border: none;
    outline: none;
  }

  .cancel-button {
    background: var(--color-surface);
    color: var(--color-text);
    border: 2px solid var(--color-border);
  }

  .cancel-button:hover {
    background: var(--color-surface);
    border-color: var(--color-border-strong);
  }

  .cancel-button:focus {
    box-shadow: 0 0 0 3px rgba(107, 114, 128, 0.1);
  }

  .save-button {
    background: linear-gradient(135deg, var(--color-primary) 0%, var(--color-primary-hover) 100%);
    color: white;
    border: 2px solid transparent;
    box-shadow: 0 4px 6px -1px rgba(59, 130, 246, 0.3);
    padding: 0.75rem 2rem;
  }

  .save-button:hover {
    background: linear-gradient(135deg, var(--color-primary-hover) 0%, var(--color-primary-hover) 100%);
    box-shadow: 0 6px 8px -1px rgba(59, 130, 246, 0.4);
    transform: translateY(-1px);
  }

  .save-button:focus {
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.2);
  }

  .button-icon {
    width: 1rem;
    height: 1rem;
  }

  /* RTL Support */


  /* Mobile Responsiveness */
  @media (max-width: 768px) {
    .modal-container {
      margin: 0.5rem;
      max-height: 98vh;
    }

    .modal-header {
      padding: 1.5rem;
    }

    .modal-title {
      font-size: 1.25rem;
    }

    .modal-form {
      padding: 1.5rem;
    }

    .actions-container {
      flex-direction: column;
      gap: 0.75rem;
    }

    .cancel-button,
    .save-button {
      width: 100%;
      justify-content: center;
    }
  }

  .form-input,
  .toggle-switch,
  .cancel-button,
  .save-button {
    transition: all 0.2s cubic-bezier(0.4, 0, 0.2, 1);
  }

  /* Focus states for accessibility */
  .form-input:focus,
  .toggle-switch:focus-within,
  .cancel-button:focus,
  .save-button:focus {
    outline: 2px solid transparent;
    outline-offset: 2px;
  }

  /* JSON Editor with Json Preview */
  .json-editor-with-preview {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 20px;
    height: 100%;
    min-height: 500px;
  }

  .json-editor-pane {
    overflow: visible;
  }

  .json-preview-pane {
    border-inline-start: 1px solid var(--color-border);
    padding-inline-start: 20px;
    overflow: auto;
  }

  .preview-title {
    font-size: 14px;
    font-weight: 600;
    color: var(--color-text);
    margin-bottom: 12px;
    padding-bottom: 8px;
    border-bottom: 1px solid var(--color-border);
  }

  @media (max-width: 1024px) {
    .json-editor-with-preview {
      grid-template-columns: 1fr;
    }

    .json-preview-pane {
      border-inline-start: none;
      border-top: 1px solid var(--color-border);
      padding-inline-start: 0;
      padding-top: 20px;
    }
  }

  /* Vertical Form Layout Styles */
  .form-grid-vertical {
    display: flex;
    flex-direction: column;
    gap: 24px;
  }

  .form-section {
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .section-label {
    font-weight: 600;
    font-size: 14px;
    color: var(--color-text);
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 4px;
  }

  .localized-inputs-3col {
    display: grid;
    grid-template-columns: 1fr 1fr 1fr;
    gap: 12px;
  }

  @media (max-width: 768px) {
    .localized-inputs-3col {
      grid-template-columns: 1fr;
    }
  }

  .localized-field-col {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  .lang-badge {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 32px;
    height: 28px;
    padding: 0 8px;
    background: var(--color-border);
    color: var(--color-text);
    font-size: 11px;
    font-weight: 600;
    border-radius: 6px;
    text-transform: uppercase;
  }

  .form-textarea {
    resize: vertical;
    min-height: 60px;
    font-family: inherit;
  }

  /* Tags Input Styles */
  .tag-input-wrapper {
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .tag-input-row {
    display: flex;
    gap: 8px;
  }

  .tag-input {
    flex: 1;
  }

  .add-tag-btn {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 8px 16px;
    background: var(--color-primary);
    color: white;
    border: none;
    border-radius: 8px;
    font-size: 14px;
    font-weight: 500;
    cursor: pointer;
    transition: background-color 0.2s;
    white-space: nowrap;
  }

  .add-tag-btn:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }

  .add-tag-btn:disabled {
    background: var(--color-text-faint);
    cursor: not-allowed;
  }

  .add-tag-btn .btn-icon {
    width: 16px;
    height: 16px;
  }

  .tags-badges-container {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    padding-top: 8px;
    border-top: 1px solid var(--color-border);
  }

  .tag-badge {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 6px 12px;
    background: var(--color-info-soft);
    color: var(--color-info);
    font-size: 13px;
    font-weight: 500;
    border-radius: 20px;
    transition: background-color 0.2s;
  }

  .tag-badge:hover {
    background: var(--color-info-soft);
  }

  .tag-remove-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 16px;
    height: 16px;
    padding: 0;
    background: transparent;
    border: none;
    color: var(--color-primary);
    cursor: pointer;
    border-radius: 50%;
    transition:
      background-color 0.2s,
      color 0.2s;
  }

  .tag-remove-btn:hover {
    background: var(--color-primary);
    color: white;
  }

  .tag-remove-btn .remove-icon {
    width: 12px;
    height: 12px;
  }

  /* Status Toggle Simple */
  .status-section {
    padding: 0;
  }

  .status-toggle-simple {
    display: flex;
    align-items: center;
    gap: 16px;
  }

  .status-toggle-simple .status-info {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .status-toggle-simple .status-label {
    font-weight: 600;
    font-size: 14px;
    color: var(--color-text);
  }

  .status-toggle-simple .status-value {
    font-size: 13px;
    font-weight: 500;
  }

  .status-toggle-simple .status-value.active {
    color: var(--color-success);
  }

  .status-toggle-simple .status-value.inactive {
    color: var(--color-danger);
  }
</style>
