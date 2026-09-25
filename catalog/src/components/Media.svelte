<script lang="ts">
  import { ResourceType } from "@edraj/tsdmart";
  import { marked } from "marked";
  import { onMount, onDestroy } from "svelte";
  import { sanitizeHtml } from "@/lib/utils/sanitize";

  export let attributes: any = {};
  export let resource_type: ResourceType;
  export let url: string;
  export let displayname: string | undefined = undefined;
  let content_type: string = attributes?.payload?.content_type || "";
  let body: any = attributes?.payload?.body;

  // A type to pin the blob to, or null to keep whatever the response declared.
  //
  // Only the branches that could interpret bytes as markup are pinned. An
  // <iframe> handed a blob typed text/html executes it in this origin, and the
  // element is chosen from dmart's declared content_type — so a file whose
  // stored bytes disagree with its metadata is the case to close. Pinning
  // application/pdf hands the frame to the browser's PDF viewer instead, and a
  // mislabelled file simply fails to display.
  //
  // <img>/<audio>/<video> never execute markup, and audio and video need the
  // real type for codec selection, so those keep the server's own value. A
  // wildcard like "audio/*" is not a valid Blob type and would blank it.
  function pinnedType(ct: string, u: string): string | null {
    if (ct.includes("pdf")) return "application/pdf";
    if (ct.includes("image") && u.endsWith("svg")) return "image/svg+xml";
    return null;
  }

  let blobUrl: string | null = null;
  let loading = true;
  let error = false;

  onMount(async () => {
    if (
      content_type.includes("image") ||
      content_type.includes("video") ||
      content_type.includes("audio") ||
      content_type.includes("pdf")
    ) {
      try {
        const token = localStorage.getItem("authToken");
        const headers: Record<string, string> = {};
        if (token) {
          headers["Authorization"] = `Bearer ${token}`;
        }
        const res = await fetch(url, { headers, credentials: "include" });
        if (res.ok) {
          // See pinnedType: PDF and SVG are retyped so a mislabelled file
          // cannot be interpreted as markup; everything else keeps the
          // response's own Content-Type.
          const pinned = pinnedType(content_type, url);
          const blob = pinned
            ? new Blob([await res.arrayBuffer()], { type: pinned })
            : await res.blob();
          blobUrl = URL.createObjectURL(blob);
        } else {
          error = true;
        }
      } catch {
        error = true;
      } finally {
        loading = false;
      }
    } else {
      loading = false;
    }
  });

  onDestroy(() => {
    if (blobUrl) {
      URL.revokeObjectURL(blobUrl);
    }
  });
</script>

{#if content_type.includes("image")}
  {#if loading}
    <div class="media-loading"><div class="spinner spinner-md"></div></div>
  {:else if error}
    <div class="media-error">Failed to load image</div>
  {:else if blobUrl}
    <!-- SVG renders through <img>, not <object>. A browser disables scripting
         in an SVG loaded as an image, while <object> executes it — so this is
         the safer element as well as the one object-src 'none' permits. The
         previous markup already carried this <img> as its fallback. -->
    <img src={blobUrl} alt={displayname || "no-image"} class="media-img" />
  {/if}
{:else if content_type.includes("audio")}
  {#if loading}
    <div class="media-loading"><div class="spinner spinner-md"></div></div>
  {:else if blobUrl}
    <audio controls src={blobUrl}>
      <track kind="captions" />
    </audio>
  {/if}
{:else if content_type.includes("video")}
  {#if loading}
    <div class="media-loading"><div class="spinner spinner-md"></div></div>
  {:else if blobUrl}
    <video controls src={blobUrl}>
      <track kind="captions" />
    </video>
  {/if}
{:else if content_type.includes("pdf")}
  {#if loading}
    <div class="media-loading"><div class="spinner spinner-md"></div></div>
  {:else if blobUrl}
    <!-- <iframe>, not <object>: object-src stays 'none' because <object> can
         instantiate plugins and execute embedded script, which is a materially
         larger XSS surface than rendering a document. The blob is retyped to
         application/pdf below, so the frame is handed to the browser's PDF
         viewer and never interpreted as markup. -->
    <div class="media-pdf-wrap">
      <iframe title={displayname} class="media-pdf" src={blobUrl}></iframe>
    </div>
  {/if}
{:else if ["markdown", "html", "text"].includes(content_type)}
  <div>
    {@html sanitizeHtml(marked(body))}
  </div>
{:else}
  <a
    href={url}
    title={displayname}
    target="_blank"
    rel="noopener noreferrer"
    download>link {displayname}</a
  >
{/if}

<style>
  .media-img {
    max-width: 100%;
    height: auto;
    border-radius: 0.5rem;
    border: 1px solid var(--color-gray-200);
    display: block;
  }

  .media-loading {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 120px;
    color: var(--color-gray-400);
  }

  .media-error {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 80px;
    color: var(--color-gray-400);
    font-size: 0.8125rem;
  }

  .media-pdf-wrap {
    width: 100%;
    height: 100vh;
    overflow: hidden;
  }

  .media-pdf {
    width: 100%;
    height: 100%;
  }
</style>
