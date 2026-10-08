<script lang="ts">
  import { goto as gotoStore, params } from "@roxi/routify";
  import { ResourceType } from "@edraj/tsdmart/dmart.model";
  import { ArrowUpRightFromSquareOutline } from "flowbite-svelte-icons";
  import {
    createComment,
    createReaction,
    deleteReactionComment,
    getEntityStrict,
    getRelatedContents,
  } from "@/lib/dmart_services";
  import { catalogBreadcrumbs, catalogPath, decodeSubpath, withBase } from "@/lib/paths";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { formatNumberInText } from "@/lib/helpers";
  import { renderMarkdown } from "@/lib/markdown";
  import { setTitle } from "@/lib/title";
  import { confirm } from "@/lib/confirm";
  import { toasts } from "@/lib/toast";
  import { findUserReactionId, itemTitle, tagsOf, type CatalogRecord, type Localized } from "@/lib/catalogItems";
  import { categorizeAttachments, getAuthorInfo, getDescription, getPostTitle } from "@/lib/utils/postUtils";
  import { getCurrentScope, user } from "@/stores/user";
  import { website } from "@/config";
  import { AUTHORS_SUBPATH } from "@/lib/constants";
  import Attachments from "@/components/Attachments.svelte";
  import BreadcrumbNavigation from "@/components/navigation/BreadcrumbNavigation.svelte";
  import SkeletonBlock from "@/components/SkeletonBlock.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import Card from "@/components/ui/Card.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import InteractiveForm from "@/components/post/InteractiveForm.svelte";
  import MarkdownBody from "@/components/post/MarkdownBody.svelte";
  import NestedComments from "@/components/post/NestedComments.svelte";
  import PostCardSkeleton from "@/components/post/PostCardSkeleton.svelte";
  import PostContent from "@/components/post/PostContent.svelte";
  import PostHeader from "@/components/post/PostHeader.svelte";
  import PostInteractions from "@/components/post/PostInteractions.svelte";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  // The retrieve-entry shape: attributes flattened onto the record.
  interface Relationship {
    related_to?: { shortname?: string; space_name?: string };
    attributes?: { role?: string; relation?: string };
  }

  interface EntryRecord {
    uuid?: string;
    shortname: string;
    owner_shortname?: string;
    created_at?: string;
    updated_at?: string;
    displayname?: Localized;
    description?: Localized;
    tags?: unknown;
    payload?: { content_type?: string; schema_shortname?: string; body?: unknown };
    attachments?: unknown;
    relationships?: Relationship[];
    space_name?: string;
    [key: string]: unknown;
  }

  let isLoading = $state(false);
  let postData = $state<EntryRecord | null>(null);
  // Fetch a few more than we show so the tag filter has something to pick from.
  const RELATED_FETCH_LIMIT = 12;
  const RELATED_SHOW_LIMIT = 6;
  let relatedContent = $state<CatalogRecord[]>([]);
  let isLoadingRelated = $state(false);
  let error = $state<unknown>(null);
  let spaceName = $state("");
  let itemShortname = $state("");
  let actualSubpath = $state("/");
  let resourceType = $state<string>(ResourceType.content);
  let newComment = $state("");
  let isSubmittingComment = $state(false);
  let isSubmittingReaction = $state(false);

  // The entry's owner, not "a user whose shortname equals the entry's".
  const isOwner = $derived(!!$user?.shortname && $user.shortname === postData?.owner_shortname);

  // The signed-in user's reaction, read from the attachments the entry
  // already carries — no second query for something we were just given.
  const userReactionId = $derived(findUserReactionId(postData?.attachments, $user?.shortname));

  const postTitle = $derived(postData ? getPostTitle(postData) : "");
  const descriptionHtml = $derived(
    postData
      ? renderMarkdown(getDescription(postData, $locale ?? undefined), {
          diagramLabel: $_("post_detail.markdown.diagram_source"),
        })
      : "",
  );
  const attachments = $derived(postData ? categorizeAttachments(postData) : { reactions: [], comments: [], mediaFiles: [] });
  const relationships = $derived(postData?.relationships ?? []);

  // Rebuilt from the route and the locale, so the "Catalogs" crumb follows a
  // language switch.
  const breadcrumbs = $derived(
    spaceName
      ? catalogBreadcrumbs({
          space: spaceName,
          subpath: actualSubpath,
          shortname: itemShortname,
          catalogsLabel: $_("post_detail.breadcrumb.catalogs"),
        })
      : [],
  );

  $effect(() => {
    if (postTitle) setTitle(postTitle, spaceName);
    else if (spaceName) setTitle(spaceName);
  });

  let loadToken = 0;

  function fetchEntry() {
    return getEntityStrict(
      itemShortname,
      spaceName,
      actualSubpath,
      resourceType as ResourceType,
      getCurrentScope(),
      true,
    ) as Promise<EntryRecord | null>;
  }

  async function loadPostData() {
    const token = ++loadToken;
    isLoading = true;
    error = null;

    try {
      const response = await fetchEntry();
      if (token !== loadToken) return;

      if (response && response.uuid) {
        postData = response;
        // The article renders now; related entries arrive underneath it when
        // they are ready and never hold the main content back.
        isLoading = false;
        void loadRelatedContent(token, response);
      } else {
        console.error("Invalid response structure:", response);
        error = $_("post_detail.error.invalid_response");
        postData = null;
      }
    } catch (err) {
      if (token !== loadToken) return;
      console.error("Error fetching post data:", err);
      // ErrorState turns the axios error into a translated category ("not
      // found", "no permission", "offline"), never the raw status text.
      error = err;
      postData = null;
    } finally {
      if (token === loadToken) isLoading = false;
    }
  }

  // After a comment or reaction only the attachments change, so re-fetch the
  // entry and swap those in. The article stays mounted: no spinner over the
  // content and the scroll position is kept.
  async function refreshInteractions() {
    const token = loadToken;
    try {
      const fresh = await fetchEntry();
      if (token !== loadToken || !fresh?.uuid || !postData) return;
      postData = { ...postData, attachments: fresh.attachments };
    } catch (err) {
      console.error("Error refreshing comments and reactions:", err);
    }
  }

  async function loadRelatedContent(token: number, source: EntryRecord) {
    isLoadingRelated = true;
    try {
      const response = await getRelatedContents(
        spaceName,
        actualSubpath,
        getCurrentScope(),
        source.owner_shortname,
        RELATED_FETCH_LIMIT,
      );
      if (token !== loadToken) return;

      if (response?.records) {
        // The query returns the folder's newest entries. Drop the entry itself,
        // then prefer the ones that share a tag with it; when none do, the
        // newest neighbours are still "related" enough to show.
        const tags = Array.isArray(source.tags) ? (source.tags as string[]) : [];
        const others = (response.records as CatalogRecord[]).filter((item) => item.shortname !== itemShortname);
        const sharingTag = tags.length ? others.filter((item) => tagsOf(item).some((t) => tags.includes(t))) : [];
        relatedContent = (sharingTag.length ? sharingTag : others).slice(0, RELATED_SHOW_LIMIT);
      }
    } catch (err) {
      console.error("Error loading related content:", err);
    } finally {
      if (token === loadToken) isLoadingRelated = false;
    }
  }

  function goBack() {
    goto(catalogPath({ space: spaceName, subpath: actualSubpath }));
  }

  // Signing in is a navigation away from the page, so ask first.
  async function promptLogin() {
    const go = await confirm({
      title: $_("post_detail.login_required.title"),
      body: $_("post_detail.login_required.message"),
      confirmLabel: $_("post_detail.login_required.login"),
      cancelLabel: $_("post_detail.login_required.cancel"),
    });
    if (go) goto("/login");
  }

  async function handleAddComment() {
    if (!$user?.shortname) {
      await promptLogin();
      return;
    }
    if (!newComment.trim()) {
      toasts.error($_("post_detail.comments.empty_comment"));
      return;
    }

    isSubmittingComment = true;
    try {
      const success = await createComment(spaceName, actualSubpath, itemShortname, newComment.trim());
      if (success) {
        newComment = "";
        await refreshInteractions();
      } else {
        toasts.error($_("post_detail.comments.add_failed"));
      }
    } catch (err) {
      console.error("Error adding comment:", err);
      toasts.error($_("post_detail.comments.add_error"));
    } finally {
      isSubmittingComment = false;
    }
  }

  async function handleToggleReaction() {
    if (!$user?.shortname) {
      await promptLogin();
      return;
    }

    isSubmittingReaction = true;
    try {
      if (userReactionId) {
        const success = await deleteReactionComment(
          ResourceType.reaction,
          `${actualSubpath}/${itemShortname}`,
          userReactionId,
          spaceName,
        );
        if (success) {
          toasts.success($_("post_detail.reactions.removed_successfully"));
          await refreshInteractions();
        } else {
          toasts.error($_("post_detail.reactions.remove_failed"));
        }
      } else {
        const success = await createReaction(itemShortname, spaceName, actualSubpath);
        if (success) {
          toasts.success($_("post_detail.reactions.added_successfully"));
          await refreshInteractions();
        } else {
          toasts.error($_("post_detail.reactions.add_failed"));
        }
      }
    } catch (err) {
      console.error("Error toggling reaction:", err);
      toasts.error($_("post_detail.reactions.toggle_error"));
    } finally {
      isSubmittingReaction = false;
    }
  }

  function isEditorLink(relationship: Relationship): boolean {
    return relationship.attributes?.role === "editor" && !!relationship.related_to?.shortname;
  }

  function editorHref(relationship: Relationship): string {
    return withBase(
      catalogPath({
        space: spaceName,
        subpath: AUTHORS_SUBPATH,
        shortname: relationship.related_to?.shortname ?? "",
        resourceType: ResourceType.content,
      }),
    );
  }

  function relatedHref(item: CatalogRecord): string {
    return withBase(
      catalogPath({
        space: spaceName,
        subpath: item.subpath || actualSubpath,
        shortname: item.shortname,
        resourceType: item.resource_type ?? ResourceType.content,
      }),
    );
  }

  function relatedAuthor(item: CatalogRecord): string {
    return getAuthorInfo(item, "") || item.attributes?.owner_shortname || $_("common.unknown");
  }

  let prevParamsKey = "";

  $effect(() => {
    const { shortname, subpath, space_name, resource_type } = $params;
    if (!shortname || !subpath || !space_name) return;
    const key = `${space_name}|${subpath}|${shortname}|${resource_type ?? ""}`;
    if (key === prevParamsKey) return;
    prevParamsKey = key;

    spaceName = space_name;
    itemShortname = shortname;
    actualSubpath = decodeSubpath(subpath);
    resourceType = resource_type || ResourceType.content;
    relatedContent = [];
    void loadPostData();
  });

  const number = (n: number) => formatNumberInText(n, $locale ?? "");
</script>

<div class="mx-auto w-full max-w-5xl px-4 sm:px-6 pb-12">
  <BreadcrumbNavigation {breadcrumbs} onGoBack={goBack} />

  <main class="flex flex-col gap-6">
    {#if isLoading}
      <Card padding="none" class="p-6 sm:p-8" aria-busy="true" role="status" aria-label={$_("post_detail.loading.content")}>
        <div class="flex items-center gap-3">
          <SkeletonBlock width="2.75rem" height="2.75rem" radius="var(--radius-full)" />
          <div class="flex-1 space-y-2">
            <SkeletonBlock width="30%" height="0.75rem" radius="var(--radius-full)" />
            <SkeletonBlock width="45%" height="0.625rem" radius="var(--radius-full)" />
          </div>
        </div>
        <SkeletonBlock width="70%" height="1.5rem" radius="var(--radius-full)" class="mt-6" />
        <div class="mt-6 space-y-3">
          <SkeletonBlock width="100%" height="0.75rem" radius="var(--radius-full)" />
          <SkeletonBlock width="95%" height="0.75rem" radius="var(--radius-full)" />
          <SkeletonBlock width="85%" height="0.75rem" radius="var(--radius-full)" />
          <SkeletonBlock width="60%" height="0.75rem" radius="var(--radius-full)" />
        </div>
      </Card>
    {:else if error}
      <ErrorState title={$_("post_detail.error.title")} {error} onRetry={() => void loadPostData()}>
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={goBack}>
          {$_("navigation.go_back")}
        </button>
      </ErrorState>
    {:else if postData}
      <Card padding="none" class="p-6 sm:p-8">
        <article>
          <PostHeader {postData} locale={$locale ?? ""} />

          {#if descriptionHtml}
            <section class="mt-5 rounded-card border border-border bg-surface px-4 py-3" aria-label={$_("post_detail.sections.description")}>
              <MarkdownBody html={descriptionHtml} compact />
            </section>
          {/if}

          <PostContent {postData} {spaceName} />

          {#if website.enable_reaction_and_comment}
            <PostInteractions
              reactionsCount={attachments.reactions.length}
              commentsCount={attachments.comments.length}
              {userReactionId}
              {isSubmittingReaction}
              onToggleReaction={() => void handleToggleReaction()}
            />
          {/if}
        </article>
      </Card>

      {#if website.enable_reaction_and_comment}
        <Card padding="none" class="p-6 sm:p-8">
          <section id="comments" tabindex="-1" aria-labelledby="comments-heading" class="outline-none">
            <div class="flex items-center gap-3 mb-5">
              <h2 id="comments-heading" class="text-lg font-semibold text-text">{$_("post_detail.sections.comments")}</h2>
              <Badge>{number(attachments.comments.length)}</Badge>
            </div>

            <InteractiveForm bind:newComment {isSubmittingComment} onAddComment={() => void handleAddComment()} />

            {#if attachments.comments.length > 0}
              <div class="mt-6">
                <NestedComments
                  comments={attachments.comments}
                  {spaceName}
                  subpath={actualSubpath}
                  {itemShortname}
                  entryOwnerShortname={postData.owner_shortname ?? ""}
                  onCommentAdded={() => void refreshInteractions()}
                />
              </div>
            {/if}
          </section>
        </Card>
      {/if}

      {#if attachments.mediaFiles.length > 0}
        <Card padding="none" class="p-6 sm:p-8">
          <h2 class="text-lg font-semibold text-text mb-5">
            {$_("post_detail.media.title", { values: { count: number(attachments.mediaFiles.length) } })}
          </h2>
          <Attachments
            attachments={attachments.mediaFiles}
            resource_type={ResourceType.ticket}
            space_name={spaceName}
            subpath={actualSubpath}
            parent_shortname={itemShortname}
            {isOwner}
          />
        </Card>
      {/if}

      {#if relationships.length > 0}
        <Card padding="none" class="p-6 sm:p-8">
          <h2 class="text-lg font-semibold text-text mb-5">{$_("post_detail.sections.relationships")}</h2>
          <ul class="grid gap-3 sm:grid-cols-2">
            {#each relationships as relationship, i (i)}
              {@const name = relationship.related_to?.shortname || $_("common.unknown")}
              <li>
                {#snippet relationshipBody()}
                  <div class="flex flex-wrap items-center gap-2 min-w-0">
                    <Badge variant="primary" size="sm">
                      {relationship.attributes?.role || $_("post_detail.relationships.related")}
                    </Badge>
                    <span class="text-sm font-semibold text-text truncate">{name}</span>
                    {#if relationship.related_to?.space_name}
                      <span class="text-xs text-text-muted">({relationship.related_to.space_name})</span>
                    {/if}
                  </div>
                  <div class="flex items-center gap-2 shrink-0 text-xs uppercase tracking-wide text-text-faint">
                    <span>{relationship.attributes?.relation || $_("common.unknown")}</span>
                    {#if isEditorLink(relationship)}
                      <ArrowUpRightFromSquareOutline size="xs" class="text-primary rtl:-scale-x-100" aria-hidden="true" />
                    {/if}
                  </div>
                {/snippet}
                {#if isEditorLink(relationship)}
                  <a
                    href={editorHref(relationship)}
                    class="relationship relationship-link"
                    aria-label={$_("post_detail.relationships.view_aria", { values: { name } })}
                  >
                    {@render relationshipBody()}
                  </a>
                {:else}
                  <div class="relationship">
                    {@render relationshipBody()}
                  </div>
                {/if}
              </li>
            {/each}
          </ul>
        </Card>
      {/if}

      {#if isLoadingRelated || relatedContent.length > 0}
        <section aria-labelledby="related-heading">
          <h2 id="related-heading" class="text-lg font-semibold text-text mb-4">{$_("related_content")}</h2>
          {#if isLoadingRelated && relatedContent.length === 0}
            <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" aria-busy="true" aria-label={$_("ui.loading")}>
              {#each Array.from({ length: 3 }, (_, i) => i) as i (i)}
                <PostCardSkeleton preview={false} />
              {/each}
            </div>
          {:else}
            <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {#each relatedContent as item (`${item.subpath}/${item.shortname}`)}
                {@const tags = tagsOf(item)}
                <Card href={relatedHref(item)} padding="sm" class="h-full">
                  <h3 class="text-sm font-semibold text-text leading-snug break-words">{itemTitle(item, $locale)}</h3>
                  <p class="mt-1.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-xs text-text-muted">
                    <span class="truncate">{relatedAuthor(item)}</span>
                    {#if item.attributes?.updated_at}
                      <time datetime={item.attributes.updated_at}>{formatDate(item.attributes.updated_at, "date", $locale)}</time>
                    {/if}
                  </p>
                  {#if tags.length > 0}
                    <div class="mt-2 flex flex-wrap gap-1">
                      {#each tags.slice(0, 3) as tag (tag)}
                        <Badge size="sm">#{tag}</Badge>
                      {/each}
                      {#if tags.length > 3}
                        <Badge size="sm">{$_("catalog_contents.tags.more", { values: { count: number(tags.length - 3) } })}</Badge>
                      {/if}
                    </div>
                  {/if}
                </Card>
              {/each}
            </div>
          {/if}
        </section>
      {/if}
    {:else}
      <EmptyState title={$_("post_detail.no_data.title")} hint={$_("post_detail.no_data.message")}>
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={goBack}>
          {$_("navigation.go_back")}
        </button>
      </EmptyState>
    {/if}
  </main>
</div>

<style>
  .relationship {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem 0.75rem;
    padding: 0.75rem 1rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-card);
    background: var(--color-surface);
    color: var(--color-text);
    text-decoration: none;
  }

  .relationship-link {
    transition: border-color var(--duration-fast) var(--ease-out), background var(--duration-fast) var(--ease-out);
  }

  .relationship-link:hover {
    border-color: var(--color-primary);
    background: var(--color-primary-soft);
  }
</style>
