<script lang="ts">
    import { ResourceType } from "@edraj/tsdmart";
    import Prism from "@/components/Prism.svelte";
    import MarkdownView from "@/components/management/renderers/MarkdownView.svelte";
    import { limitJsonForDisplay } from "@/utils/displayJson";
    import { isRecord } from "@/utils/compare";
    import { _ } from "@/i18n";

    // Shows one attachment's content inside the view modal. Props are reactive
    // (runes), so switching the selected attachment while the modal is open
    // re-renders the right thing instead of the first one.
    let {
        attributes = {},
        resource_type,
        url,
        displayname = "",
    }: {
        /** The attachment record's `attributes`; the payload inside is read defensively. */
        attributes?: Record<string, unknown>;
        resource_type: ResourceType;
        url: string;
        displayname?: string;
    } = $props();

    const payload = $derived(isRecord(attributes.payload) ? attributes.payload : undefined);
    const contentType = $derived<string>(typeof payload?.content_type === "string" ? payload.content_type : "");
    const body = $derived(payload?.body);
    // A comment's body is `{ state, body }`.
    const commentBody = $derived(isRecord(body) ? body : undefined);
    const jsonPreview = $derived(limitJsonForDisplay(body));
</script>

{#if resource_type === ResourceType.comment}
    <dl class="w-full text-sm space-y-2">
        <div>
            <dt class="font-medium text-text-muted">{$_("state")}</dt>
            <dd class="text-text">{commentBody?.state ?? $_("not_applicable")}</dd>
        </div>
        <div>
            <dt class="font-medium text-text-muted">{$_("body")}</dt>
            <dd class="text-text whitespace-pre-wrap break-words">{commentBody?.body ?? ""}</dd>
        </div>
    </dl>
{:else if resource_type === ResourceType.json || resource_type === ResourceType.reaction}
    <div class="w-full space-y-2">
        {#if jsonPreview.truncated}
            <p class="text-xs text-text-muted">{$_("preview_truncated")}</p>
        {/if}
        <div class="max-h-[70vh] overflow-auto">
            <Prism language="json" code={jsonPreview.value as object | string} />
        </div>
        {#if resource_type === ResourceType.reaction}
            <p class="text-sm text-text"><span class="font-medium text-text-muted">{$_("type")}:</span> {attributes.type ?? $_("not_applicable")}</p>
        {/if}
    </div>
{:else if contentType.includes("image")}
    {#if url.endsWith("svg")}
        <object data={url} type="image/svg+xml" title={displayname} class="max-w-full">
            <img src={url} alt={displayname} class="max-w-full h-auto rounded-control border border-border" loading="lazy" decoding="async" />
        </object>
    {:else}
        <img src={url} alt={displayname} class="max-w-full h-auto rounded-control border border-border" loading="lazy" decoding="async" />
    {/if}
{:else if contentType.includes("audio")}
    <audio controls src={url} class="w-full">
        <track kind="captions" />
    </audio>
{:else if contentType.includes("video")}
    <video controls src={url} class="max-w-full max-h-[70vh]">
        <track kind="captions" />
    </video>
{:else if contentType.includes("pdf")}
    <!-- iframe, not <object>: the CSP this app serves carries object-src 'none',
         so an <object> embed is refused outright and the user gets the fallback
         text below instead of the document. An iframe falls under frame-src,
         which inherits default-src 'self' — and the payload is same-origin. -->
    <iframe title={displayname} class="pdf-viewer" src={url}></iframe>
{:else if ["markdown", "html", "text"].includes(contentType)}
    <div class="w-full">
        <MarkdownView source={typeof body === "string" ? body : ""} rows={4} />
    </div>
{:else}
    <a href={url} title={displayname} target="_blank" rel="noopener noreferrer" download class="text-primary hover:underline">
        {$_("download_file", { values: { name: displayname } })}
    </a>
{/if}

<style>
    .pdf-viewer {
        width: 100%;
        height: 90vh;
        min-height: 500px;
    }
</style>
