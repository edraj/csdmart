<script lang="ts">
  import { onMount } from "svelte";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { localized } from "@/lib/catalogItems";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { getPolls, userVote } from "@/lib/dmart_services";
  import { APPLICATIONS_SPACE } from "@/lib/constants";
  import { DmartScope } from "@edraj/tsdmart";
  import { toasts } from "@/lib/toast";
  import {
    CheckCircleOutline,
    ClockOutline,
    EyeOutline,
    UserOutline,
    ChartOutline,
    PlusOutline,
  } from "flowbite-svelte-icons";
  import { user } from "@/stores/user";
  import CreatePollModal from "./CreatePollModal.svelte";
  import Modal from "@/components/Modal.svelte";
  import Avatar from "@/components/Avatar.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import CatalogToolbar from "@/components/ui/CatalogToolbar.svelte";
  import Card from "@/components/ui/Card.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  interface Candidate {
    key: string;
    name: string;
    votes: number;
    voters: string[];
    percentage: number;
    attachment: unknown;
  }

  interface Poll {
    shortname: string;
    displayname: unknown;
    description: unknown;
    candidates: Candidate[];
    isActive: boolean;
    hasVoted: boolean;
    userVote: string | null;
    totalVotes: number;
    createdBy: string;
    createdAt: string | null;
    tags: string[];
  }

  type StatusFilter = "all" | "active" | "ended";

  let polls = $state<Poll[]>([]);
  let loading = $state(true);
  let loadError = $state<unknown>(null);
  let searchTerm = $state("");
  let filterStatus = $state<StatusFilter>("all");
  let selectedPoll = $state<Poll | null>(null);
  let showVoteModal = $state(false);
  let selectedCandidate = $state("");
  let votingInProgress = $state(false);
  let showResults = $state(false);
  let showCreateModal = $state(false);

  $effect(() => setTitle($_("polls.title")));

  const statusFilters: Array<{ id: StatusFilter; label: () => string }> = [
    { id: "all", label: () => $_("polls.filter_all") },
    { id: "active", label: () => $_("polls.filter_active") },
    { id: "ended", label: () => $_("polls.filter_ended") },
  ];

  onMount(async () => {
    await loadPolls();
  });

  async function loadPolls() {
    loading = true;
    loadError = null;
    try {
      const response = await getPolls(APPLICATIONS_SPACE, DmartScope.managed);

      if (response?.status === "success" && response?.records) {
        const me = $user?.shortname;
        polls = response.records.map((poll): Poll => {
          const attrs = (poll.attributes ?? {}) as Record<string, unknown>;
          const body = ((attrs.payload as { body?: Record<string, unknown> } | undefined)?.body ?? {}) as Record<string, unknown>;
          const rawCandidates = Array.isArray(body.candidates) ? (body.candidates as Array<{ key: string; value: string }>) : [];
          const attachments = ((poll as unknown as { attachments?: { json?: unknown[] } }).attachments?.json ?? []) as Array<{
            shortname?: string;
            attributes?: { payload?: { body?: { voters?: unknown } } };
          }>;

          const candidates: Candidate[] = rawCandidates.map((candidate) => {
            const attachment = attachments.find((att) => att.shortname === candidate.key);
            const rawVoters = attachment?.attributes?.payload?.body?.voters;
            const voters = Array.isArray(rawVoters) ? (rawVoters as string[]) : [];
            return {
              key: candidate.key,
              name: candidate.value,
              votes: voters.length,
              voters,
              percentage: 0,
              attachment,
            };
          });

          const totalVotes = candidates.reduce((sum, c) => sum + c.votes, 0);
          for (const c of candidates) {
            c.percentage = totalVotes > 0 ? Math.round((c.votes / totalVotes) * 100) : 0;
          }

          const mine = me ? candidates.find((c) => c.voters.includes(me)) : undefined;

          return {
            shortname: poll.shortname,
            displayname: attrs.displayname,
            description: attrs.description,
            candidates,
            isActive: attrs.is_active !== false,
            hasVoted: !!mine,
            userVote: mine?.name ?? null,
            totalVotes,
            createdBy: typeof attrs.owner_shortname === "string" ? attrs.owner_shortname : "",
            createdAt: typeof attrs.created_at === "string" ? attrs.created_at : null,
            tags: Array.isArray(attrs.tags) ? (attrs.tags as string[]) : [],
          };
        });
      } else {
        polls = [];
      }
    } catch (error) {
      log.error("Error loading polls:", error);
      loadError = error;
    } finally {
      loading = false;
    }
  }

  function titleOf(poll: Poll): string {
    return localized(poll.displayname as never, $locale) || poll.shortname || $_("polls.untitled");
  }

  function descriptionOf(poll: Poll): string {
    return localized(poll.description as never, $locale);
  }

  const filteredPolls = $derived.by(() => {
    const q = searchTerm.trim().toLowerCase();
    return polls.filter((poll) => {
      const matchesSearch =
        !q ||
        titleOf(poll).toLowerCase().includes(q) ||
        descriptionOf(poll).toLowerCase().includes(q) ||
        poll.candidates.some((c) => c.name.toLowerCase().includes(q));
      const matchesStatus =
        filterStatus === "all" ||
        (filterStatus === "active" && poll.isActive) ||
        (filterStatus === "ended" && !poll.isActive);
      return matchesSearch && matchesStatus;
    });
  });

  const activeCount = $derived(polls.filter((p) => p.isActive).length);

  function leadingOf(poll: Poll): Candidate | null {
    if (poll.candidates.length === 0) return null;
    return poll.candidates.reduce((best, c) => (c.votes > best.votes ? c : best), poll.candidates[0]);
  }

  function openVoteModal(poll: Poll) {
    selectedPoll = poll;
    selectedCandidate = "";
    showVoteModal = true;
    showResults = false;
  }

  function openResultsModal(poll: Poll) {
    selectedPoll = poll;
    showResults = true;
    showVoteModal = true;
  }

  function closeModal() {
    if (votingInProgress) return;
    showVoteModal = false;
    showResults = false;
    selectedPoll = null;
    selectedCandidate = "";
  }

  async function submitVote() {
    const me = $user?.shortname;
    if (!selectedPoll || !me || !selectedCandidate) {
      toasts.error($_("polls.select_option"));
      return;
    }

    votingInProgress = true;
    try {
      const candidateObj = selectedPoll.candidates.find((c) => c.key === selectedCandidate);
      if (!candidateObj) {
        toasts.error($_("polls.invalid_candidate"));
        return;
      }
      if (candidateObj.voters.includes(me)) {
        toasts.error($_("polls.already_voted"));
        return;
      }

      for (const other of selectedPoll.candidates) {
        if (other.key !== selectedCandidate && other.voters.includes(me)) {
          await userVote(
            selectedPoll.shortname,
            other.key,
            other.voters.filter((voter) => voter !== me),
            true,
          );
        }
      }

      const response = await userVote(
        selectedPoll.shortname,
        selectedCandidate,
        [...candidateObj.voters, me],
        candidateObj.attachment != null,
      );

      if (response) {
        toasts.success($_("polls.vote_success"));
        votingInProgress = false;
        closeModal();
        await loadPolls();
      } else {
        toasts.error($_("polls.vote_error"));
      }
    } catch (error) {
      log.error("Error submitting vote:", error);
      toasts.error($_("polls.vote_error"));
    } finally {
      votingInProgress = false;
    }
  }
</script>

<div class="mx-auto max-w-6xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("polls.title")} description={$_("polls.description")} icon={ChartOutline}>
    {#snippet actions()}
      <button type="button" class="app-btn app-btn-primary" onclick={() => (showCreateModal = true)}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("polls.create_poll")}
      </button>
    {/snippet}
  </PageHeader>

  <div class="flex flex-wrap items-center gap-2 mb-4" aria-label={$_("statistics")} role="group">
    <Badge>
      <ChartOutline size="sm" aria-hidden="true" />
      {$_("route_labels.label_total")}: {polls.length}
    </Badge>
    <Badge variant="success">
      <ClockOutline size="sm" aria-hidden="true" />
      {$_("route_labels.label_active")}: {activeCount}
    </Badge>
    <Badge>
      <CheckCircleOutline size="sm" aria-hidden="true" />
      {$_("route_labels.label_ended")}: {polls.length - activeCount}
    </Badge>
  </div>

  <CatalogToolbar
    class="mb-6"
    bind:search={searchTerm}
    placeholder={$_("polls.search_placeholder")}
    onSearch={(q) => (searchTerm = q)}
  >
    {#snippet filters()}
      <div class="inline-flex rounded-control border border-border bg-surface-2 p-0.5" role="group" aria-label={$_("polls.filter_label")}>
        {#each statusFilters as filter (filter.id)}
          <button
            type="button"
            class="px-3 h-8 rounded-control text-sm font-medium transition-colors cursor-pointer
 {filterStatus === filter.id ? 'bg-primary text-text-on-primary' : 'text-text-muted hover:text-text hover:bg-surface-3'}"
            aria-pressed={filterStatus === filter.id}
            onclick={() => (filterStatus = filter.id)}
          >
            {filter.label()}
          </button>
        {/each}
      </div>
    {/snippet}
  </CatalogToolbar>

  {#if loading && polls.length === 0}
    <LoadingState label={$_("polls.loading")} />
  {:else if loadError}
    <ErrorState title={$_("polls.load_error")} error={loadError} onRetry={loadPolls} />
  {:else if filteredPolls.length === 0}
    <EmptyState
      icon={ChartOutline}
      title={$_("polls.no_polls")}
      hint={polls.length === 0 ? $_("polls.no_polls_description") : $_("search_filters.no_results.description")}
    >
      {#if polls.length === 0}
        <button type="button" class="app-btn app-btn-primary app-btn-sm" onclick={() => (showCreateModal = true)}>
          <PlusOutline size="sm" aria-hidden="true" />
          {$_("polls.create_poll")}
        </button>
      {/if}
    </EmptyState>
  {:else}
    <LoadingState variant="overlay" {loading}>
      <ul class="grid grid-cols-1 md:grid-cols-2 gap-6 list-none p-0 m-0">
        {#each filteredPolls as poll (poll.shortname)}
          {@const leading = leadingOf(poll)}
          <li class="flex">
            <Card class="w-full flex flex-col" padding="md">
              <div class="flex flex-wrap items-start justify-between gap-3 mb-4">
                <div class="flex items-center gap-3 min-w-0">
                  <Avatar alt={poll.createdBy} size="40" />
                  <div class="min-w-0">
                    <p class="text-sm font-semibold text-text truncate">{poll.createdBy || $_("common.unknown")}</p>
                    <p class="text-xs text-text-faint">{formatDate(poll.createdAt, "relative", $locale)}</p>
                  </div>
                </div>
                <Badge variant={poll.isActive ? "success" : "neutral"} size="sm">
                  {poll.isActive ? $_("polls.filter_active") : $_("polls.filter_ended")}
                </Badge>
              </div>

              <h3 class="text-lg font-semibold text-text mb-1">{titleOf(poll)}</h3>
              {#if descriptionOf(poll)}
                <p class="text-sm text-text-muted mb-5 line-clamp-2">{descriptionOf(poll)}</p>
              {/if}

              {#if leading}
                <div class="mb-5">
                  <div class="flex justify-between items-center text-xs mb-1.5">
                    <span class="text-text-muted">
                      {$_("polls.leading")}: <span class="text-text font-medium">{leading.name}</span>
                    </span>
                    <span class="font-semibold text-primary tabular-nums">{leading.percentage}%</span>
                  </div>
                  <div
                    class="w-full h-1.5 bg-surface-3 rounded-full overflow-hidden"
                    role="progressbar"
                    aria-valuenow={leading.percentage}
                    aria-valuemin="0"
                    aria-valuemax="100"
                    aria-label={leading.name}
                  >
                    <div class="h-full bg-primary rounded-full transition-[width]" style="width: {leading.percentage}%"></div>
                  </div>
                </div>
              {/if}

              <div class="flex flex-wrap items-center justify-between gap-3 pt-4 border-t border-border mt-auto">
                <div class="flex flex-wrap items-center gap-3 text-xs text-text-faint">
                  <span class="inline-flex items-center gap-1 tabular-nums">
                    <UserOutline size="xs" aria-hidden="true" />
                    {$_("polls.votes_short", { values: { count: poll.totalVotes } })}
                  </span>
                  <span class="tabular-nums">{$_("polls.options_count", { values: { count: poll.candidates.length } })}</span>
                  {#if poll.tags.length > 0}
                    <span>{poll.tags.join(", ")}</span>
                  {/if}
                </div>

                <div class="flex items-center gap-2">
                  <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={() => openResultsModal(poll)}>
                    <EyeOutline size="sm" aria-hidden="true" />
                    {$_("polls.results_title")}
                  </button>
                  {#if poll.hasVoted}
                    <Badge variant="success">
                      <CheckCircleOutline size="sm" aria-hidden="true" />
                      {$_("polls.voted")}
                    </Badge>
                  {:else if poll.isActive}
                    <button type="button" class="app-btn app-btn-primary app-btn-sm" onclick={() => openVoteModal(poll)}>
                      {$_("polls.vote_button")}
                    </button>
                  {/if}
                </div>
              </div>
            </Card>
          </li>
        {/each}
      </ul>
    </LoadingState>
  {/if}
</div>

{#snippet results(poll: Poll)}
  <ul class="space-y-5 list-none p-0 m-0">
    {#each poll.candidates as candidate (candidate.key)}
      <li>
        <div class="flex justify-between items-end mb-1.5">
          <span class="text-sm text-text font-medium">{candidate.name}</span>
          <span class="text-xs font-semibold text-primary tabular-nums">{candidate.percentage}%</span>
        </div>
        <div
          class="w-full h-2 bg-surface-3 rounded-full overflow-hidden mb-1"
          role="progressbar"
          aria-valuenow={candidate.percentage}
          aria-valuemin="0"
          aria-valuemax="100"
          aria-label={candidate.name}
        >
          <div class="h-full bg-primary rounded-full" style="width: {candidate.percentage}%"></div>
        </div>
        <div class="text-xs text-text-faint tabular-nums">{$_("polls.votes_short", { values: { count: candidate.votes } })}</div>
      </li>
    {/each}
  </ul>
{/snippet}

{#if showVoteModal && selectedPoll}
  {@const poll = selectedPoll}
  <Modal onClose={closeModal} title={titleOf(poll)} size="lg" dismissable={!votingInProgress}>
    {#snippet icon()}
      <ChartOutline size="lg" />
    {/snippet}

    {#if showResults}
      {@render results(poll)}
    {:else if poll.isActive && !poll.hasVoted}
      <div class="space-y-3" role="radiogroup" aria-label={$_("polls.select_option")}>
        {#each poll.candidates as candidate (candidate.key)}
          {@const chosen = selectedCandidate === candidate.key}
          <button
            type="button"
            role="radio"
            aria-checked={chosen}
            class="w-full flex items-center gap-4 p-4 rounded-card border-2 transition-colors text-start cursor-pointer
 {chosen ? 'border-primary bg-primary-soft' : 'border-border hover:border-border-strong'}"
            onclick={() => (selectedCandidate = candidate.key)}
          >
            <span
              class="shrink-0 w-5 h-5 rounded-full border-2 flex items-center justify-center
 {chosen ? 'border-primary bg-primary' : 'border-border-strong'}"
              aria-hidden="true"
            >
              {#if chosen}
                <svg class="w-3 h-3 text-text-on-primary" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="3" d="M5 13l4 4L19 7" />
                </svg>
              {/if}
            </span>
            <span class="text-sm font-medium text-text">{candidate.name}</span>
          </button>
        {/each}
      </div>
    {:else}
      <div class="text-center pb-5 border-b border-border mb-5">
        {#if poll.hasVoted}
          <Badge variant="success">
            <CheckCircleOutline size="sm" aria-hidden="true" />
            {$_("polls.voted_for")}: {poll.userVote}
          </Badge>
        {:else}
          <Badge>
            <ClockOutline size="sm" aria-hidden="true" />
            {$_("polls.poll_ended")}
          </Badge>
        {/if}
      </div>
      {@render results(poll)}
    {/if}

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeModal} disabled={votingInProgress}>
        {$_("polls.cancel")}
      </button>
      {#if !showResults && poll.isActive && !poll.hasVoted}
        <button
          type="button"
          class="app-btn app-btn-primary"
          onclick={submitVote}
          disabled={!selectedCandidate || votingInProgress}
          aria-busy={votingInProgress}
        >
          {#if votingInProgress}
            <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {/if}
          {$_("polls.vote_button")}
        </button>
      {/if}
    {/snippet}
  </Modal>
{/if}

{#if showCreateModal}
  <CreatePollModal onClose={() => (showCreateModal = false)} onSuccess={loadPolls} />
{/if}
