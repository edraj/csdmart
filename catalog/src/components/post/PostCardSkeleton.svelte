<script lang="ts">
  import SkeletonBlock from "@/components/SkeletonBlock.svelte";
  import Card from "@/components/ui/Card.svelte";

  // The shape of a PostCard while the list is loading: avatar, two meta
  // bars, a title and two lines of text. Decorative; the page announces the
  // loading state once through LoadingState/aria-busy, not per card.
  let { preview = true }: { preview?: boolean } = $props();
</script>

<Card aria-hidden="true">
  <div class="flex items-center gap-3">
    <SkeletonBlock width="2.5rem" height="2.5rem" radius="var(--radius-full)" />
    <div class="flex-1 space-y-2">
      <SkeletonBlock width="35%" height="0.75rem" radius="var(--radius-full)" />
      <SkeletonBlock width="25%" height="0.625rem" radius="var(--radius-full)" />
    </div>
  </div>
  <SkeletonBlock width="70%" height="1rem" radius="var(--radius-full)" class="mt-4" />
  {#if preview}
    <SkeletonBlock width="100%" height="0.75rem" radius="var(--radius-full)" class="mt-3" />
    <SkeletonBlock width="55%" height="0.75rem" radius="var(--radius-full)" class="mt-2" />
  {/if}
</Card>
