<script lang="ts">
  import type { MenuItem } from "@/components/ui/DropdownMenu.svelte";
  import type { AttachmentCounts } from "@/lib/catalogItems";
  import {
    DotsHorizontalOutline,
    FireOutline,
    FolderOutline,
    HeartOutline,
    MessageDotsOutline,
    PaperClipOutline,
  } from "flowbite-svelte-icons";
  import Avatar from "@/components/Avatar.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import Card from "@/components/ui/Card.svelte";
  import DropdownMenu from "@/components/ui/DropdownMenu.svelte";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { formatNumberInText } from "@/lib/helpers";

  // One entry in a browse list (space page, folder page, catalog search).
  //
  // The card itself is a link (<Card href>), so middle-click and "open in new
  // tab" work. Anything else the user can press — the tag chips that filter the
  // list, the "…" share/report menu — is a *sibling* of that link inside the
  // <article>, never nested in it. Tags without an `onTagClick` handler render
  // as plain badges inside the link.
  let {
    href,
    title,
    author,
    avatarUrl = null,
    date,
    folder = null,
    isFolder = false,
    preview = "",
    tags = [],
    selectedTags = [],
    onTagClick,
    maxTags = 3,
    counts,
    hot = false,
    menuItems = [],
    onMenuSelect,
  }: {
    /** Final href (withBase applied). */
    href: string;
    title: string;
    author: string;
    avatarUrl?: string | null;
    /** ISO timestamp shown relative ("5 minutes ago"). */
    date?: string | null;
    /** Folder label shown beside the date; null hides it. */
    folder?: string | null;
    isFolder?: boolean;
    /** Plain-text excerpt (see previewText in lib/catalogItems). */
    preview?: string;
    tags?: string[];
    selectedTags?: string[];
    /** Makes the tags pressable filter chips (rendered outside the link). */
    onTagClick?: (tag: string) => void;
    maxTags?: number;
    /** Comment/reaction/attachment counts; omit to hide the counters. */
    counts?: AttachmentCounts;
    hot?: boolean;
    menuItems?: MenuItem[];
    onMenuSelect?: (id: string) => void;
  } = $props();

  const shownTags = $derived(tags.slice(0, maxTags));
  const extraTags = $derived(Math.max(0, tags.length - maxTags));
  const interactiveTags = $derived(!!onTagClick && tags.length > 0);
  const hasCounters = $derived(!!counts);
  const hasFooter = $derived(interactiveTags || hasCounters);
  const hasMenu = $derived(menuItems.length > 0 && !!onMenuSelect);
  const number = (n: number) => formatNumberInText(n, $locale ?? "");
</script>

<article class="post" class:has-footer={hasFooter}>
  <Card {href} padding="none" class="post-link {hasMenu ? 'has-menu' : ''}">
    <div class="p-4 sm:p-5">
      <header class="flex items-center gap-3 min-w-0">
        <Avatar src={avatarUrl} alt="" size={40} />
        <div class="min-w-0 flex-1">
          <p class="text-sm font-semibold text-text truncate">{author}</p>
          <p class="text-xs text-text-muted flex items-center gap-1.5 flex-wrap">
            {#if date}
              <time datetime={date}>{formatDate(date, "relative", $locale)}</time>
            {/if}
            {#if folder}
              {#if date}<span aria-hidden="true">·</span>{/if}
              <span class="inline-flex items-center gap-1 min-w-0">
                <FolderOutline size="xs" aria-hidden="true" />
                <span class="truncate">{folder}</span>
              </span>
            {/if}
          </p>
        </div>
        {#if hot}
          <Badge variant="warning" size="sm" class="shrink-0">
            <FireOutline size="xs" aria-hidden="true" />
            {$_("space.hot")}
          </Badge>
        {/if}
        {#if isFolder}
          <Badge variant="info" size="sm" class="shrink-0">
            <FolderOutline size="xs" aria-hidden="true" />
            {$_("catalog_contents.card.folder")}
          </Badge>
        {/if}
      </header>

      <h3 class="mt-3 text-base sm:text-lg font-semibold text-text leading-snug break-words" dir="auto">{title}</h3>

      {#if preview}
        <p class="mt-1.5 text-sm text-text-muted leading-relaxed line-clamp-3 break-words" dir="auto">{preview}</p>
      {/if}

      {#if tags.length > 0 && !interactiveTags}
        <div class="mt-3 flex flex-wrap gap-1.5">
          {#each shownTags as tag (tag)}
            <Badge variant="primary" size="sm">#{tag}</Badge>
          {/each}
          {#if extraTags > 0}
            <Badge size="sm">{$_("catalog_contents.tags.more", { values: { count: number(extraTags) } })}</Badge>
          {/if}
        </div>
      {/if}
    </div>
  </Card>

  {#if hasMenu}
    <div class="post-menu">
      <DropdownMenu
        label={$_("catalog_contents.card.actions_aria", { values: { title } })}
        items={menuItems}
        onSelect={(id) => onMenuSelect?.(id)}
        align="end"
      >
        {#snippet trigger()}
          <DotsHorizontalOutline size="sm" aria-hidden="true" />
        {/snippet}
      </DropdownMenu>
    </div>
  {/if}

  {#if hasFooter}
    <div class="post-footer">
      {#if interactiveTags}
        <div class="flex flex-wrap gap-1.5 min-w-0">
          {#each shownTags as tag (tag)}
            {@const selected = selectedTags.includes(tag)}
            <button
              type="button"
              class="tag-chip"
              class:selected
              aria-pressed={selected}
              aria-label={$_("catalog_contents.tags.filter_by_tag_aria", { values: { tag } })}
              onclick={() => onTagClick?.(tag)}
            >
              #{tag}
            </button>
          {/each}
          {#if extraTags > 0}
            <Badge size="sm">{$_("catalog_contents.tags.more", { values: { count: number(extraTags) } })}</Badge>
          {/if}
        </div>
      {/if}

      {#if counts}
        <div class="counters ms-auto">
          <span class="counter" title={$_("catalog_contents.card.comments_aria", { values: { count: number(counts.comments) } })}>
            <MessageDotsOutline size="sm" aria-hidden="true" />
            <span aria-hidden="true">{number(counts.comments)}</span>
            <span class="sr-only">{$_("catalog_contents.card.comments_aria", { values: { count: number(counts.comments) } })}</span>
          </span>
          <span class="counter" title={$_("catalog_contents.card.reactions_aria", { values: { count: number(counts.reactions) } })}>
            <HeartOutline size="sm" aria-hidden="true" />
            <span aria-hidden="true">{number(counts.reactions)}</span>
            <span class="sr-only">{$_("catalog_contents.card.reactions_aria", { values: { count: number(counts.reactions) } })}</span>
          </span>
          {#if counts.media > 0}
            <span class="counter" title={$_("catalog_contents.card.media_aria", { values: { count: number(counts.media) } })}>
              <PaperClipOutline size="sm" aria-hidden="true" />
              <span aria-hidden="true">{number(counts.media)}</span>
              <span class="sr-only">{$_("catalog_contents.card.media_aria", { values: { count: number(counts.media) } })}</span>
            </span>
          {/if}
        </div>
      {/if}
    </div>
  {/if}
</article>

<style>
  .post {
    position: relative;
    border-radius: var(--radius-card);
    transition: box-shadow var(--duration-normal) var(--ease-out);
  }

  .post:hover {
    box-shadow: var(--shadow-modal);
  }

  /* The link's own hover shadow would paint under the footer strip; the
     article carries the lift instead so link and footer rise as one card. */
  .post :global(.post-link:hover) {
    box-shadow: var(--shadow-card);
  }

  .post :global(.post-link.has-menu > div) {
    padding-inline-end: 3.5rem;
  }

  .post.has-footer :global(.post-link) {
    border-end-start-radius: 0;
    border-end-end-radius: 0;
    border-bottom: 0;
  }

  .post-menu {
    position: absolute;
    top: 0.75rem;
    inset-inline-end: 0.75rem;
    z-index: 1;
  }

  .post-footer {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem 0.75rem;
    padding: 0.625rem 1rem 0.75rem;
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-top: 1px solid var(--color-border);
    border-end-start-radius: var(--radius-card);
    border-end-end-radius: var(--radius-card);
  }

  .post:hover .post-footer {
    border-color: var(--color-border-strong);
  }

  .tag-chip {
    display: inline-flex;
    align-items: center;
    padding: 0.125rem 0.625rem;
    border-radius: var(--radius-full);
    border: 1px solid var(--color-border);
    background: var(--color-surface-2);
    color: var(--color-text-muted);
    font-size: var(--font-size-xs);
    font-weight: var(--font-weight-medium);
    line-height: 1.5;
    cursor: pointer;
    transition: background var(--duration-fast) var(--ease-out), color var(--duration-fast) var(--ease-out),
      border-color var(--duration-fast) var(--ease-out);
  }

  .tag-chip:hover {
    background: var(--color-surface-3);
    color: var(--color-text);
  }

  .tag-chip.selected {
    background: var(--color-primary-soft);
    border-color: var(--color-primary);
    color: var(--color-primary);
  }

  .counters {
    display: inline-flex;
    align-items: center;
    gap: 1rem;
    color: var(--color-text-muted);
    font-size: var(--font-size-sm);
    font-variant-numeric: tabular-nums;
  }

  .counter {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
  }
</style>
