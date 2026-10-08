<script lang="ts">
  import {
    deleteAllNotification,
    fetchMyNotifications,
    markNotification,
  } from "@/lib/dmart_services";
  import { getAvatarsCached } from "@/lib/dmart_services/avatars";
  import { user } from "@/stores/user";
  import { onMount } from "svelte";
  import Avatar from "@/components/Avatar.svelte";
  import { newNotificationType } from "@/stores/newNotificationType";
  import { ResourceType } from "@edraj/tsdmart";
  import {
    BellOutline,
    CheckCircleOutline,
    EyeOutline,
    EyeSlashOutline,
    RefreshOutline,
    TrashBinOutline,
  } from "flowbite-svelte-icons";

  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { setTitle } from "@/lib/title";
  import { confirm } from "@/lib/confirm";
  import { toasts } from "@/lib/toast";
  import { goto as gotoStore } from "@roxi/routify";
  import { wsStatus } from "@/stores/websocket";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  interface NotificationItem {
    shortname: string;
    created_at: string;
    action_by: string;
    entry_shortname?: string;
    entry_subpath?: string;
    entry_space?: string;
    parent_shortname?: string;
    parent_space_name?: string;
    parent_subpath?: string;
    resource_type: string;
    is_read: string;
  }

  let notifications = $state<NotificationItem[]>([]);
  let avatars = $state<Map<string, string | null>>(new Map());
  let isNotificationsLoading = $state(false);
  let hasLoaded = $state(false);
  let notificationError = $state<unknown>(null);
  let busy = $state(false);

  $effect(() => setTitle($_("Notifications")));

  const connectionLabel = $derived(
    $wsStatus === "connected"
      ? $_("notifications_page.connection.connected")
      : $wsStatus === "connecting"
        ? $_("notifications_page.connection.connecting")
        : $_("notifications_page.connection.disconnected"),
  );
  const connectionTone = $derived(
    $wsStatus === "connected" ? "success" : $wsStatus === "connecting" ? "warning" : "danger",
  );
  const unreadCount = $derived(notifications.filter((n) => n.is_read !== "yes").length);

  onMount(async () => {
    $newNotificationType = "";
    await loadNotifications();
  });

  // The shared websocket (stores/websocket.ts) already turns every create /
  // update broadcast into a `newNotificationType` change, so this effect is
  // the one reload per event: no page-level listener on top of it (perf #12).
  $effect(() => {
    if ($newNotificationType && hasLoaded) {
      loadNotifications(true).then(() => {
        $newNotificationType = "";
      });
    }
  });

  function toItem(record: { shortname: string; attributes: { created_at?: string; payload?: { body?: Record<string, unknown> } } }): NotificationItem {
    const body = record.attributes?.payload?.body ?? {};
    const str = (key: string) => (typeof body[key] === "string" ? (body[key] as string) : undefined);
    return {
      shortname: record.shortname,
      created_at: record.attributes?.created_at ?? "",
      action_by: str("action_by") ?? "",
      entry_shortname: str("entry_shortname"),
      entry_subpath: str("entry_subpath"),
      entry_space: str("entry_space"),
      parent_shortname: str("parent_shortname"),
      parent_space_name: str("parent_space_name"),
      parent_subpath: str("parent_subpath"),
      resource_type: str("resource_type") ?? "unknown",
      is_read: str("is_read") ?? "no",
    };
  }

  async function loadNotifications(force: boolean = false) {
    isNotificationsLoading = true;
    notificationError = null;

    try {
      const records = await fetchMyNotifications($user.shortname!);
      const next = records.map(toItem);

      if (notifications.length === 0 || force) {
        notifications = next;
      } else {
        const known = new Set(notifications.map((n) => n.shortname));
        const fresh = next.filter((n) => !known.has(n.shortname));
        const stillThere = new Set(next.map((n) => n.shortname));
        notifications = [...fresh, ...notifications.filter((n) => stillThere.has(n.shortname))];
      }

      // One lookup per distinct author, cached for the session.
      avatars = await getAvatarsCached(notifications.map((n) => n.action_by));
    } catch (error) {
      notificationError = error;
    }

    isNotificationsLoading = false;
    hasLoaded = true;
  }

  async function handleNotificationClick(notification: NotificationItem) {
    try {
      await markNotification($user.shortname!, notification.shortname);
      notification.is_read = "yes";

      if (notification.resource_type === ResourceType.ticket || notification.resource_type === "ticket") {
        // Report (ticket) notifications have no detail page to open yet.
        return;
      }

      const strip = (p: string) => (p.startsWith("/") ? p.substring(1) : p);
      if (notification.parent_shortname && notification.parent_space_name && notification.parent_subpath) {
        goto("/dashboard/admin/[space_name]/[subpath]/[shortname]/[resource_type]", {
          space_name: notification.parent_space_name,
          subpath: strip(notification.parent_subpath),
          shortname: notification.parent_shortname,
          resource_type: "content",
        });
      } else if (notification.entry_shortname && notification.entry_subpath) {
        goto("/dashboard/admin/[space_name]/[subpath]/[shortname]/[resource_type]", {
          space_name: notification.entry_space || "catalog",
          subpath: strip(notification.entry_subpath),
          shortname: notification.entry_shortname,
          resource_type: "content",
        });
      } else {
        goto("/dashboard/admin");
      }
    } catch {
      toasts.error($_("notifications_page.open_failed"));
    }
  }

  async function markAll(read: boolean) {
    busy = true;
    try {
      await Promise.all(
        notifications
          .filter((n) => (read ? n.is_read !== "yes" : n.is_read === "yes"))
          .map((n) => markNotification($user.shortname!, n.shortname, read)),
      );
      await loadNotifications(true);
      toasts.success(read ? $_("notifications_page.all_read") : $_("notifications_page.all_unread"));
    } catch {
      toasts.error(read ? $_("notifications_page.mark_read_failed") : $_("notifications_page.mark_unread_failed"));
    } finally {
      busy = false;
    }
  }

  async function handleDeleteAll() {
    const count = notifications.length;
    const confirmed = await confirm({
      title: $_("notifications_page.delete_all_title", { values: { count } }),
      body: $_("notifications_page.delete_all_body"),
      variant: "danger",
      action: () => deleteAllNotification($user.shortname!, notifications.map((n) => n.shortname)),
    });
    if (!confirmed) return;
    await loadNotifications(true);
    toasts.success($_("notifications_page.all_deleted"));
  }
</script>

<div class="mx-auto max-w-4xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("Notifications")} description={$_("NotificationsMsg")} icon={BellOutline}>
    {#snippet actions()}
      <Badge variant={connectionTone} size="sm">
        <span class="w-1.5 h-1.5 rounded-full bg-current" aria-hidden="true"></span>
        {connectionLabel}
      </Badge>
      <button
        type="button"
        class="app-btn app-btn-secondary app-btn-sm"
        onclick={() => loadNotifications(true)}
        disabled={isNotificationsLoading}
      >
        <RefreshOutline size="sm" aria-hidden="true" />
        {$_("Refresh")}
      </button>
      <button
        type="button"
        class="app-btn app-btn-secondary app-btn-sm"
        onclick={() => markAll(true)}
        disabled={busy || unreadCount === 0}
      >
        <EyeOutline size="sm" aria-hidden="true" />
        {$_("ReadAll")}
      </button>
      <button
        type="button"
        class="app-btn app-btn-secondary app-btn-sm"
        onclick={() => markAll(false)}
        disabled={busy || unreadCount === notifications.length}
      >
        <EyeSlashOutline size="sm" aria-hidden="true" />
        {$_("UnReadAll")}
      </button>
      <button
        type="button"
        class="app-btn app-btn-danger app-btn-sm"
        onclick={handleDeleteAll}
        disabled={busy || notifications.length === 0}
      >
        <TrashBinOutline size="sm" aria-hidden="true" />
        {$_("DeleteAll")}
      </button>
    {/snippet}
  </PageHeader>

  {#if notificationError}
    <ErrorState
      class="mb-6"
      compact
      title={$_("notifications_page.load_failed")}
      error={notificationError}
      onRetry={() => loadNotifications(true)}
    />
  {/if}

  {#if isNotificationsLoading && notifications.length === 0}
    <LoadingState label={$_("notifications_page.loading")} />
  {:else if notifications.length === 0 && !notificationError}
    <EmptyState icon={BellOutline} title={$_("NoNotifications")} hint={$_("NoNotificationsMsg")} />
  {:else}
    <LoadingState variant="overlay" loading={isNotificationsLoading}>
      <ul class="space-y-3 list-none p-0 m-0">
        {#each notifications as notification (notification.shortname)}
          {@const unread = notification.is_read !== "yes"}
          {@const isTicket =
            notification.resource_type === ResourceType.ticket || notification.resource_type === "ticket"}
          <li>
            <button
              type="button"
              class="w-full text-start flex gap-4 p-4 sm:p-5 rounded-card border bg-surface-2 shadow-card transition-[box-shadow,border-color] hover:shadow-modal hover:border-border-strong focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary cursor-pointer
 {unread ? 'border-primary/40' : 'border-border'}"
              aria-label={$_("notifications_page.open", { values: { name: notification.action_by } })}
              onclick={() => handleNotificationClick(notification)}
            >
              <span class="shrink-0">
                <Avatar src={avatars.get(notification.action_by)} size="48" />
              </span>

              <span class="flex-1 min-w-0 flex flex-col gap-1">
                <span class="flex items-center gap-2">
                  <span class="font-semibold text-text truncate">{notification.action_by}</span>
                  {#if unread}
                    <Badge variant="primary" size="sm">{$_("notifications_page.unread")}</Badge>
                  {/if}
                </span>

                <span class="text-sm text-text-muted">
                  {#if isTicket}
                    {$_("notifications_page.ticket_update")}
                  {:else}
                    {$_("notifications_page.notification")}
                  {/if}
                </span>

                <span class="text-xs text-text-faint tabular-nums">
                  {formatDate(notification.created_at, "relative", $locale) || $_("common.not_available")}
                </span>
              </span>

              {#if !unread}
                <span class="shrink-0 text-success" aria-hidden="true">
                  <CheckCircleOutline size="md" />
                </span>
              {/if}
            </button>
          </li>
        {/each}
      </ul>
    </LoadingState>
  {/if}
</div>
