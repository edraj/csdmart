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

  // Audio, video and PDF are handed `url` directly instead of being downloaded
  // into a blob first. The payload endpoint already advertises
  // "accept-ranges: bytes" and answers a ranged GET with 206 + content-range,
  // on both the public and managed paths — but a fetch()-into-blob asks for the
  // whole file in one request, so none of that reaches the media element. The
  // element only ever saw a fully-downloaded blob, which is why playback could
  // not begin until the last byte arrived and seeking was impossible before it.
  //
  // Handing over the URL lets the browser's own media stack range-fetch: it
  // reads the header, reports duration, starts playing, and issues fresh Range
  // requests when the user seeks. Measured on four entries in a live archive,
  // authenticated, alternating which strategy ran first so cache warming
  // favoured neither:
  //
  //     6.2 MB  1049 ms -> 97 ms     12.6 MB  2018 ms ->  92 ms
  //     9.9 MB  1712 ms -> 113 ms    12.9 MB  1919 ms ->  95 ms
  //
  // The direct timing is flat in file size because only the header is fetched;
  // the blob timing scales with it, and scales again on a slower link.
  //
  // A media element cannot carry an Authorization header, which is why the blob
  // path existed. It is not needed: dmart accepts the auth_token cookie, and
  // CookieAuthAllowed admits Sec-Fetch-Site: same-origin, which is exactly what
  // a same-origin <audio src> sends. Verified against managed/payload with the
  // cookie alone — 206, content-range bytes 0-99/12965325.
  const streams =
    content_type.includes("audio") ||
    content_type.includes("video") ||
    content_type.includes("pdf");

  // Images keep the download-then-render path. There is nothing to stream, and
  // it preserves the Authorization-header route for a deployment that has
  // cookie auth turned off via CsrfProtectCookieAuth.
  //
  // SVG is retyped so a file whose stored bytes disagree with its declared
  // content_type cannot be handed to the renderer as something else. It is
  // rendered through <img>, where scripting is disabled regardless.
  function pinnedType(ct: string, u: string): string | null {
    if (ct.includes("image") && u.endsWith("svg")) return "image/svg+xml";
    return null;
  }

  let blobUrl: string | null = null;
  let loading = !streams && content_type.includes("image");
  let error = false;

  onMount(async () => {
    if (!content_type.includes("image")) return;
    try {
      const token = localStorage.getItem("authToken");
      const headers: Record<string, string> = {};
      if (token) {
        headers["Authorization"] = `Bearer ${token}`;
      }
      const res = await fetch(url, { headers, credentials: "include" });
      if (res.ok) {
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
  <!-- src={url}, not a blob: the browser range-fetches, so playback starts on
       the header instead of the last byte and seeking works immediately. -->
  <audio controls preload="metadata" src={url}>
    <track kind="captions" />
  </audio>
{:else if content_type.includes("video")}
  <!-- Same as audio: range-fetched by the browser rather than pre-downloaded.
       This matters more here — a video blob would have to land in full before
       a single frame appeared. -->
  <video controls preload="metadata" src={url}>
    <track kind="captions" />
  </video>
{:else if content_type.includes("pdf")}
  <!-- <iframe>, not <object>: object-src stays 'none' because <object> can
       instantiate plugins and execute embedded script, which is a materially
       larger XSS surface than rendering a document.
       src={url} rather than a blob so the viewer can page-stream — the payload
       endpoint answers a ranged GET with 206 (verified: bytes 0-99/36544 on a
       real attachment). The blob type pin that used to guard this path is gone
       with the blob; the browser now honours the server's Content-Type, which
       dmart sets from the same declared content_type and serves with
       X-Content-Type-Options: nosniff. -->
  <div class="media-pdf-wrap">
    <iframe title={displayname} class="media-pdf" src={url}></iframe>
  </div>
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
