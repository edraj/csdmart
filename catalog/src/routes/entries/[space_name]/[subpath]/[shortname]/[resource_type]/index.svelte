<script lang="ts">
  import { goto as gotoStore, params } from "@roxi/routify";
  import { diagrams } from "@/lib/diagrams";
  import { onMount } from "svelte";
  import { sanitizeHtml } from "@/lib/utils/sanitize";
  import {
    checkCurrentUserReactedIdea,
    createComment,
    createReaction,
    deleteEntity,
    deleteReactionComment,
    getEntity,
  } from "@/lib/dmart_services";
  import { formatNumberInText } from "@/lib/helpers";
  import Attachments from "@/components/Attachments.svelte";
  import BreadcrumbNavigation from "@/components/navigation/BreadcrumbNavigation.svelte";
  import { catalogBreadcrumbs, decodeSubpath } from "@/lib/paths";
  import { ResourceType, DmartScope } from "@edraj/tsdmart";
  import { user } from "@/stores/user";
  import {
    errorToastMessage,
    successToastMessage,
  } from "@/lib/toasts_messages";
  import Avatar from "@/components/Avatar.svelte";
  import {
    CheckCircleSolid,
    ClockOutline,
    CloseCircleSolid,
    EditOutline,
    EyeSlashSolid,
    EyeSolid,
    HeartSolid,
    MessagesSolid,
    TagOutline,
    TrashBinOutline,
    TrashBinSolid,
    UserCircleOutline,
  } from "flowbite-svelte-icons";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { renderMarkdown } from "@/lib/markdown";
  import { confirm } from "@/lib/confirm";
  import JsonViewer from "@/components/JsonViewer.svelte";
  import { getTemplate } from "@/lib/dmart_services/templates";
  import {
    bodyAs,
    bodyObject,
    type EntryDetail,
    type JsonObject,
    type TemplateBody as TemplateEntryBody,
    type TemplateInstanceBody,
  } from "@/lib/types";
  import { isJsonValue } from "@/components/json-table/types";
  import { getAvatarsCached } from "@/lib/dmart_services/avatars";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import LoadingState from "@/components/ui/LoadingState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  let entity = $state<EntryDetail | null>(null);
  let isLoading = $state(false);
  let isLoadingPage: boolean = $state(true);
  let isOwner = $state(false);
  /** The shortname of the current user's reaction on this entry, when they left one. */
  let userReactionEntry = $state<string | null>(null);
  let counts = $state({ reaction: 0, reply: 0, comment: 0, media: 0 });
  // One cached lookup per distinct commenter instead of an {#await} per row.
  let commentAvatars = $state<Map<string, string | null>>(new Map());

  $effect(() => {
    if (entity) setTitle(getLocalizedDisplayName(entity), $params.space_name);
  });

  // Template rendering state
  let templateRenderedContent = $state("");
  let isLoadingTemplate = $state(false);
  let templateError = $state("");
  let loadedTemplateKey: string = $state(""); // Track which template was loaded

  // A template-based entry's body: which template and the values for it.
  const templateBody = $derived(bodyAs<TemplateInstanceBody>(entity?.payload));

  // Check if this is a template-based entry
  const isTemplateEntry = $derived(
    entity?.payload?.schema_shortname === "templates" &&
    !!templateBody?.template &&
    !!templateBody?.data
  );

  // Generate a unique key for the current template entry to prevent duplicate loads
  const currentTemplateKey = $derived(
    isTemplateEntry && $params.space_name
      ? `${$params.space_name}-${templateBody?.template}`
      : ""
  );


  onMount(async () => {
    isLoadingPage = true;
    await refreshIdea();
    isOwner = $user.shortname === entity?.owner_shortname;
    await refreshCounts();
    isLoadingPage = false;
  });

  function handleEdit(entity: EntryDetail) {
    goto("/entries/[space_name]/[subpath]/[shortname]/[resource_type]/edit", {
      shortname: entity.shortname,
      space_name: $params.space_name,
      subpath: $params.subpath,
      resource_type: $params.resource_type,
    });
  }

  let comment = $state("");

  async function handleAddComment() {
    if (comment) {
      const response = await createComment(
        $params.space_name,
        $params.subpath,
        $params.shortname,
        comment,
      );
      if (response) {
        await refreshCounts();
        await refreshIdea();
        successToastMessage($_("entry_detail.comments.add_success"));
        comment = "";
        await refreshIdea();
      } else {
        errorToastMessage($_("entry_detail.comments.add_error"));
      }
    }
  }

  async function deleteComment(shortname: string) {
    if (!entity) return;
    const response = await deleteReactionComment(
      ResourceType.comment,
      `${$params.subpath}/${entity.shortname}`,
      shortname,
      $params.space_name,
    );

    if (response) {
      await refreshCounts();
      await refreshIdea();
      successToastMessage($_("entry_detail.comments.delete_success"));
    } else {
      errorToastMessage($_("entry_detail.comments.delete_error"));
    }
  }

  async function handleReaction() {
    if (!entity) return;
    if (userReactionEntry) {
      const response = await deleteReactionComment(
        ResourceType.reaction,
        `${$params.subpath}/${entity.shortname}`,
        userReactionEntry,
        $params.space_name,
      );
      if (response) {
        userReactionEntry = null;
        await refreshCounts();
        await refreshIdea();
        successToastMessage($_("entry_detail.reactions.remove_success"));
      } else {
        errorToastMessage($_("entry_detail.reactions.remove_error"));
      }
    } else {
      const response = await createReaction(
        entity.shortname,
        $params.space_name,
        $params.subpath,
      );
      if (response) {
        await refreshCounts();
        await refreshIdea();
        successToastMessage($_("entry_detail.reactions.add_success"));
      } else {
        errorToastMessage($_("entry_detail.reactions.add_error"));
      }
    }
  }

  async function handleDeleteItem(entity: EntryDetail) {
    const confirmed = await confirm({
      title: $_("admin_item_detail.delete_modal.title"),
      body: $_("admin_item_detail.delete_modal.message", {
        values: { name: entity.shortname },
      }),
      variant: "danger",
    });
    if (!confirmed) return;

    try {
      const success = await deleteEntity(
        entity.shortname,
        $params.space_name,
        $params.subpath,
        $params.resource_type,
      );

      if (success) {
        goto("/entries");
      }
    } catch (err) {
      log.error("Error deleting item:", err);
    }
  }

  async function refreshIdea() {
    entity = await getEntity(
      $params.shortname,
      $params.space_name,
      $params.subpath,
      $params.resource_type,
      DmartScope.managed,
    );
    if (entity) {
      counts = {
        reaction: entity.attachments?.reaction?.length || 0,
        reply: entity.attachments?.comment?.length || 0,
        comment: entity.attachments?.comment?.length || 0,
        media: entity.attachments?.media?.length || 0,
      };

      const commenters = (entity.attachments?.comment ?? []).map((c) => c.attributes?.owner_shortname);
      commentAvatars = await getAvatarsCached(commenters);

      userReactionEntry = await checkCurrentUserReactedIdea(
        $user.shortname ?? "",
        entity.shortname,
        $params.space_name,
        $params.subpath,
      );

      // Load template content if this is a template-based entry
      const instance = bodyAs<TemplateInstanceBody>(entity.payload);
      if (entity.payload?.schema_shortname === "templates" && instance?.template && instance.data) {
        const contentKey = `${$params.space_name}-${instance.template}-${Object.values(instance.data).join(',')}`;
        await loadTemplateContent(contentKey);
      }
    }
  }

  async function refreshCounts() {
    if (entity) {
      counts = {
        reaction: entity.attachments?.reaction?.length || 0,
        reply: entity.attachments?.comment?.length || 0,
        comment: entity.attachments?.comment?.length || 0,
        media: entity.attachments?.media?.length || 0,
      };
    }
  }

  // Load and render template content
  async function loadTemplateContent(contentKey?: string) {
    if (!isTemplateEntry || isLoadingTemplate) return;

    // Prevent duplicate loads of the same template
    const keyToUse = contentKey || currentTemplateKey;
    if (keyToUse === loadedTemplateKey) return;

    isLoadingTemplate = true;
    templateError = "";

    try {
      const templateShortname = templateBody?.template ?? "";
      const templateData = templateBody?.data ?? {};
      
      // Try to get template from current space first
      let template = await getTemplate($params.space_name, templateShortname, DmartScope.managed);
      
      // If not found in current space, try applications space
      if (!template) {
        template = await getTemplate("applications", templateShortname, DmartScope.managed);
      }
      
      if (!template) {
        templateError = $_("entry_detail.template.not_found", { values: { name: templateShortname } });
        templateRenderedContent = "";
        return;
      }
      
      // Get the template content. A retrieved entry is flat: its payload sits
      // at the top level (no `attributes` wrapper, which only query records have).
      const content = bodyAs<TemplateEntryBody>(template.payload)?.content || "";
      
      // Replace placeholders with data
      const renderedContent = renderTemplateWithData(content, templateData);
      
      // Parse markdown to HTML
      templateRenderedContent = renderMarkdown(renderedContent);
      
      // Mark this template as loaded to prevent duplicate loads
      loadedTemplateKey = keyToUse;
    } catch (err) {
      log.error("Error loading template:", err);
      templateError = $_("entry_detail.template.load_failed");
    } finally {
      isLoadingTemplate = false;
    }
  }
  
  function renderTemplateWithData(templateContent: string, data: JsonObject): string {
    if (!templateContent || !data) return templateContent;
    
    let result = templateContent;
    
    // Replace {{fieldName:type}} patterns with actual data
    const placeholderRegex = /\{\{(\w+)(?::(\w+))?\}\}/g;
    
    result = result.replace(placeholderRegex, (match, fieldName) => {
      const value = data[fieldName];
      
      if (value === undefined || value === null) {
        return match; // Keep placeholder if data not found
      }
      
      return String(value);
    });
    
    return result;
  }

  function getStatusInfo(entity: EntryDetail) {
    if (!entity.is_active) {
      return {
        text: $_("entry_detail.status.draft"),
        class: "status-draft",
        icon: EyeSlashSolid,
        description: $_("entry_detail.status.draft_description"),
      };
    } else if (entity.state === "pending") {
      return {
        text: $_("entry_detail.status.pending"),
        class: "status-pending",
        icon: ClockOutline,
        description: $_("entry_detail.status.pending_description"),
      };
    } else if (entity.state === "approved") {
      return {
        text: $_("entry_detail.status.published"),
        class: "status-published",
        icon: CheckCircleSolid,
        description: $_("entry_detail.status.published_description"),
      };
    } else if (entity.state === "rejected") {
      return {
        text: $_("entry_detail.status.rejected"),
        class: "status-rejected",
        icon: CloseCircleSolid,
        description: $_("entry_detail.status.rejected_description"),
      };
    } else {
      return {
        text: $_("entry_detail.status.active"),
        class: "status-active",
        icon: EyeSolid,
        description: $_("entry_detail.status.active_description"),
      };
    }
  }

  function getLocalizedDisplayName(entity: EntryDetail | null): string {
    if (!entity?.displayname)
      return entity?.shortname || $_("entry_detail.untitled");

    const displayname = entity.displayname;
    if (($locale ?? "") === "ar" && displayname.ar) return displayname.ar;
    if (($locale ?? "") === "ku" && displayname.ku) return displayname.ku;
    if (($locale ?? "") === "en" && displayname.en) return displayname.en;

    return (
      displayname.ar ||
      displayname.en ||
      displayname.ku ||
      entity.shortname ||
      $_("entry_detail.untitled")
    );
  }

  function renderContent(entity: EntryDetail): string {
    if (!entity?.payload?.body) {
      return $_("entry_detail.no_content");
    }

    const contentType = entity.payload.content_type;
    const body = entity.payload.body;

    // Handle template-based entries
    if (isTemplateEntry && templateRenderedContent) {
      return templateRenderedContent; // Already parsed HTML
    }

    if (contentType === "html") {
      return typeof body === "string" ? body : String(body);
    } else if (contentType === "markdown" || contentType === "md") {
      return renderMarkdown(typeof body === "string" ? body : String(body));
    } else if (contentType === "json") {
      // Return a placeholder for JSON - will be rendered by JsonViewer
      return "__JSON_CONTENT__";
    } else {
      // plain text or unknown type — render safely
      return typeof body === "string"
        ? body
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/\n/g, "<br>")
        : String(body);
    }
  }
</script>

{#if isLoadingPage}
  <div class="page-container">
    <div class="content-wrapper">
      <LoadingState label={$_("entry_detail.loading")} />
    </div>
  </div>
{:else if entity}
  {@const current = entity}
  <div class="page-container">
    <div class="content-wrapper">
      <BreadcrumbNavigation
        breadcrumbs={catalogBreadcrumbs({
          space: $params.space_name,
          subpath: decodeSubpath($params.subpath),
          shortname: $params.shortname,
          catalogsLabel: $_("post_detail.breadcrumb.catalogs"),
        })}
        onGoBack={() =>
          goto(`/catalogs/${$params.space_name}/${$params.subpath}`)}
      />

      {#if isOwner}
        <div class="entry-actions">
          <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={() => handleEdit(current)}>
            <EditOutline size="sm" aria-hidden="true" />
            {$_("entry_detail.edit_entry")}
          </button>
          <button type="button" class="app-btn app-btn-danger app-btn-sm" onclick={() => handleDeleteItem(current)}>
            <TrashBinOutline size="sm" aria-hidden="true" />
            {$_("entry_detail.delete_entry")}
          </button>
        </div>
      {/if}

      <!-- Status Banner -->
      <div class="status-banner">
        <div class="status-icon">
          {#if entity}
            {#key entity.state}
              {#if getStatusInfo(entity).icon}
                {@const SvelteComponent = getStatusInfo(entity).icon}
                <SvelteComponent class="w-6 h-6" />
              {/if}
            {/key}
          {/if}
        </div>
        <div class="status-info">
          <div class="status-header">
            <span class="status-badge {getStatusInfo(entity).class}">
              {getStatusInfo(entity).text}
            </span>
            <span class="created-date">
              {$_("entry_detail.created")}
              {formatDate(entity.created_at, "datetime", $locale)}
            </span>
          </div>
          <p class="status-description">
            {getStatusInfo(entity).description}
          </p>
        </div>
      </div>

      <!-- Main Content -->
      <div class="main-card">
        <!-- Title -->
        <h1 class="entry-title">
          {getLocalizedDisplayName(entity)}
        </h1>

        <!-- Tags -->
        {#if entity.tags && entity.tags.length > 0}
          <div class="tags-section">
            <h3 class="section-title">
              <TagOutline class="w-5 h-5" />
              {$_("entry_detail.tags")}
            </h3>
            <div class="tags-container">
              {#each entity.tags as tag (tag)}
                <span class="tag">
                  <TagOutline class="w-3 h-3" />
                  {tag}
                </span>
              {/each}
            </div>
          </div>
        {/if}

        <!-- Relationships -->
        {#if entity.relationships && entity.relationships.length > 0}
          <div class="relationships-section">
            <h3 class="section-title">
              <UserCircleOutline class="w-5 h-5" />
              {$_("entry_detail.contributors")}
            </h3>
            <div
              class="relationships-container"
            >
              {#each entity.relationships as relationship, i (i)}
                <div class="relationship-item">
                  <span class="relationship-role"
                    >{relationship.attributes?.relation}:</span
                  >
                  <span class="relationship-name"
                    >{relationship.related_to?.shortname}</span
                  >
                </div>
              {/each}
            </div>
          </div>
        {/if}

        <!-- Content -->
        <div class="entry-content prose max-w-none" use:diagrams={entity}>
          {#if isTemplateEntry}
            {#if isLoadingTemplate}
              <LoadingState label={$_("entry_detail.template.loading")} />
            {:else if templateError}
              <div class="template-error">
                <ErrorState compact message={templateError} />
                <div class="fallback-data">
                  <h4>{$_("templates._val")}: {templateBody?.template}</h4>
                  <dl>
                    {#each Object.entries(templateBody?.data ?? {}) as [key, value] (key)}
                      <dt>{key}:</dt>
                      <dd>{value}</dd>
                    {/each}
                  </dl>
                </div>
                <pre class="fallback-content">{JSON.stringify(entity.payload?.body, null, 2)}</pre>
              </div>
            {:else}
              {@html sanitizeHtml(renderContent(entity))}
            {/if}
          {:else if entity?.payload?.content_type === "json"}
            <JsonViewer
              data={isJsonValue(entity.payload.body) ? entity.payload.body : null}
              title={getLocalizedDisplayName(entity)}
              schemaShortname={entity.payload?.schema_shortname}
              spaceName={$params.space_name}
            />
          {:else}
            {@html sanitizeHtml(renderContent(entity))}
          {/if}
        </div>

        <!-- Attachments -->
        {#if (entity.attachments?.media?.length ?? 0) > 0}
          <div class="attachments-section">
            <h3 class="section-title">
              {$_("entry_detail.attachments")}
            </h3>
            <Attachments
              resource_type={ResourceType.ticket}
              space_name={$params.space_name}
              subpath={$params.subpath}
              parent_shortname={entity.shortname}
              attachments={entity.attachments?.media ?? []}
              {isOwner}
            />
          </div>
        {/if}

        <!-- Actions -->
        <div class="actions-section">
          <button
            aria-label={$_("entry_detail.actions.like")}
            class="like-button {userReactionEntry ? 'liked' : ''}"
            onclick={handleReaction}
            disabled={isLoading}
          >
            <HeartSolid class="w-5 h-5" />
            {userReactionEntry
              ? $_("entry_detail.actions.unlike")
              : $_("entry_detail.actions.like")} ({formatNumberInText(
              counts.reaction,
              $locale ?? "",
            ) || 0})
          </button>
        </div>
      </div>

      <!-- Comments Section -->
      <div class="comments-section">
        <h3 class="comments-title">
          <MessagesSolid class="w-6 h-6" />
          {$_("entry_detail.comments.title")} ({formatNumberInText(
            counts.reply,
            $locale ?? "",
          ) || 0})
        </h3>

        <!-- Add Comment -->
        <div class="comment-form">
          <div class="comment-input-container">
            <div class="comment-input-wrapper">
              <label for="comment-input" class="visually-hidden"></label>
              <input
                type="text"
                bind:value={comment}
                placeholder={$_("entry_detail.comments.placeholder")}
                class="comment-input"
                onkeydown={(e) => {
                  if (e.key === "Enter" && !e.shiftKey) {
                    e.preventDefault();
                    handleAddComment();
                  }
                }}
              />
              <button
                class="comment-submit"
                onclick={handleAddComment}
                disabled={!comment.trim() || isLoading}
                aria-label={$_("entry_detail.comments.submit")}
              >
                <svg
                  class="w-4 h-4"
                  version="1.1"
                  id="Layer_1"
                  xmlns="http://www.w3.org/2000/svg"
                  xmlns:xlink="http://www.w3.org/1999/xlink"
                  viewBox="0 0 512 512"
                  xml:space="preserve"
                  fill="var(--color-text)"
                >
                  <g id="SVGRepo_bgCarrier" stroke-width="0"></g>
                  <g
                    id="SVGRepo_tracerCarrier"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  ></g>
                  <g id="SVGRepo_iconCarrier">
                    <polygon
                      style="fill:var(--color-primary);"
                      points="490.452,21.547 16.92,235.764 179.068,330.053 179.068,330.053 "
                    ></polygon>
                    <polygon
                      style="fill:var(--color-primary-hover);"
                      points="490.452,21.547 276.235,495.079 179.068,330.053 179.068,330.053 "
                    ></polygon>
                    <rect
                      x="257.137"
                      y="223.122"
                      transform="matrix(-0.7071 -0.7071 0.7071 -0.7071 277.6362 609.0793)"
                      style="fill:var(--color-surface-2);"
                      width="15.652"
                      height="47.834"
                    ></rect>
                    <path
                      style="fill:var(--color-text);"
                      d="M0,234.918l174.682,102.4L277.082,512L512,0L0,234.918z M275.389,478.161L190.21,332.858 l52.099-52.099l-11.068-11.068l-52.099,52.099L33.839,236.612L459.726,41.205L293.249,207.682l11.068,11.068L470.795,52.274 L275.389,478.161z"
                    ></path>
                  </g>
                </svg>
              </button>
            </div>
          </div>
        </div>

        <!-- Comments List -->
        {#if (entity.attachments?.comment?.length ?? 0) > 0}
          <div class="comments-list">
            {#each entity.attachments?.comment ?? [] as reply (reply.shortname)}
              <div class="comment-item">
                <div class="comment-avatar">
                  <Avatar src={commentAvatars.get(reply.attributes.owner_shortname ?? "")} size="40" />
                </div>
                <div class="comment-content">
                  <div class="comment-header">
                    <span class="comment-author">
                      {                      reply.attributes?.displayname?.[$locale ?? ""] ||
                        reply.attributes?.displayname?.en ||
                        reply.attributes?.displayname?.ar ||
                        reply.attributes?.owner_shortname}
                    </span>
                    <span class="comment-date">
                      {formatDate(reply.attributes.created_at, "datetime", $locale)}
                    </span>
                    {#if reply.attributes.owner_shortname === $user.shortname}
                      <button
                        aria-label={$_("entry_detail.comments.delete_comment")}
                        class="delete-comment"
                        onclick={() => deleteComment(reply.shortname)}
                      >
                        <TrashBinSolid class="w-3 h-3" aria-hidden="true" />
                      </button>
                    {/if}
                  </div>
                  <p class="comment-text">
                    {bodyObject(reply.attributes.payload)?.embedded ||
                      bodyObject(reply.attributes.payload)?.body ||
                      $_("entry_detail.no_content")}
                  </p>
                </div>
              </div>
            {/each}
          </div>
        {:else}
          <div class="no-comments">
            <MessagesSolid class="w-12 h-12 no-comments-icon" />
            <p class="no-comments-title">
              {$_("entry_detail.comments.no_comments")}
            </p>
            <p class="no-comments-subtitle">
              {$_("entry_detail.comments.be_first")}
            </p>
          </div>
        {/if}
      </div>
    </div>
  </div>
{:else}
  <div class="page-container">
    <div class="content-wrapper">
      <ErrorState title={$_("entry_detail.error.not_found_title")} message={$_("entry_detail.error.not_found_message")}>
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={() => goto(`/catalogs/${$params.space_name}/${$params.subpath}`)}>
          {$_("entry_detail.back_to_folder")}
        </button>
      </ErrorState>
    </div>
  </div>
{/if}

<style>

  .page-container {
    min-height: 100vh;
    background: linear-gradient(135deg, var(--color-surface) 0%, var(--color-border) 100%);
    padding: 2rem 1rem;
  }

  .content-wrapper {
    max-width: 800px;
    margin: 0 auto;
  }

  .loading-container {
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
    background: linear-gradient(135deg, var(--color-surface) 0%, var(--color-border) 100%);
  }

  .loading-content {
    text-align: center;
  }

  .loading-text {
    color: var(--color-text-muted);
    margin-top: 1rem;
    font-size: 1.125rem;
  }

  .entry-actions {
    display: flex;
    justify-content: flex-end;
    margin-bottom: 2rem;
  }

  .header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 2rem;
  }

  .back-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 12px;
    color: var(--color-text-muted);
    font-weight: 500;
    transition: all 0.2s ease;
    cursor: pointer;
  }

  .back-button:hover {
    background: var(--color-surface);
    border-color: var(--color-border-strong);
    color: var(--color-text-muted);
  }

  .edit-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    background: linear-gradient(135deg, var(--color-primary) 0%, var(--color-primary-hover) 100%);
    border: none;
    border-radius: 12px;
    color: white;
    font-weight: 500;
    transition: all 0.2s ease;
    cursor: pointer;
  }

  .edit-button:hover {
    background: linear-gradient(135deg, var(--color-primary-hover) 0%, var(--color-info) 100%);
    transform: translateY(-1px);
  }

  .delete-button {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    background: linear-gradient(135deg, var(--color-danger) 0%, var(--color-danger-hover) 100%);
    border: none;
    border-radius: 12px;
    color: white;
    font-weight: 500;
    transition: all 0.2s ease;
    cursor: pointer;
  }

  .delete-button:hover {
    background: linear-gradient(135deg, var(--color-danger) 0%, var(--color-danger) 100%);
    transform: translateY(-1px);
  }

  .delete-button:active {
    transform: translateY(0);
  }

  .status-banner {
    display: flex;
    align-items: center;
    gap: 1rem;
    padding: 1.5rem;
    background: var(--color-surface);
    border-radius: 16px;
    box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
    margin-bottom: 2rem;
  }

  .status-icon {
    width: 48px;
    height: 48px;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--color-surface-3);
    border-radius: 12px;
    color: var(--color-primary);
  }

  .status-info {
    flex: 1;
  }

  .status-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-bottom: 0.5rem;
  }

  .status-badge {
    padding: 0.25rem 0.75rem;
    border-radius: 20px;
    font-size: 0.875rem;
    font-weight: 500;
  }

  .status-draft {
    background: var(--color-surface-3);
    color: var(--color-text-muted);
  }

  .status-pending {
    background: var(--color-warning-soft);
    color: var(--color-warning);
  }

  .status-published {
    background: var(--color-success-soft);
    color: var(--color-success);
  }

  .status-rejected {
    background: var(--color-danger-soft);
    color: var(--color-danger);
  }

  .status-active {
    background: var(--color-info-soft);
    color: var(--color-primary-hover);
  }

  .created-date {
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }

  .status-description {
    color: var(--color-text-muted);
    font-size: 0.875rem;
    line-height: 1.4;
  }

  .main-card {
    background: var(--color-surface);
    border-radius: 16px;
    box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
    padding: 2rem;
    margin-bottom: 2rem;
  }

  .entry-title {
    font-size: 2.25rem;
    font-weight: 700;
    color: var(--color-text);
    margin-bottom: 1.5rem;
    line-height: 1.2;
  }

  .meta-info {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 1.5rem;
    padding-bottom: 1.5rem;
    border-bottom: 1px solid var(--color-border);
    margin-bottom: 2rem;
  }

  .meta-item {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    color: var(--color-text-muted);
  }

  .meta-text {
    font-weight: 500;
  }

  .engagement-stats {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-inline-start: auto;
  }

  .stat-item {
    display: flex;
    align-items: center;
    gap: 0.25rem;
  }

  .stat-item.likes {
    color: var(--color-danger);
  }

  .stat-item.comments {
    color: var(--color-primary);
  }

  .stat-count {
    font-weight: 600;
  }

  .tags-section {
    margin-bottom: 2rem;
  }

  .section-title {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--color-text);
    margin-bottom: 1rem;
  }

  .tags-container {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .tag {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0.5rem 0.75rem;
    background: var(--color-surface-3);
    color: var(--color-primary);
    border-radius: 20px;
    font-size: 0.875rem;
    font-weight: 500;
    border: 1px solid var(--color-border);
  }

  .entry-content {
    margin-bottom: 2rem;
    line-height: 1.7;
    color: var(--color-text);
  }

  .entry-content :global(h1),
  .entry-content :global(h2),
  .entry-content :global(h3),
  .entry-content :global(h4),
  .entry-content :global(h5),
  .entry-content :global(h6) {
    color: var(--color-text);
    font-weight: 600;
    margin-top: 2rem;
    margin-bottom: 1rem;
  }

  .entry-content :global(p) {
    margin-bottom: 1.25rem;
  }

  .entry-content :global(a) {
    color: var(--color-primary);
    text-decoration: underline;
  }

  .entry-content :global(blockquote) {
    border-inline-start: 4px solid var(--color-primary);
    padding-inline-start: 1rem;
    margin: 1.5rem 0;
    font-style: italic;
    color: var(--color-text-muted);
  }

  /* Enhanced markdown styles */
  .entry-content :global(ul),
  .entry-content :global(ol) {
    margin: 0.75rem 0;
    padding-inline-start: 1.5rem;
  }

  .entry-content :global(li) {
    margin: 0.25rem 0;
  }

  .entry-content :global(code) {
    background: var(--color-surface-3);
    padding: 0.125rem 0.25rem;
    border-radius: 0.25rem;
    font-family: "uthmantn", "Monaco", "Menlo", "Ubuntu Mono", monospace;
    font-size: 0.875rem;
  }

  .entry-content :global(pre) {
    background: var(--color-text);
    color: var(--color-surface);
    padding: 1rem;
    border-radius: 0.5rem;
    overflow-x: auto;
    margin: 1rem 0;
  }

  .entry-content :global(pre code) {
    background: transparent;
    padding: 0;
    color: inherit;
  }

  .entry-content :global(table) {
    width: 100%;
    border-collapse: collapse;
    margin: 1rem 0;
  }

  .entry-content :global(th),
  .entry-content :global(td) {
    padding: 0.5rem 0.75rem;
    border: 1px solid var(--color-border-strong);
    text-align: start;
  }

  .entry-content :global(th) {
    background: var(--color-surface);
    font-weight: 600;
  }

  .entry-content :global(strong) {
    font-weight: 600;
  }

  .entry-content :global(em) {
    font-style: italic;
  }

  .entry-content :global(del) {
    text-decoration: line-through;
  }

  .entry-content :global(img) {
    max-width: 100%;
    height: auto;
    border-radius: 0.5rem;
    margin: 1rem 0;
    display: block;
  }

  /* JSON content styles */
  .entry-content :global(br) {
    margin-bottom: 0.5rem;
  }

  .attachments-section {
    border-top: 1px solid var(--color-border);
    padding-top: 2rem;
    margin-bottom: 2rem;
  }

  .actions-section {
    border-top: 1px solid var(--color-border);
    padding-top: 1.5rem;
  }

  .like-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1.5rem;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 12px;
    color: var(--color-text-muted);
    font-weight: 500;
    transition: all 0.2s ease;
    cursor: pointer;
  }

  .like-button:hover {
    background: var(--color-danger-soft);
    border-color: var(--color-danger-soft);
    color: var(--color-danger);
  }

  .like-button.liked {
    background: var(--color-danger);
    border-color: var(--color-danger);
    color: white;
  }

  .like-button.liked:hover {
    background: var(--color-danger);
    border-color: var(--color-danger);
  }

  .like-button:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .comments-section {
    background: var(--color-surface);
    border-radius: 16px;
    box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
    padding: 2rem;
  }

  .comments-title {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 1.5rem;
    font-weight: 700;
    color: var(--color-text);
    margin-bottom: 1.5rem;
  }

  .comment-form {
    background: var(--color-surface);
    border-radius: 12px;
    padding: 1.5rem;
    margin-bottom: 2rem;
  }

  .comment-input-container {
    display: flex;
    gap: 1rem;
  }

  .comment-avatar {
    width: 40px;
    height: 40px;
    flex-shrink: 0;
  }

  .comment-input-wrapper {
    display: flex;
    flex: 1;
    gap: 0.5rem;
  }

  .comment-input {
    flex: 1;
    padding: 0.75rem;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    transition: border-color 0.2s ease;
  }

  /* RTL support for comment input */

  .comment-input:focus {
    outline: none;
    border-color: var(--color-primary);
  }

  .comment-submit {
    padding: 0.75rem;
    background: linear-gradient(135deg, var(--color-primary) 0%, var(--color-primary-hover) 100%);
    border: none;
    border-radius: 8px;
    color: white;
    font-weight: 500;
    transition: all 0.2s ease;
    cursor: pointer;
    flex-shrink: 0;
  }

  .comment-submit:hover {
    background: linear-gradient(135deg, var(--color-primary-hover) 0%, var(--color-info) 100%);
  }

  .comment-submit:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .comments-list {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
  }

  .comment-item {
    display: flex;
    gap: 1rem;
    padding: 1.5rem;
    background: var(--color-surface);
    border-radius: 12px;
    border: 1px solid var(--color-border);
  }

  .comment-content {
    flex: 1;
  }

  .comment-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-bottom: 0.5rem;
  }

  .comment-author {
    font-weight: 600;
    color: var(--color-text);
  }

  .comment-date {
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }

  .delete-comment {
    padding: 0.25rem;
    background: var(--color-danger-soft);
    border: 1px solid var(--color-danger-soft);
    border-radius: 6px;
    color: var(--color-danger);
    cursor: pointer;
    transition: all 0.2s ease;
    margin-inline-start: auto;
  }

  /* RTL support for delete button */

  .delete-comment:hover {
    background: var(--color-danger-soft);
    border-color: var(--color-danger-soft);
  }

  .comment-text {
    color: var(--color-text);
    line-height: 1.6;
  }

  .no-comments {
    text-align: center;
    padding: 3rem 1rem;
    color: var(--color-text-muted);
  }

  .no-comments-icon {
    margin: 0 auto 1rem;
    opacity: 0.5;
  }

  .no-comments-title {
    font-size: 1.125rem;
    font-weight: 600;
    margin-bottom: 0.5rem;
  }

  .no-comments-subtitle {
    font-size: 0.875rem;
  }

  .error-container {
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
    background: linear-gradient(135deg, var(--color-surface) 0%, var(--color-border) 100%);
  }

  .error-content {
    text-align: center;
    padding: 2rem;
  }

  .error-icon {
    width: 96px;
    height: 96px;
    background: var(--color-danger-soft);
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0 auto 1.5rem;
    color: var(--color-danger);
  }

  .error-title {
    font-size: 1.5rem;
    font-weight: 700;
    color: var(--color-text);
    margin-bottom: 0.75rem;
  }

  .error-message {
    color: var(--color-text-muted);
    margin-bottom: 1.5rem;
    max-width: 400px;
  }

  .error-button {
    padding: 0.75rem 1.5rem;
    background: linear-gradient(135deg, var(--color-primary) 0%, var(--color-primary-hover) 100%);
    border: none;
    border-radius: 12px;
    color: white;
    font-weight: 500;
    transition: all 0.2s ease;
    cursor: pointer;
  }

  .error-button:hover {
    background: linear-gradient(135deg, var(--color-primary-hover) 0%, var(--color-info) 100%);
    transform: translateY(-1px);
  }

  /* Added styles for relationships section */
  .relationships-section {
    margin-bottom: 2rem;
  }

  .relationships-container {
    display: flex;
    flex-wrap: wrap;
    gap: 1rem;
  }

  .relationship-item {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0.75rem;
    background: var(--color-surface-3);
    color: var(--color-text-muted);
    border-radius: 20px;
    font-size: 0.875rem;
    border: 1px solid var(--color-border);
  }

  .relationship-role {
    font-weight: 600;
    color: var(--color-primary);
  }

  .relationship-name {
    font-weight: 500;
  }

  @media (max-width: 768px) {
    .page-container {
      padding: 1rem;
    }

    .header {
      flex-direction: column;
      gap: 1rem;
      align-items: stretch;
    }

    .back-button,
    .edit-button {
      justify-content: center;
    }

    .delete-button:active {
      transform: translateY(0);
    }

    .main-card {
      padding: 1.5rem;
    }

    .entry-title {
      font-size: 1.875rem;
    }

    .meta-info {
      flex-direction: column;
      align-items: stretch;
      gap: 1rem;
    }

    .engagement-stats {
      margin-inline-start: 0;
      justify-content: center;
    }

    .comments-section {
      padding: 1.5rem;
    }

    .comment-form {
      padding: 1rem;
    }

    .comment-input-container {
      flex-direction: column;
    }

    .comment-avatar {
      align-self: center;
    }
  }

  /* Template loading and error states */
  .template-loading {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.75rem;
    padding: 2rem;
    color: var(--color-text-muted);
  }



  .template-error {
    padding: 1rem;
  }


  .template-error .fallback-data {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    padding: 1rem;
    border-radius: 0.5rem;
    margin-bottom: 1rem;
  }

  .template-error .fallback-data h4 {
    margin: 0 0 0.75rem 0;
    color: var(--color-text);
    font-size: 1rem;
  }

  .template-error .fallback-data dl {
    margin: 0;
  }

  .template-error .fallback-data dt {
    font-weight: 600;
    color: var(--color-text-muted);
    margin-top: 0.5rem;
  }

  .template-error .fallback-data dd {
    margin-inline-start: 0;
    color: var(--color-text-muted);
    margin-top: 0.25rem;
  }

  .template-error .fallback-content {
    background: var(--color-surface-3);
    padding: 1rem;
    border-radius: 0.5rem;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
</style>
