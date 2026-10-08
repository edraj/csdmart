<script lang="ts">
  import { HeartOutline, HeartSolid, MessageDotsOutline } from "flowbite-svelte-icons";
  import { _, locale } from "@/i18n";
  import { formatNumberInText } from "@/lib/helpers";

  // Reactions and comments under an entry. The like button is a real toggle
  // (aria-pressed + a named label); the comment counter jumps to the
  // comments section rather than looking clickable and doing nothing.
  let {
    reactionsCount,
    commentsCount,
    userReactionId,
    isSubmittingReaction,
    onToggleReaction,
    commentsId = "comments",
  }: {
    reactionsCount: number;
    commentsCount: number;
    userReactionId: string | null;
    isSubmittingReaction: boolean;
    onToggleReaction: () => void;
    /** id of the comments section the counter scrolls to. */
    commentsId?: string;
  } = $props();

  const liked = $derived(!!userReactionId);
  const number = (n: number) => formatNumberInText(n, $locale ?? "");
  const likeLabel = $derived(
    `${liked ? $_("post_detail.reactions.unlike") : $_("post_detail.reactions.like")} · ${$_(
      "post_detail.reactions.count_aria",
      { values: { count: number(reactionsCount) } },
    )}`,
  );
  const commentsLabel = $derived(
    `${$_("post_detail.comments.go_to_comments")} · ${$_("catalog_contents.card.comments_aria", {
      values: { count: number(commentsCount) },
    })}`,
  );

  function goToComments() {
    const target = document.getElementById(commentsId);
    if (!target) return;
    target.scrollIntoView({ behavior: "smooth", block: "start" });
    target.focus({ preventScroll: true });
  }
</script>

<div class="interactions" role="group" aria-label={$_("post_detail.sections.comments")}>
  <button type="button" class="stat" onclick={goToComments} aria-label={commentsLabel} title={$_("post_detail.comments.go_to_comments")}>
    <MessageDotsOutline size="md" aria-hidden="true" />
    <span class="tabular-nums" aria-hidden="true">{number(commentsCount)}</span>
  </button>

  <button
    type="button"
    class="stat like"
    class:liked
    onclick={onToggleReaction}
    disabled={isSubmittingReaction}
    aria-pressed={liked}
    aria-busy={isSubmittingReaction}
    aria-label={likeLabel}
    title={liked ? $_("post_detail.reactions.unlike") : $_("post_detail.reactions.like")}
  >
    {#if liked}
      <HeartSolid size="md" aria-hidden="true" />
    {:else}
      <HeartOutline size="md" aria-hidden="true" />
    {/if}
    <span class="tabular-nums" aria-hidden="true">{number(reactionsCount)}</span>
  </button>
</div>

<style>
  .interactions {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem 1rem;
    margin-top: 2rem;
    padding-top: 1.25rem;
    border-top: 1px solid var(--color-border);
  }

  .stat {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.375rem 0.75rem;
    border-radius: var(--radius-full);
    border: 1px solid transparent;
    background: transparent;
    color: var(--color-text-muted);
    font-size: var(--font-size-sm);
    font-weight: var(--font-weight-medium);
    cursor: pointer;
    transition: color var(--duration-fast) var(--ease-out), background var(--duration-fast) var(--ease-out);
  }

  .stat:hover:not(:disabled) {
    color: var(--color-text);
    background: var(--color-surface-3);
  }

  .stat:disabled {
    cursor: progress;
    opacity: 0.7;
  }

  .like:hover:not(:disabled),
  .like.liked {
    color: var(--color-danger);
  }

  .like.liked {
    background: var(--color-danger-soft);
  }
</style>
