<script lang="ts">
  import { goto, params } from "@roxi/routify";
  import {
    checkCurrentUserReactedIdea,
    createComment,
    createReaction,
    deleteReactionComment,
    getEntity,
    getRelatedContents,
  } from "@/lib/dmart_services";
  import { _, locale } from "@/i18n";
  import { derived as derivedStore } from "svelte/store";
  import { ResourceType } from "@edraj/tsdmart/dmart.model";
  import { getCurrentScope } from "@/stores/user";
  import Attachments from "@/components/Attachments.svelte";
  import PostHeader from "@/components/post/PostHeader.svelte";
  import PostContent from "@/components/post/PostContent.svelte";
  import PostInteractions from "@/components/post/PostInteractions.svelte";
  import InteractiveForm from "@/components/post/InteractiveForm.svelte";
  import NestedComments from "@/components/post/NestedComments.svelte";
  import BreadcrumbNavigation from "@/components/navigation/BreadcrumbNavigation.svelte";
  import { user } from "@/stores/user";
  import {
    errorToastMessage,
    successToastMessage,
  } from "@/lib/toasts_messages";
  import { formatDate, formatNumberInText } from "@/lib/helpers";
  import {
    categorizeAttachments,
    generateBreadcrumbs,
    getAuthorInfo,
    getDescription,
    getDisplayName,
  } from "@/lib/utils/postUtils";

  $goto;
  let isLoading = $state(false);
  let postData: any = $state(null);
  let relatedContent = $state<any[]>([]);
  let isLoadingRelated = $state(false);
  let error: any = $state(null);
  let spaceName = $state("");
  let subpath = "";
  let itemShortname = $state("");
  let actualSubpath: any = $state(null);
  let breadcrumbs = $state<any[]>([]);
  let isOwner = $state(false);
  let newComment = $state("");
  let isSubmittingComment = $state(false);
  let isSubmittingReaction = $state(false);
  let userReactionId: any = $state(null);
  let showLoginPrompt = $state(false);
  const isRTL = derivedStore(
    locale,
    ($locale: any) => $locale === "ar" || $locale === "ku",
  );

  $effect(() => {
    isOwner = $user?.shortname === itemShortname;
  });

  let loadToken = 0;

  function initializeContent() {
    spaceName = $params.space_name;
    subpath = $params.subpath;
    itemShortname = $params.shortname;

    actualSubpath = subpath.replace(/-/g, "/");
    breadcrumbs = generateBreadcrumbs(
      spaceName,
      actualSubpath,
      itemShortname,
      $_("post_detail.breadcrumb.catalogs"),
    );

    loadPostData();
  }

  async function loadPostData() {
    const token = ++loadToken;
    isLoading = true;
    error = null;

    try {
      const response = await getEntity(
        itemShortname,
        spaceName,
        actualSubpath,
        $params.resource_type,
        getCurrentScope(),
        true,
      );

      if (token !== loadToken) return;

      if (response && response.uuid) {
        postData = response;
        await Promise.all([
          checkUserReaction(token),
          loadRelatedContent(token, response),
        ]);
      } else {
        console.error("Invalid response structure:", response);
        error = $_("post_detail.error.invalid_response");
        postData = null;
      }
    } catch (err) {
      if (token !== loadToken) return;
      console.error("Error fetching post data:", err);
      error = (err as any).message || $_("post_detail.error.failed_load");
      postData = null;
    } finally {
      if (token === loadToken) isLoading = false;
    }
  }

  async function loadRelatedContent(token?: number, sourcePost?: any) {
    const source = sourcePost ?? postData;
    if (!source) return;

    isLoadingRelated = true;
    try {
      const response = await getRelatedContents(
        spaceName,
        actualSubpath,
        getCurrentScope(),
        source.tags || [],
        source.owner_shortname,
        6,
      );

      if (token !== undefined && token !== loadToken) return;

      if (response?.records) {
        //TODO fix
        relatedContent = []
        // relatedContent = response.records.filter(
        //   (item) => item.shortname !== itemShortname,
        // );
      }
    } catch (err) {
      console.error("Error loading related content:", err);
    } finally {
      if (token === undefined || token === loadToken) isLoadingRelated = false;
    }
  }

  // function navigateToBreadcrumb(path: any) {
  //   if (path) {
  //     $goto(path);
  //   }
  // }

  function goBack() {
    $goto("/catalogs/[space_name]/[subpath]", {
      space_name: spaceName,
      subpath,
    });
  }

  async function handleAddComment(parentCommentId?: string) {
    if (!$user || !$user.shortname) {
      showLoginPrompt = true;
      return;
    }

    if (!newComment.trim()) {
      errorToastMessage($_("post_detail.comments.empty_comment"));
      return;
    }

    isSubmittingComment = true;

    try {
      const success = await createComment(
        spaceName,
        actualSubpath,
        itemShortname,
        newComment.trim(),
        parentCommentId,
      );

      if (success) {
        newComment = "";
        await loadPostData();
      } else {
        errorToastMessage($_("post_detail.comments.add_failed"));
      }
    } catch (error) {
      console.error("Error adding comment:", error);
      errorToastMessage($_("post_detail.comments.add_error"));
    } finally {
      isSubmittingComment = false;
    }
  }

  async function handleToggleReaction() {
    if (!$user || !$user.shortname) {
      showLoginPrompt = true;
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
          userReactionId = null;
          successToastMessage($_("post_detail.reactions.removed_successfully"));
          await loadPostData();
        } else {
          errorToastMessage($_("post_detail.reactions.remove_failed"));
        }
      } else {
        const success = await createReaction(
          itemShortname,
          spaceName,
          actualSubpath,
        );

        if (success) {
          successToastMessage($_("post_detail.reactions.added_successfully"));
          await loadPostData();
        } else {
          errorToastMessage($_("post_detail.reactions.add_failed"));
        }
      }
    } catch (error) {
      console.error("Error toggling reaction:", error);
      errorToastMessage($_("post_detail.reactions.toggle_error"));
    } finally {
      isSubmittingReaction = false;
    }
  }

  async function checkUserReaction(token?: number) {
    if (!$user || !$user.shortname) return;

    try {
      const reactionId = await checkCurrentUserReactedIdea(
        $user.shortname,
        itemShortname,
        spaceName,
        actualSubpath,
      );
      if (token !== undefined && token !== loadToken) return;
      userReactionId = reactionId;
    } catch (error) {
      console.error("Error checking user reaction:", error);
    }
  }

  function closeLoginPrompt() {
    showLoginPrompt = false;
  }

  function goToLogin() {
    $goto("/login");
  }

  function handleRelationshipClick(relationship: any) {
    if (
      relationship.attributes?.role === "editor" &&
      relationship.related_to?.shortname
    ) {
      const editorShortname = relationship.related_to.shortname;
      $goto("/catalogs/[space_name]/[subpath]/[shortname]/[resource_type]", {
        space_name: spaceName,
        subpath: "authors",
        shortname: editorShortname,
        resource_type: ResourceType.content,
      });
    }
  }

  function handleRelatedContentClick(item: any) {
    $goto("/catalogs/[space_name]/[subpath]/[shortname]/[resource_type]", {
      space_name: spaceName,
      subpath: item.subpath,
      shortname: item.shortname,
      resource_type: ResourceType.content,
    });
  }

  let prevParamsKey = "";

  $effect(() => {
    const { shortname, subpath, space_name, resource_type } = $params;

    if (!shortname || !subpath || !space_name) return;

    const key = `${space_name}|${subpath}|${shortname}|${resource_type ?? ""}`;
    if (key === prevParamsKey) return;
    prevParamsKey = key;

    initializeContent();
  });
</script>

<div class="page-container" class:rtl={$isRTL}>
  <BreadcrumbNavigation
    {breadcrumbs}
    onGoBack={goBack}
  />

  <main class="main-content ">
    {#if isLoading}
      <div class="loading-container">
        <div class="loading-content">
          <div class="spinner spinner-lg"></div>
          <p class="loading-text">{$_("post_detail.loading.content")}</p>
        </div>
      </div>
    {:else if error}
      <div class="error-container">
        <div class="error-icon">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
            ></path>
          </svg>
        </div>
        <h3 class="error-title">{$_("post_detail.error.title")}</h3>
      </div>
    {:else if postData}
      {@const { reactions, comments, mediaFiles } =
        categorizeAttachments(postData)}

      <article class="post-card">
        <PostHeader {postData} locale={$locale ?? ""} />

        {#if getDescription(postData, $locale ?? "")}
          <div class="description-section">
            <h3 class="section-title">
              <svg fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path
                  d="M20.59 13.41L10.59 3.41A2 2 0 0 0 9.17 3H4a2 2 0 0 0-2 2v5.17a2 2 0 0 0 .59 1.42l10 10a2 2 0 0 0 2.83 0l5.17-5.17a2 2 0 0 0 0-2.83z"
                />
                <circle cx="7.5" cy="7.5" r="1.5" />
              </svg>
              {$_("post_detail.sections.description")}
            </h3>
            <p class="description-text">{getDescription(postData)}</p>
          </div>
        {/if}
        <PostContent {postData} {spaceName} />

        <PostInteractions
          reactionsCount={reactions.length}
          commentsCount={comments.length}
          {userReactionId}
          {isSubmittingReaction}
          onToggleReaction={handleToggleReaction}
        />
      </article>

      <section class="post-card comments-card mb-6">
        <div class="comments-header-row mb-6">
          <div class="comments-accent-bar"></div>
          <h3 class="comments-title">
            {$_("post_detail.sections.comments", { default: "Comments" })}
          </h3>
          <span class="comments-count-badge">{comments.length}</span>
        </div>

        <InteractiveForm
          bind:newComment
          {isSubmittingComment}
          onAddComment={handleAddComment}
        />

        {#if comments.length > 0}
          <div class="comments-list mt-6">
            <NestedComments
              {comments}
              {spaceName}
              subpath={actualSubpath}
              {itemShortname}
              entryOwnerShortname={postData.owner_shortname}
              onCommentAdded={loadPostData}
            />
          </div>
        {/if}
      </section>

      {#if mediaFiles.length > 0}
        <section class="post-card media-section mb-6">
          <h3 class="section-title-large">
            <span class="title-accent-green"></span>
            {$_("post_detail.media.title", {
              values: {
                count: formatNumberInText(mediaFiles.length, $locale ?? ""),
              },
            })}
          </h3>
          <Attachments
            attachments={mediaFiles}
            resource_type={ResourceType.ticket}
            space_name={spaceName}
            subpath={actualSubpath}
            parent_shortname={itemShortname}
            {isOwner}
          />
        </section>
      {/if}
      {#if postData.relationships && postData.relationships.length > 0}
        <section class="post-card relationships-section mb-6">
          <h3 class="section-title-large">
            <span class="title-accent-purple"></span>
            {$_("post_detail.sections.relationships")}
          </h3>
          <div class="relationships-grid">
            {#each postData.relationships as relationship}
              <button
                aria-label={`View relationship with ${relationship.related_to?.shortname}`}
                class="relationship-item clickable"
                onclick={() => handleRelationshipClick(relationship)}
                disabled={relationship.attributes?.role !== "editor"}
              >
                <div class="relationship-content">
                  <span class="relationship-role">
                    {relationship.attributes?.role ||
                      $_("post_detail.relationships.related")}
                  </span>
                  <span class="relationship-name">
                    {relationship.related_to?.shortname || $_("common.unknown")}
                  </span>
                  {#if relationship.related_to?.space_name}
                    <span class="relationship-space">
                      ({relationship.related_to.space_name})
                    </span>
                  {/if}
                </div>
                <div class="relationship-meta">
                  <span class="relationship-type">
                    {relationship.attributes?.relation || "unknown"}
                  </span>
                  {#if relationship.attributes?.relation === "editor"}
                    <svg
                      class="click-icon"
                      fill="none"
                      stroke="currentColor"
                      viewBox="0 0 24 24"
                    >
                      <path
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        stroke-width="2"
                        d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14"
                      />
                    </svg>
                  {/if}
                </div>
              </button>
            {/each}
          </div>
        </section>
      {/if}

      <!-- Related Content Section -->
      {#if relatedContent.length > 0}
        <section class="related-wrapper mb-6">
          <div class="related-header-row mb-4">
            <div class="related-accent-bar"></div>
            <h3 class="related-title">
              {$_("related_content", { default: "Related Posts" })}
            </h3>
          </div>
          {#if isLoadingRelated}
            <div class="loading-related">
              <div class="spinner spinner-md"></div>
              <p>{$_("loading")}</p>
            </div>
          {:else}
            <div class="related-content-grid">
              {#each relatedContent as item}
                <button
                  aria-label={`View related content: ${getDisplayName(item)}`}
                  class="related-content-card"
                  onclick={() => handleRelatedContentClick(item)}
                >
                  <div class="related-content-header">
                    <h4 class="related-content-title">
                      {getDisplayName(item)}
                    </h4>
                    <svg
                      class="external-link-icon"
                      fill="none"
                      stroke="currentColor"
                      viewBox="0 0 24 24"
                    >
                      <path
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        stroke-width="2"
                        d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14"
                      />
                    </svg>
                  </div>
                  <div class="related-content-meta">
                    <span class="related-content-date">
                      {formatDate(item.attributes?.updated_at)}
                    </span>
                    <span class="related-content-author">
                      {getAuthorInfo(item, $locale ?? "")}
                    </span>
                  </div>
                  {#if item.tags && item.tags.length > 0}
                    <div class="related-content-tags">
                      {#each item.tags.slice(0, 3) as tag}
                        <span class="related-tag">#{tag}</span>
                      {/each}
                      {#if item.tags.length > 3}
                        <span class="tag-more">+{item.tags.length - 3}</span>
                      {/if}
                    </div>
                  {/if}
                </button>
              {/each}
            </div>
          {/if}
        </section>
      {/if}
    {:else}
      <div class="no-data-container">
        <div class="no-data-icon">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M9 13h6m-3-3v6m5 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
            ></path>
          </svg>
        </div>
        <h3 class="no-data-title">{$_("post_detail.no_data.title")}</h3>
        <p class="no-data-message">{$_("post_detail.no_data.message")}</p>
      </div>
    {/if}

    {#if showLoginPrompt}
      <div class="modal-overlay" onclick={closeLoginPrompt} role="presentation">
        <!-- svelte-ignore a11y_click_events_have_key_events -->
        <!-- svelte-ignore a11y_no_static_element_interactions -->
        <div class="login-prompt-modal" onclick={(e) => e.stopPropagation()}>
          <div class="modal-header">
            <h3 class="modal-title">
              {$_("post_detail.login_required.title")}
            </h3>
            <button
              aria-label={$_("route_labels.aria_close_login_prompt")}
              onclick={closeLoginPrompt}
              class="modal-close-button"
            >
              <svg
                class="w-5 h-5"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
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
            <p class="login-prompt-text">
              {$_("post_detail.login_required.message")}
            </p>
            <div class="login-prompt-actions">
              <button onclick={goToLogin} class="login-button">
                {$_("post_detail.login_required.login")}
              </button>
              <button onclick={closeLoginPrompt} class="cancel-button">
                {$_("post_detail.login_required.cancel")}
              </button>
            </div>
          </div>
        </div>
      </div>
    {/if}
  </main>
</div>

<style>
  .page-container {
    min-height: 100vh;
    background: #fafafa;
  }

  .rtl {
    direction: rtl;
  }

  .main-content {
    max-width: 80rem;
    /*max-width: 48rem; !* Centered column layout *!*/
    margin: 0 auto;
    padding: 0 1.5rem 4rem;
  }

  .loading-container {
    display: flex;
    justify-content: center;
    padding: 5rem 0;
  }

  .loading-content {
    text-align: center;
  }

  .loading-text {
    margin-top: 1rem;
    color: #64748b;
    font-weight: 500;
  }

  .error-container {
    text-align: center;
    padding: 5rem 0;
  }

  .rtl .error-container {
    text-align: center;
  }

  .error-icon {
    width: 5rem;
    height: 5rem;
    background: #fee2e2;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0 auto 1.5rem;
    color: #ef4444;
  }

  .error-icon svg {
    width: 2.5rem;
    height: 2.5rem;
  }

  .error-title {
    font-size: 2rem;
    font-weight: 700;
    color: #0f172a;
    margin-bottom: 0.75rem;
  }

  .post-card {
    background: #ffffff;
    border-radius: 12px;
    box-shadow: 0 1px 2px rgba(0, 0, 0, 0.05);
    border: 1px solid #f1f5f9;
    padding: 32px;
    overflow: hidden;
  }

  .mb-6 {
    margin-bottom: 24px;
  }

  .comments-card {
    padding: 24px 32px;
  }

  .comments-header-row {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  .comments-accent-bar {
    width: 4px;
    height: 20px;
    background: #8b5cf6;
    border-radius: 9999px;
  }

  .comments-title {
    font-size: 18px;
    font-weight: 700;
    color: #0f172a;
    margin: 0;
  }

  .comments-count-badge {
    background: #f1f5f9;
    color: #64748b;
    padding: 2px 8px;
    border-radius: 9999px;
    font-size: 12px;
    font-weight: 600;
  }

  .related-header-row {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  .related-accent-bar {
    width: 4px;
    height: 20px;
    background: #ef4444; /* red accent */
    border-radius: 9999px;
  }

  .related-title {
    font-size: 18px;
    font-weight: 700;
    color: #0f172a;
    margin: 0;
  }

  .description-section {
    padding: 1.5rem;
    margin: 0;
  }

  .rtl .description-section {
    text-align: right;
  }

  .section-title {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 1rem;
    font-weight: 600;
    color: #1a202c;
    margin-bottom: 0.75rem;
  }

  .section-title svg {
    width: 1.25rem;
    height: 1.25rem;
    color: #4a5568;
  }

  .description-text {
    color: #4a5568;
    line-height: 1.7;
    margin: 0;
    font-size: 0.95rem;
  }

  .title-accent-green {
    width: 0.25rem;
    height: 1.5rem;
    background: #10b981;
    border-radius: 9999px;
  }

  .title-accent-purple {
    width: 0.25rem;
    height: 1.5rem;
    background: #8b5cf6;
    border-radius: 9999px;
  }

  .section-title-large {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    font-size: 1.25rem;
    font-weight: 700;
    color: #0f172a;
    margin-bottom: 1.5rem;
  }

  .media-section {
    padding: 0 2rem 2rem;
  }

  .rtl .media-section {
    text-align: right;
  }

  .relationships-section {
    padding: 0 2rem 2rem;
  }

  .rtl .relationships-section {
    text-align: right;
  }

  .relationships-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
    gap: 1rem;
  }

  .relationship-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 1rem;
    background: linear-gradient(135deg, #faf5ff 0%, #f3e8ff 100%);
    border-radius: 0.75rem;
    border: 1px solid #c4b5fd;
    width: 100%;
    text-align: left;
    transition: all 0.2s ease;
  }

  .relationship-item.clickable:not(:disabled) {
    cursor: pointer;
  }

  .relationship-item.clickable:not(:disabled):hover {
    background: linear-gradient(135deg, #f3e8ff 0%, #e9d5ff 100%);
    transform: translateY(-1px);
    box-shadow: 0 4px 12px rgba(139, 92, 246, 0.15);
  }

  .relationship-item:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }

  .relationship-content {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    flex: 1;
  }

  .relationship-role {
    display: inline-flex;
    align-items: center;
    padding: 0.25rem 0.75rem;
    border-radius: 9999px;
    font-size: 0.75rem;
    font-weight: 600;
    background: #e9d5ff;
    color: #6b21a8;
  }

  .relationship-name {
    font-size: 0.875rem;
    font-weight: 600;
    color: #0f172a;
  }

  .relationship-space {
    font-size: 0.75rem;
    color: #64748b;
    font-family: "uthmantn", "Courier New", monospace;
  }

  .relationship-meta {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .relationship-type {
    font-size: 0.75rem;
    color: #94a3b8;
    font-weight: 500;
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }

  .click-icon {
    width: 1rem;
    height: 1rem;
    color: #8b5cf6;
  }

  /* Related Content Styles */
  .related-wrapper {
    margin-top: 1rem;
    padding-top: 1rem;
  }

  .loading-related {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1rem;
    padding: 2rem;
    color: #64748b;
  }

  .related-content-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
    gap: 1rem;
  }

  .related-content-card {
    background: #ffffff;
    border: 1px solid #f1f5f9;
    border-left: 3px solid #f472b6; /* slightly pink/red edge accent, similar to design */
    border-radius: 12px;
    padding: 16px;
    text-align: left;
    transition: all 0.2s ease;
    cursor: pointer;
    width: 100%;
  }

  .related-content-card:hover {
    border-color: #cbd5e1;
    transform: translateY(-2px);
    box-shadow:
      0 4px 6px -1px rgba(0, 0, 0, 0.1),
      0 2px 4px -1px rgba(0, 0, 0, 0.06);
  }

  .related-content-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.75rem;
    margin-bottom: 0.75rem;
  }

  .related-content-title {
    font-size: 0.875rem;
    font-weight: 600;
    color: #0f172a;
    line-height: 1.3;
    margin: 0;
    flex: 1;
  }

  .external-link-icon {
    width: 1rem;
    height: 1rem;
    color: #f97316;
    flex-shrink: 0;
    margin-top: 0.125rem;
  }

  .related-content-meta {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-bottom: 0.75rem;
    font-size: 0.75rem;
    color: #64748b;
  }

  .related-content-date,
  .related-content-author {
    font-weight: 500;
  }

  .related-content-tags {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem;
  }

  .related-tag {
    display: inline-flex;
    align-items: center;
    padding: 0.125rem 0.5rem;
    border-radius: 9999px;
    font-size: 0.625rem;
    font-weight: 500;
    background: #f1f5f9;
    color: #475569;
  }

  .tag-more {
    display: inline-flex;
    align-items: center;
    padding: 0.125rem 0.5rem;
    border-radius: 9999px;
    font-size: 0.625rem;
    font-weight: 500;
    background: rgba(107, 114, 128, 0.1);
    color: #6b7280;
  }

  .no-data-container {
    text-align: center;
    padding: 5rem 0;
  }

  .no-data-icon {
    width: 5rem;
    height: 5rem;
    background: #f1f5f9;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0 auto 1.5rem;
    color: #94a3b8;
  }

  .no-data-icon svg {
    width: 2.5rem;
    height: 2.5rem;
  }

  .no-data-title {
    font-size: 2rem;
    font-weight: 700;
    color: #0f172a;
    margin-bottom: 0.75rem;
  }

  .no-data-message {
    color: #64748b;
    margin: 0;
  }

  .modal-overlay {
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
    padding: 1rem;
  }

  .login-prompt-modal {
    background: white;
    border-radius: 0.75rem;
    max-width: 28rem;
    width: 100%;
    box-shadow: 0 20px 25px -5px rgba(0, 0, 0, 0.1);
    overflow: hidden;
  }

  .modal-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 1.5rem;
    border-bottom: 1px solid rgba(148, 163, 184, 0.2);
    background: linear-gradient(135deg, #f8fafc 0%, #f1f5f9 100%);
  }

  .modal-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: #0f172a;
    margin: 0;
  }

  .modal-close-button {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 2rem;
    height: 2rem;
    border-radius: 0.375rem;
    border: 1px solid rgba(148, 163, 184, 0.3);
    background: white;
    color: #64748b;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .modal-close-button:hover {
    background: rgba(248, 250, 252, 0.8);
    color: #374151;
  }

  .modal-body {
    padding: 1.5rem;
  }

  .login-prompt-text {
    color: #64748b;
    line-height: 1.6;
    margin-bottom: 1.5rem;
  }

  .login-prompt-actions {
    display: flex;
    gap: 0.75rem;
    justify-content: flex-end;
  }

  .rtl .login-prompt-actions {
    justify-content: flex-start;
  }

  .login-button {
    padding: 0.75rem 1.5rem;
    background: linear-gradient(135deg, #4f46e5 0%, #7c3aed 100%);
    color: white;
    border: none;
    border-radius: 0.5rem;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s ease;
    box-shadow: 0 2px 4px rgba(79, 70, 229, 0.2);
  }

  .login-button:hover {
    transform: translateY(-1px);
    box-shadow: 0 4px 12px rgba(79, 70, 229, 0.3);
  }

  .cancel-button {
    padding: 0.75rem 1.5rem;
    background: rgba(248, 250, 252, 0.8);
    color: #64748b;
    border: 1px solid rgba(148, 163, 184, 0.3);
    border-radius: 0.5rem;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .cancel-button:hover {
    background: rgba(241, 245, 249, 0.8);
    color: #374151;
  }

  @keyframes spin {
    from {
      transform: rotate(0deg);
    }
    to {
      transform: rotate(360deg);
    }
  }

  /* Mobile Responsive */
  @media (max-width: 768px) {
    .main-content {
      padding: 1rem;
    }

    .media-section,
    .relationships-section {
      padding: 0 1.5rem 1.5rem;
    }

    .relationships-grid,
    .related-content-grid {
      grid-template-columns: 1fr;
    }

    .relationship-item {
      flex-direction: column;
      align-items: flex-start;
      gap: 0.5rem;
    }

    .rtl .relationship-item {
      flex-direction: column;
      align-items: flex-end;
    }
  }
</style>
