<script lang="ts">
  import { ReplyOutline, TrashBinOutline, UserCircleOutline } from "flowbite-svelte-icons";
  import Badge from "@/components/ui/Badge.svelte";
  import { _, locale } from "@/i18n";
  import { confirm } from "@/lib/confirm";
  import { formatDate } from "@/lib/format";
  import { formatNumberInText } from "@/lib/helpers";
  import { toasts } from "@/lib/toast";
  import { user } from "@/stores/user";
  import { createComment, deleteComment, deleteMultipleComments, findAllChildComments } from "@/lib/dmart_services";

  // Two-level comment threads under an entry: top-level comments with their
  // replies, reply in place, delete (own comment, or any comment on your own
  // entry) behind the shared ConfirmDialog.
  interface CommentRecord {
    shortname: string;
    attributes?: {
      owner_shortname?: string;
      created_at?: string;
      payload?: { body?: { body?: string; parent_comment_id?: string | null } };
    };
  }

  interface CommentNode extends CommentRecord {
    replies: CommentNode[];
  }

  let {
    comments = [],
    spaceName,
    subpath,
    itemShortname,
    entryOwnerShortname,
    onCommentAdded = () => {},
  }: {
    comments?: CommentRecord[];
    spaceName: string;
    subpath: string;
    itemShortname: string;
    entryOwnerShortname: string;
    onCommentAdded?: () => void;
  } = $props();

  let replyingTo = $state<string | null>(null);
  let replyText = $state("");
  let isSubmitting = $state(false);
  let deletingCommentId = $state<string | null>(null);

  function organizeComments(list: CommentRecord[]): CommentNode[] {
    const byId = new Map<string, CommentNode>();
    const topLevel: CommentNode[] = [];
    for (const comment of list) byId.set(comment.shortname, { ...comment, replies: [] });
    for (const comment of list) {
      const node = byId.get(comment.shortname)!;
      const parentId = comment.attributes?.payload?.body?.parent_comment_id;
      const parent = parentId ? byId.get(parentId) : undefined;
      if (parent) parent.replies.push(node);
      else topLevel.push(node);
    }
    return topLevel;
  }

  const organizedComments = $derived(organizeComments(comments));
  const number = (n: number) => formatNumberInText(n, $locale ?? "");

  function commentText(comment: CommentRecord): string {
    return comment.attributes?.payload?.body?.body ?? "";
  }

  function commentAuthor(comment: CommentRecord): string {
    return comment.attributes?.owner_shortname || $_("post_detail.comments.anonymous");
  }

  function canDeleteComment(comment: CommentRecord): boolean {
    const me = $user?.shortname;
    if (!me) return false;
    // The comment's owner, or the entry's owner for any comment on it.
    return comment.attributes?.owner_shortname === me || entryOwnerShortname === me;
  }

  function startReply(commentId: string) {
    replyingTo = commentId;
    replyText = "";
  }

  function cancelReply() {
    replyingTo = null;
    replyText = "";
  }

  async function handleDeleteComment(comment: CommentRecord) {
    if (!canDeleteComment(comment)) {
      toasts.error($_("post_detail.comments.delete_not_allowed"));
      return;
    }

    // Every reply under it goes too.
    const childIds = findAllChildComments(comment.shortname, comments);
    const hasReplies = childIds.length > 0;

    const confirmed = await confirm({
      title: $_("post_detail.comments.delete"),
      body: hasReplies
        ? $_("post_detail.comments.confirm_delete_with_replies", { values: { count: number(childIds.length) } })
        : $_("post_detail.comments.confirm_delete"),
      variant: "danger",
    });
    if (!confirmed) return;

    deletingCommentId = comment.shortname;
    try {
      const success = hasReplies
        ? await deleteMultipleComments([...childIds, comment.shortname], spaceName, subpath, itemShortname)
        : await deleteComment(comment.shortname, spaceName, subpath, itemShortname);

      if (success) {
        toasts.success(
          hasReplies
            ? $_("post_detail.comments.deleted_with_replies_successfully", {
                values: { count: number(childIds.length + 1) },
              })
            : $_("post_detail.comments.deleted_successfully"),
        );
        onCommentAdded();
      } else {
        toasts.error($_("post_detail.comments.delete_failed"));
      }
    } catch (error) {
      console.error("Error deleting comment:", error);
      toasts.error($_("post_detail.comments.delete_error"));
    } finally {
      deletingCommentId = null;
    }
  }

  async function submitReply() {
    if (!$user?.shortname) {
      toasts.error($_("post_detail.login_required.message"));
      return;
    }
    if (!replyText.trim()) {
      toasts.error($_("post_detail.comments.empty_comment"));
      return;
    }

    isSubmitting = true;
    try {
      const success = await createComment(spaceName, subpath, itemShortname, replyText.trim(), replyingTo ?? undefined);
      if (success) {
        toasts.success($_("post_detail.comments.reply_added_successfully"));
        replyText = "";
        replyingTo = null;
        onCommentAdded();
      } else {
        toasts.error($_("post_detail.comments.reply_failed"));
      }
    } catch (error) {
      console.error("Error adding reply:", error);
      toasts.error($_("post_detail.comments.reply_error"));
    } finally {
      isSubmitting = false;
    }
  }
</script>

{#snippet commentBlock(comment: CommentNode, isReply: boolean)}
  {@const deleting = deletingCommentId === comment.shortname}
  {@const replyCount = comment.replies.length}
  <div class="comment" class:reply={isReply}>
    <div class="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
      <div class="flex items-center gap-2 min-w-0">
        <UserCircleOutline size={isReply ? "sm" : "md"} class="text-text-faint shrink-0" aria-hidden="true" />
        <span class="text-sm font-semibold text-text truncate">{commentAuthor(comment)}</span>
        {#if comment.attributes?.created_at}
          <time datetime={comment.attributes.created_at} class="text-xs text-text-muted whitespace-nowrap">
            {formatDate(comment.attributes.created_at, "datetime", $locale)}
          </time>
        {/if}
      </div>
      <div class="flex items-center gap-1">
        {#if !isReply && $user?.shortname}
          <button
            type="button"
            class="app-btn app-btn-ghost app-btn-sm"
            onclick={() => startReply(comment.shortname)}
            disabled={replyingTo === comment.shortname}
          >
            <ReplyOutline size="xs" class="rtl:-scale-x-100" aria-hidden="true" />
            {$_("post_detail.comments.reply")}
          </button>
        {/if}
        {#if canDeleteComment(comment)}
          <button
            type="button"
            class="app-btn app-btn-ghost app-btn-sm text-danger"
            onclick={() => void handleDeleteComment(comment)}
            disabled={deleting}
            aria-busy={deleting}
            title={replyCount > 0
              ? $_("post_detail.comments.delete_with_replies_warning", { values: { count: number(replyCount) } })
              : $_("post_detail.comments.delete")}
          >
            {#if deleting}
              <span class="spinner spinner-xs" aria-hidden="true"></span>
            {:else}
              <TrashBinOutline size="xs" aria-hidden="true" />
            {/if}
            {$_("post_detail.comments.delete")}
            {#if replyCount > 0}
              <Badge variant="warning" size="sm">{number(replyCount)}</Badge>
            {/if}
          </button>
        {/if}
      </div>
    </div>

    <p class="mt-2 text-sm text-text leading-relaxed whitespace-pre-wrap break-words">{commentText(comment)}</p>

    {#if replyingTo === comment.shortname}
      <form
        class="mt-3 pt-3 border-t border-border flex flex-col gap-2"
        onsubmit={(event) => {
          event.preventDefault();
          void submitReply();
        }}
      >
        <label for="reply-{comment.shortname}" class="sr-only">{$_("post_detail.comments.reply_placeholder")}</label>
        <textarea
          id="reply-{comment.shortname}"
          bind:value={replyText}
          placeholder={$_("post_detail.comments.reply_placeholder")}
          class="reply-textarea"
          rows="3"
          disabled={isSubmitting}
        ></textarea>
        <div class="flex flex-wrap justify-end gap-2">
          <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={cancelReply} disabled={isSubmitting}>
            {$_("post_detail.comments.cancel")}
          </button>
          <button
            type="submit"
            class="app-btn app-btn-primary app-btn-sm"
            disabled={isSubmitting || !replyText.trim()}
            aria-busy={isSubmitting}
          >
            {#if isSubmitting}
              <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
            {/if}
            {$_("post_detail.comments.submit_reply")}
          </button>
        </div>
      </form>
    {/if}
  </div>
{/snippet}

{#if organizedComments.length > 0}
  <div class="flex flex-col gap-4">
    {#each organizedComments as comment (comment.shortname)}
      <div class="thread">
        {@render commentBlock(comment, false)}
        {#if comment.replies.length > 0}
          <div class="replies">
            {#each comment.replies as reply (reply.shortname)}
              {@render commentBlock(reply, true)}
            {/each}
          </div>
        {/if}
      </div>
    {/each}
  </div>
{/if}

<style>
  .thread {
    border: 1px solid var(--color-border);
    border-radius: var(--radius-card);
    overflow: hidden;
    background: var(--color-surface-2);
  }

  .comment {
    padding: 1rem;
  }

  .replies {
    background: var(--color-surface);
    border-top: 1px solid var(--color-border);
    padding-inline-start: 1rem;
  }

  .comment.reply {
    border-inline-start: 2px solid var(--color-border-strong);
    padding: 0.875rem 1rem;
  }

  .comment.reply + .comment.reply {
    border-top: 1px solid var(--color-border);
  }

  .reply-textarea {
    width: 100%;
    padding: 0.75rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-control);
    background: var(--color-surface-2);
    color: var(--color-text);
    font-size: var(--font-size-sm);
    font-family: inherit;
    line-height: var(--line-height-normal);
    resize: vertical;
    min-height: 5rem;
  }

  .reply-textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 1px var(--color-primary);
  }

  .reply-textarea:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }
</style>
