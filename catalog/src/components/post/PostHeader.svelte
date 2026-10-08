<script lang="ts">
  import { CalendarMonthOutline, FileLinesOutline } from "flowbite-svelte-icons";
  import Avatar from "@/components/Avatar.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import { _ } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { getAvatarCached } from "@/lib/dmart_services/avatars";
  import { getAuthorInfo, getPostTitle } from "@/lib/utils/postUtils";

  // The top of an entry page: who wrote it (name once — not "name @name"),
  // when, which schema it follows, the title and its tags. Nothing here is
  // decorative state: no static "Hot" badge, no read time computed from
  // fields the record does not have.
  interface PostHeaderData {
    owner_shortname?: string;
    created_at?: string;
    tags?: unknown;
    payload?: { schema_shortname?: string };
    [key: string]: unknown;
  }

  let { postData, locale }: { postData: PostHeaderData; locale: string } = $props();

  const author = $derived(getAuthorInfo(postData, $_("common.unknown")));
  const title = $derived(getPostTitle(postData));
  const schema = $derived(postData.payload?.schema_shortname ?? "");
  const tags = $derived(
    Array.isArray(postData.tags) ? postData.tags.filter((t): t is string => typeof t === "string" && t.trim() !== "") : [],
  );

  // One cached lookup for the author's picture; Avatar shows initials meanwhile.
  let avatarUrl = $state<string | null>(null);
  $effect(() => {
    const owner = postData.owner_shortname;
    let cancelled = false;
    avatarUrl = null;
    if (!owner) return;
    void getAvatarCached(owner).then((url) => {
      if (!cancelled) avatarUrl = url;
    });
    return () => {
      cancelled = true;
    };
  });
</script>

<header>
  <div class="flex items-center gap-3 min-w-0">
    <Avatar src={avatarUrl} alt="" size={44} />
    <div class="min-w-0">
      <p class="text-sm font-semibold text-text truncate">{author}</p>
      <div class="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-text-muted">
        {#if postData.created_at}
          <time datetime={postData.created_at} class="inline-flex items-center gap-1">
            <CalendarMonthOutline size="xs" aria-hidden="true" />
            {formatDate(postData.created_at, "datetime", locale) || $_("common.not_available")}
          </time>
        {/if}
        {#if schema}
          <Badge size="sm">
            <FileLinesOutline size="xs" aria-hidden="true" />
            {schema}
          </Badge>
        {/if}
      </div>
    </div>
  </div>

  <h1 class="mt-5 text-2xl sm:text-3xl font-semibold text-text leading-tight break-words" dir="auto">{title}</h1>

  {#if tags.length > 0}
    <div class="mt-3 flex flex-wrap gap-1.5">
      {#each tags as tag (tag)}
        <Badge variant="primary">#{tag}</Badge>
      {/each}
    </div>
  {/if}
</header>
