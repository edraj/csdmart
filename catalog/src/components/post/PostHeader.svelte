<script lang="ts">
  import { _ } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { getAuthorInfo, getPostTitle } from "@/lib/utils/postUtils";

  let { postData, locale }: { postData: any; locale: string } = $props();

  const authorInfo = $derived(getAuthorInfo(postData, $_("common.unknown")));
</script>

<header class="post-header mb-6">
  <div class="author-row">
    <div class="author-avatar-wrapper">
      <div class="author-avatar">
        {authorInfo ? authorInfo.substring(0, 2).toUpperCase() : "U"}
      </div>
    </div>

    <div class="author-details-container">
      <div class="author-identity">
        <span class="author-name">{authorInfo}</span>
        <span class="author-handle"
          >@{authorInfo.toLowerCase().replace(/\s+/g, "")}</span
        >
      </div>
      <div class="post-meta">
        <svg
          class="clock-icon"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
        >
          <path
            stroke-linecap="round"
            stroke-linejoin="round"
            stroke-width="2"
            d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z"
          />
        </svg>
        <span class="post-time">
          {formatDate(postData.created_at, "date", locale) || $_("common.not_available")}
        </span>
        <span class="separator">·</span>
        <span class="folder-badge">
          <svg
            class="folder-icon"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z"
            />
          </svg>
          {postData.payload?.schema_shortname ||
            $_("post_detail.content_type.content")}
        </span>
      </div>
    </div>
  </div>

  <h1 class="post-title break-words">{getPostTitle(postData)}</h1>

  <div class="post-tags">
    {#if postData.tags && postData.tags.length > 0}
      {#each postData.tags as tag (tag)}
        {#if tag && tag.trim()}
          <span class="badge badge-tag">#{tag}</span>
        {/if}
      {/each}
    {/if}
  </div>
</header>

<style>
  .post-header {
    background: transparent;
    padding: 0;
    margin-bottom: 32px;
    border: none;
    box-shadow: none;
  }

  .author-row {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-bottom: 24px;
  }

  .author-avatar-wrapper {
    position: relative;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .author-avatar {
    width: 44px;
    height: 44px;
    border-radius: 50%;
    background-color: #f8fafc;
    color: #0f172a;
    display: flex;
    align-items: center;
    justify-content: center;
    font-weight: 700;
    font-size: 16px;
    border: 1px solid #e2e8f0;
  }


  .author-details-container {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  .author-identity {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 15px;
  }

  .author-name {
    font-weight: 700;
    color: #0f172a;
  }

  .author-handle {
    color: #94a3b8;
    font-weight: 500;
  }

  .post-meta {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    color: #94a3b8;
    font-weight: 500;
  }

  .separator {
    color: #cbd5e1;
    margin: 0 4px;
  }

  .clock-icon,
  .folder-icon,

  .folder-badge {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    background-color: #f1f5f9;
    color: #64748b;
    padding: 2px 8px;
    border-radius: 6px;
    font-size: 12px;
    font-weight: 600;
  }




  .post-title {
    font-size: 32px;
    font-weight: 800;
    color: #0f172a;
    margin: 0 0 16px 0;
    line-height: 1.25;
    letter-spacing: -0.02em;
  }

  .post-tags {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    align-items: center;
  }

  .badge {
    display: inline-flex;
    align-items: center;
    padding: 6px 12px;
    border-radius: 9999px;
    font-size: 13px;
    font-weight: 600;
  }

  .badge-tag {
    background-color: #e0e7ff; /* light blue/indigo */
    color: #4f46e5;
    border: none;
  }
</style>
