<script lang="ts">
  import { ExclamationCircleOutline } from "flowbite-svelte-icons";
  import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import { _ } from "@/i18n";

  // The delete dialog used by the dashboard pages. Same props as before; now a
  // thin layer over ui/ConfirmDialog (tokens, focus trap, Escape/overlay).
  interface Props {
    open?: boolean;
    title?: string;
    itemName?: string;
    itemType?: string;
    isDeleting?: boolean;
    onConfirm?: (force: boolean) => void;
    onCancel?: () => void;
  }

  let {
    open = $bindable(false),
    title = "",
    itemName = "",
    itemType = "item",
    isDeleting = false,
    onConfirm = () => {},
    onCancel = () => {},
  }: Props = $props();

  let forceDelete = $state(false);
  const showForce = $derived(itemType === "folder" || itemType === "user");
  // Reset the opt-in each time the dialog opens.
  $effect(() => {
    if (open) forceDelete = false;
  });

  const displayTitle = $derived(
    title || $_("delete_confirmation.title", { values: { type: itemType } }),
  );
  const uid = $props.id();
</script>

<ConfirmDialog
  bind:open
  title={displayTitle}
  variant="danger"
  loading={isDeleting}
  loadingLabel={$_("deleting")}
  confirmLabel={$_("delete")}
  cancelLabel={$_("cancel")}
  onConfirm={() => onConfirm(showForce && forceDelete)}
  {onCancel}
>
  <p class="text-sm text-text-muted">{$_("delete_confirmation.irreversible")}</p>

  <div class="flex items-start gap-3 rounded-card border border-danger/30 bg-danger-soft px-4 py-3">
    <ExclamationCircleOutline size="md" class="shrink-0 mt-0.5 text-danger" aria-hidden="true" />
    <div class="flex-1 min-w-0">
      <h4 class="text-sm font-semibold text-text">{$_("delete_confirmation.confirm")}</h4>
      <p class="mt-1 text-sm text-text-muted">{$_("delete_confirmation.warning")}</p>
    </div>
  </div>

  <div class="rounded-card border border-border bg-surface px-4 py-3 text-sm flex flex-wrap items-center gap-2">
    <span class="font-semibold text-text-muted">{$_("delete_confirmation.item_label")}:</span>
    <span class="break-all text-text">{itemName}</span>
    {#if itemType}
      <Badge size="sm">{itemType}</Badge>
    {/if}
  </div>

  {#if showForce}
    <label
      for="{uid}-force"
      class="flex items-start gap-2 rounded-card border border-warning/40 bg-warning-soft px-4 py-3 text-sm cursor-pointer"
    >
      <input
        id="{uid}-force"
        type="checkbox"
        bind:checked={forceDelete}
        disabled={isDeleting}
        class="mt-0.5 accent-[var(--color-warning)]"
      />
      <span>
        <span class="font-semibold text-text">{$_("force_delete")}</span>
        <span class="block text-text-muted">{$_("force_delete_help")}</span>
      </span>
    </label>
  {/if}
</ConfirmDialog>
