<script lang="ts">
  import { _, locale } from "@/i18n";
  import { renderMarkdown } from "@/lib/markdown";
  import { sanitizeHtml } from "@/lib/utils/sanitize";
  import { getPostContent } from "@/lib/utils/postUtils";
  import { localized, type Localized } from "@/lib/catalogItems";
  import { getTemplate } from "@/lib/dmart_services/templates";
  import { getCurrentScope } from "@/stores/user";
  import { APPLICATIONS_SPACE } from "@/lib/constants";
  import JsonViewer from "@/components/JsonViewer.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";
  import MarkdownBody from "./MarkdownBody.svelte";

  // The body of an entry: HTML as-is (sanitized), JSON through the viewer,
  // anything else as Markdown through the one shared renderer in
  // lib/markdown (no per-mount marked.use(), review perf #32). A
  // template-based entry fetches its template and fills the placeholders.
  interface TemplateBody {
    template: string;
    data: Record<string, unknown>;
  }

  interface PostContentData {
    displayname?: Localized;
    space_name?: string;
    payload?: { content_type?: string; schema_shortname?: string; body?: unknown };
    [key: string]: unknown;
  }

  let {
    postData,
    spaceName = "",
    isAdmin = false,
  }: { postData: PostContentData | null; spaceName?: string; isAdmin?: boolean } = $props();

  const payload = $derived(postData?.payload);
  const diagramLabel = $derived($_("post_detail.markdown.diagram_source"));

  const templateBody = $derived.by((): TemplateBody | null => {
    if (payload?.schema_shortname !== "templates") return null;
    const body = payload.body;
    if (!body || typeof body !== "object") return null;
    const { template, data } = body as Partial<TemplateBody>;
    if (typeof template !== "string" || !template || !data || typeof data !== "object") return null;
    return { template, data };
  });
  const isTemplateEntry = $derived(templateBody !== null);
  const isJson = $derived(payload?.content_type === "json");
  const hasContent = $derived(isTemplateEntry || !!getPostContent(postData));

  // Markdown/HTML body → sanitized HTML, recomputed only when the body or the
  // caption's language changes.
  const bodyHtml = $derived.by(() => {
    if (!payload || payload.body === null || payload.body === undefined || isJson || isTemplateEntry) return "";
    const body = payload.body;
    if (payload.content_type === "html") return typeof body === "string" ? sanitizeHtml(body) : "";
    if (typeof body === "string") return renderMarkdown(body, { diagramLabel });
    return "";
  });

  // ---- Template-based entries ----
  let templateHtml = $state("");
  let isLoadingTemplate = $state(false);
  let templateError = $state("");
  let loadedTemplateKey = "";

  $effect(() => {
    const body = templateBody;
    if (!body || !spaceName) return;
    const key = `${spaceName}\u0000${body.template}\u0000${JSON.stringify(body.data)}`;
    if (key === loadedTemplateKey) return;
    loadedTemplateKey = key;
    void loadTemplateContent(body, key);
  });

  async function loadTemplateContent(body: TemplateBody, key: string) {
    isLoadingTemplate = true;
    templateError = "";
    templateHtml = "";
    try {
      // The current space first, then the shared applications space.
      let template = await getTemplate(spaceName, body.template, getCurrentScope());
      if (!template) template = await getTemplate(APPLICATIONS_SPACE, body.template, getCurrentScope());
      if (key !== loadedTemplateKey) return;
      if (!template) {
        templateError = $_("post_detail.template.not_found", { values: { name: body.template } });
        return;
      }
      const content: unknown = template.attributes?.payload?.body?.content;
      if (typeof content !== "string" || !content) {
        templateError = $_("post_detail.template.empty");
        return;
      }
      templateHtml = renderMarkdown(renderTemplateWithData(content, body.data), { diagramLabel });
    } catch (err) {
      console.error("Error loading template:", err);
      if (key === loadedTemplateKey) templateError = $_("post_detail.template.load_failed");
    } finally {
      if (key === loadedTemplateKey) isLoadingTemplate = false;
    }
  }

  // Replace {{fieldName}} / {{fieldName:type}} with the entry's data; a
  // placeholder with no value is left as written.
  function renderTemplateWithData(templateContent: string, data: Record<string, unknown>): string {
    return templateContent.replace(/\{\{(\w+)(?::(\w+))?\}\}/g, (match, fieldName: string) => {
      const value = data[fieldName];
      return value === undefined || value === null ? match : String(value);
    });
  }

  const jsonTitle = $derived(localized(postData?.displayname, $locale) || $_("post_detail.content_type.json"));
</script>

{#if hasContent}
  <section class="mt-6">
    {#if templateBody}
      {#if isLoadingTemplate}
        <LoadingState label={$_("post_detail.template.loading")} />
      {:else if templateError}
        <ErrorState compact message={templateError} />
        <div class="mt-4 rounded-card border border-border bg-surface p-4 text-sm">
          <p class="font-semibold text-text">
            {$_("post_detail.template.label", { values: { name: templateBody.template } })}
          </p>
          <dl class="mt-2 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
            {#each Object.entries(templateBody.data) as [key, value] (key)}
              <dt class="font-medium text-text-muted">{key}</dt>
              <dd class="text-text break-words">{typeof value === "object" ? JSON.stringify(value) : String(value)}</dd>
            {/each}
          </dl>
        </div>
      {:else}
        <MarkdownBody html={templateHtml} />
      {/if}
    {:else if isJson}
      <JsonViewer
        data={payload?.body}
        title={jsonTitle}
        {isAdmin}
        schemaShortname={payload?.schema_shortname}
        spaceName={postData?.space_name}
      />
    {:else if bodyHtml}
      <MarkdownBody html={bodyHtml} />
    {:else if payload?.body !== undefined}
      <pre class="fallback">{JSON.stringify(payload.body, null, 2)}</pre>
    {/if}
  </section>
{/if}

<style>
  .fallback {
    background: var(--color-surface-3);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-card);
    padding: 1rem;
    font-size: var(--font-size-sm);
    color: var(--color-text);
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    direction: ltr;
    text-align: start;
  }
</style>
