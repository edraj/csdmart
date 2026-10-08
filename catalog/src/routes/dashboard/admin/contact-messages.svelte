<script lang="ts">
  import { resolveTotal } from "@shared/query-total";
  import { onMount } from "svelte";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { APPLICATIONS_SPACE, CONTACTS_SUBPATH } from "@/lib/constants";
  import {
    fetchContactMessages,
    markMessageAsReplied,
  } from "@/lib/dmart_services";
  import { toasts } from "@/lib/toast";
  import {
    ChevronLeftOutline,
    ChevronRightOutline,
    MessagesOutline,
    PaperPlaneOutline,
    RefreshOutline,
    ReplyOutline,
  } from "flowbite-svelte-icons";
  import Modal from "@/components/Modal.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import Card from "@/components/ui/Card.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  interface ContactBody {
    full_name?: string;
    email?: string;
    contact_email?: string;
    subject?: string;
    message?: string;
    attachments?: Array<{ name?: string; filename?: string }>;
  }

  interface ContactMessage {
    shortname: string;
    attributes: {
      created_at?: string;
      payload?: { body?: ContactBody; replied?: boolean };
    };
    attachments?: Record<string, unknown> | unknown[];
  }

  let messages = $state<ContactMessage[]>([]);
  let loading = $state(true);
  let error = $state<unknown>(null);
  let currentPage = $state(0);
  const limit = 20;
  let totalMessages = $state(0);

  let showModal = $state(false);
  let selectedMessage = $state<ContactMessage | null>(null);
  let replyContent = $state("");
  let sendingReply = $state(false);

  $effect(() => setTitle($_("contactMessages")));

  const totalPages = $derived(Math.max(1, Math.ceil(totalMessages / limit)));
  const rangeStart = $derived(totalMessages === 0 ? 0 : currentPage * limit + 1);
  const rangeEnd = $derived(Math.min((currentPage + 1) * limit, totalMessages));

  function bodyOf(message: ContactMessage): ContactBody {
    return message.attributes?.payload?.body ?? {};
  }

  function hasAttachment(message: ContactMessage): boolean {
    const list = bodyOf(message).attachments;
    return Array.isArray(list) && list.length > 0;
  }

  function isReplied(message: ContactMessage): boolean {
    const a = message.attachments;
    if (!a) return false;
    return Array.isArray(a) ? a.length > 0 : Object.keys(a).length > 0;
  }

  // Replies are stored as `comment` attachments with
  // payload.body = { state: "replied", body: <text> } (see markMessageAsReplied).
  function getReplyMessage(message: ContactMessage): string {
    if (!message.attachments || Array.isArray(message.attachments)) return "";
    for (const group of Object.values(message.attachments)) {
      const list = Array.isArray(group) ? group : [group];
      for (const attachment of list) {
        const body = (attachment as { attributes?: { payload?: { body?: { state?: string; body?: unknown } } } })
          ?.attributes?.payload?.body;
        if (body?.state === "replied") {
          return typeof body.body === "string" ? body.body : "";
        }
      }
    }
    return "";
  }

  async function loadMessages() {
    loading = true;
    error = null;
    try {
      const response = await fetchContactMessages();
      if (response && response.status === "success") {
        messages = (response.records ?? []) as unknown as ContactMessage[];
        totalMessages = resolveTotal((response.attributes as { total?: number })?.total);
        await autoMarkAttachmentMessages();
      } else {
        error = $_("failedToFetchContactMessages");
      }
    } catch (err) {
      log.error("Error fetching contact messages:", err);
      error = err;
    } finally {
      loading = false;
    }
  }

  async function autoMarkAttachmentMessages() {
    const messagesToMark = messages.filter((m) => hasAttachment(m) && !isReplied(m));

    for (const message of messagesToMark) {
      try {
        await markMessageAsReplied(
          APPLICATIONS_SPACE,
          CONTACTS_SUBPATH,
          message.shortname,
          "Auto-replied: Message contains attachment",
        );
        if (message.attributes.payload) message.attributes.payload.replied = true;
      } catch (err) {
        log.error(`Error auto-marking message ${message.shortname}:`, err);
      }
    }
  }

  function openReplyModal(message: ContactMessage) {
    const body = bodyOf(message);
    const ownerEmail = body.email || body.contact_email || "";

    if (!ownerEmail) {
      toasts.error($_("noEmailFoundForMessage"));
      return;
    }

    selectedMessage = message;
    replyContent = "";
    showModal = true;
  }

  function closeModal() {
    if (sendingReply) return;
    showModal = false;
    selectedMessage = null;
    replyContent = "";
  }

  async function sendReply(event?: SubmitEvent) {
    event?.preventDefault();
    if (!selectedMessage || !replyContent.trim()) {
      toasts.error($_("toast.reply_required"));
      return;
    }

    sendingReply = true;
    const target = selectedMessage;

    try {
      const body = bodyOf(target);
      const ownerEmail = body.email || body.contact_email || "";
      const subject = body.subject ? `${body.subject} - ${$_("reply")}` : $_("replyToYourMessage");

      window.open(
        `mailto:${ownerEmail}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(replyContent)}`,
      );

      const success = await markMessageAsReplied(
        APPLICATIONS_SPACE,
        CONTACTS_SUBPATH,
        target.shortname,
        replyContent,
      );

      sendingReply = false;
      closeModal();
      if (success) {
        await loadMessages();
        toasts.success($_("toast.reply_sent_marked"));
      } else {
        toasts.error($_("toast.reply_sent_failed"));
      }
    } catch (err) {
      log.error("Error marking message as replied:", err);
      sendingReply = false;
      closeModal();
      toasts.error($_("toast.reply_sent_error"));
    }
  }

  function nextPage() {
    if ((currentPage + 1) * limit < totalMessages) {
      currentPage++;
      loadMessages();
    }
  }

  function prevPage() {
    if (currentPage > 0) {
      currentPage--;
      loadMessages();
    }
  }

  onMount(() => {
    loadMessages();
  });
</script>

<div class="mx-auto max-w-5xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader
    title={$_("contactMessages")}
    description={totalMessages > 0
      ? $_("contact_inbox.showing", { values: { start: rangeStart, end: rangeEnd, total: totalMessages } })
      : undefined}
    icon={MessagesOutline}
  >
    {#snippet actions()}
      <button
        type="button"
        class="app-btn app-btn-secondary app-btn-sm"
        onclick={loadMessages}
        disabled={loading}
      >
        <RefreshOutline size="sm" class={loading ? "animate-spin" : ""} aria-hidden="true" />
        {loading ? $_("refreshing") : $_("refresh")}
      </button>
    {/snippet}
  </PageHeader>

  {#if loading && messages.length === 0}
    <LoadingState label={$_("loadingMessages")} />
  {:else if error}
    <ErrorState title={$_("failedToFetchContactMessages")} {error} onRetry={loadMessages} />
  {:else if messages.length === 0}
    <EmptyState icon={MessagesOutline} title={$_("noContactMessages")} hint={$_("noMessagesSubmitted")} />
  {:else}
    <LoadingState variant="overlay" {loading}>
      <ul class="space-y-4 list-none p-0 m-0">
        {#each messages as message (message.shortname)}
          {@const body = bodyOf(message)}
          {@const replied = isReplied(message)}
          <li>
            <Card>
              <div class="flex flex-wrap items-start justify-between gap-3 mb-3">
                <div class="min-w-0">
                  <h3 class="text-lg font-semibold text-text break-words">
                    {body.full_name || $_("anonymous")}
                  </h3>
                  <p class="text-sm text-text-muted break-all">
                    {body.email || $_("noEmailProvided")}
                  </p>
                  <p class="text-xs text-text-faint mt-1 tabular-nums">
                    {$_("submitted")}: {formatDate(message.attributes.created_at, "datetime", $locale)}
                  </p>
                </div>
                <div class="flex flex-wrap items-center gap-2">
                  <Badge size="sm">{message.shortname}</Badge>
                  {#if hasAttachment(message)}
                    <Badge variant="warning" size="sm">{$_("attachment")}</Badge>
                  {/if}
                  {#if replied}
                    <Badge variant="success" size="sm">{$_("replied")}</Badge>
                  {/if}
                  {#if body.email && !replied}
                    <button
                      type="button"
                      class="app-btn app-btn-primary app-btn-sm"
                      onclick={() => openReplyModal(message)}
                    >
                      <ReplyOutline size="sm" aria-hidden="true" />
                      {$_("reply")}
                    </button>
                  {/if}
                </div>
              </div>

              {#if body.subject}
                <p class="text-sm text-text-muted mb-2">
                  <strong class="text-text">{$_("subject")}:</strong>
                  {body.subject}
                </p>
              {/if}

              {#if hasAttachment(message)}
                <div class="text-sm text-text-muted mb-2">
                  <strong class="text-text">{$_("attachments")}:</strong>
                  <ul class="mt-1 text-xs list-disc ps-5">
                    {#each body.attachments ?? [] as attachment, i (i)}
                      <li>{attachment.name || attachment.filename || $_("unknownFile")}</li>
                    {/each}
                  </ul>
                </div>
              {/if}

              <div class="rounded-control bg-surface-3 p-3">
                <h4 class="text-sm font-medium text-text mb-1">{$_("message")}</h4>
                <p class="text-sm text-text-muted whitespace-pre-wrap break-words">
                  {body.message || $_("noMessageContent")}
                </p>
              </div>

              {#if replied}
                <div class="rounded-control bg-success-soft p-3 mt-3">
                  <h4 class="text-sm font-medium text-success mb-1">{$_("reply")}</h4>
                  <p class="text-sm text-text whitespace-pre-wrap break-words">
                    {getReplyMessage(message) || $_("noReplyContent")}
                  </p>
                </div>
              {/if}
            </Card>
          </li>
        {/each}
      </ul>
    </LoadingState>

    {#if totalMessages > limit}
      <nav class="flex items-center justify-between gap-4 mt-6 pt-4 border-t border-border" aria-label={$_("contact_inbox.pagination")}>
        <button
          type="button"
          class="app-btn app-btn-secondary app-btn-sm"
          onclick={prevPage}
          disabled={currentPage === 0 || loading}
        >
          <ChevronLeftOutline size="sm" class="rtl:rotate-180" aria-hidden="true" />
          {$_("previous")}
        </button>

        <span class="text-sm text-text-muted tabular-nums" aria-current="page">
          {$_("contact_inbox.page_of", { values: { page: currentPage + 1, total: totalPages } })}
        </span>

        <button
          type="button"
          class="app-btn app-btn-secondary app-btn-sm"
          onclick={nextPage}
          disabled={(currentPage + 1) * limit >= totalMessages || loading}
        >
          {$_("next")}
          <ChevronRightOutline size="sm" class="rtl:rotate-180" aria-hidden="true" />
        </button>
      </nav>
    {/if}
  {/if}
</div>

{#if showModal && selectedMessage}
  {@const body = bodyOf(selectedMessage)}
  <Modal title={$_("replyToMessage")} size="xl" dismissable={!sendingReply} onClose={closeModal}>
    <div class="rounded-control bg-surface-3 p-3 mb-4 text-sm text-text-muted space-y-2">
      <p><strong class="text-text">{$_("name")}:</strong> {body.full_name || $_("anonymous")}</p>
      <p><strong class="text-text">{$_("email")}:</strong> {body.email || $_("noEmailProvided")}</p>
      <p><strong class="text-text">{$_("subject")}:</strong> {body.subject || $_("noSubject")}</p>
      <div>
        <strong class="text-text">{$_("originalMessage")}:</strong>
        <div class="mt-1 rounded-control border border-border bg-surface-2 p-2 max-h-24 overflow-y-auto whitespace-pre-wrap break-words text-text">
          {body.message || $_("noMessageContent")}
        </div>
      </div>
    </div>

    <form id="contact-reply-form" onsubmit={sendReply}>
      <label for="reply-content" class="block text-sm font-medium text-text mb-2">
        {$_("yourReply")}
      </label>
      <textarea
        id="reply-content"
        bind:value={replyContent}
        rows="6"
        class="w-full px-3 py-2 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary resize-none"
        placeholder={$_("typeReplyPlaceholder")}
        disabled={sendingReply}
        required
        data-autofocus
      ></textarea>
    </form>

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeModal} disabled={sendingReply}>
        {$_("cancel")}
      </button>
      <button
        type="submit"
        form="contact-reply-form"
        class="app-btn app-btn-primary"
        disabled={sendingReply}
        aria-busy={sendingReply}
      >
        {#if sendingReply}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("sending")}
        {:else}
          <PaperPlaneOutline size="sm" aria-hidden="true" />
          {$_("sendReply")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}
