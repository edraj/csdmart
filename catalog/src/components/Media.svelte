<script lang="ts">
    import {ResourceType} from "@edraj/tsdmart";
    import {marked} from "marked";
    import {onMount, onDestroy} from "svelte";

    export let attributes: any = {};
  export let resource_type: ResourceType;
  export let url: string;
  export let displayname: string | undefined = undefined;
  let content_type: string = attributes?.payload?.content_type || "";
  let body: any = attributes?.payload?.body;

  let blobUrl: string | null = null;
  let loading = true;
  let error = false;

  onMount(async () => {
    if (content_type.includes("image") || content_type.includes("video") || content_type.includes("audio") || content_type.includes("pdf")) {
      try {
        const token = localStorage.getItem("authToken");
        const headers: Record<string, string> = {};
        if (token) {
          headers["Authorization"] = `Bearer ${token}`;
        }
        const res = await fetch(url, { headers, credentials: "include" });
        if (res.ok) {
          const blob = await res.blob();
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

{#if resource_type === ResourceType.comment}
  <div>
    <p style="margin: 0px"><b>State:</b>{attributes.state}</p>
    <br />
    <p style="margin: 0px"><b>Body:</b>{attributes.body}</p>
  </div>
{:else if content_type.includes("image")}
  {#if loading}
    <div class="media-loading"><div class="spinner spinner-md"></div></div>
  {:else if error}
    <div class="media-error">Failed to load image</div>
  {:else if blobUrl}
    {#if url.endsWith("svg")}
      <object data={blobUrl} type="image/svg+xml" title={displayname}>
        <img src={blobUrl} alt={displayname || "no-image"} class="media-img" />
      </object>
    {:else}
      <img src={blobUrl} alt={displayname || "no-image"} class="media-img" />
    {/if}
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
    <div class="media-pdf-wrap">
      <object
        title={displayname}
        class="media-pdf"
        type="application/pdf"
        data={blobUrl}
      >
        <p>For some reason PDF is not rendered here properly.</p>
      </object>
    </div>
  {/if}
{:else if ["markdown", "html", "text"].includes(content_type)}
  <div>
    {@html marked(body)}
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
