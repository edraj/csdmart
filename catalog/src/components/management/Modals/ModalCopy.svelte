<script lang="ts">
  import { Modal } from "flowbite-svelte";
  import { _, locale } from "@/i18n";
  import { onMount } from "svelte";
  import {
    Dmart,
    DmartScope,
    RequestType,
    type ActionRequestRecord,
    type ApiQueryResponse,
  } from "@edraj/tsdmart";
  import { getSpaces, getSpaceFolders } from "@/lib/dmart_services";
  import {
    successToastMessage,
    errorToastMessage,
  } from "@/lib/toasts_messages";
  import { derived as derivedStore } from "svelte/store";

  type CopyMoveAction = "copy" | "move";

  interface Props {
    open: boolean;
    /** Records to act on. Each must carry `resource_type`, `shortname`, `subpath`, `attributes`. */
    records: any[];
    /** Which action to perform — "copy" (default) or "move". */
    action?: CopyMoveAction;
    /** Source space (required for move; used as default destination for copy). */
    sourceSpace: string;
    /** Default destination subpath (usually the current subpath). */
    defaultSubpath?: string;
    onClose: () => void;
    onDone?: () => void;
  }

  let {
    open = $bindable(false),
    records,
    action = "copy",
    sourceSpace,
    defaultSubpath = "/",
    onClose,
    onDone,
  }: Props = $props();

  const isRTL = derivedStore(
    locale,
    ($locale) => $locale === "ar" || $locale === "ku",
  );

  let spacesList = $state<any[]>([]);
  let folderList = $state<string[]>(["/"]);
  let selectedSpace = $state("");
  let selectedSubpath = $state("/");
  let isSubmitting = $state(false);
  let errorMessage: string | null = $state(null);

  const isMove = $derived(action === "move");
  const actionLabel = $derived(
    isMove ? ($_("admin_content.bulk_actions.move") || "Move") : ($_("admin_content.bulk_actions.copy") || "Copy"),
  );
  const submittingLabel = $derived(
    isMove ? ($_("admin_content.bulk_actions.moving") || "Moving...") : ($_("admin_content.bulk_actions.copying") || "Copying..."),
  );
  const title = $derived(
    records.length === 1
      ? `${actionLabel} item`
      : `${actionLabel} ${records.length} items`,
  );

  onMount(async () => {
    try {
      const spacesResponse = await getSpaces(false, DmartScope.managed);
      spacesList = spacesResponse.records ?? [];
    } catch (err) {
      console.error("Error loading spaces:", err);
    }
    selectedSpace = sourceSpace || spacesList[0]?.shortname || "";
    selectedSubpath = defaultSubpath || "/";
    await loadFoldersForSpace(selectedSpace);
  });

  async function loadFoldersForSpace(space: string) {
    if (!space) {
      folderList = ["/"];
      return;
    }
    try {
      // `exact_subpath: false` under the hood means getSpaceFolders walks the
      // whole tree, so records include deeply nested folders. Each record's
      // full path is `<subpath>/<shortname>`.
      const response: ApiQueryResponse = await getSpaceFolders(
        space,
        "/",
        DmartScope.managed,
        500,
        0,
      );
      const paths = (response?.records ?? [])
        .filter((r: any) => r.resource_type === "folder")
        .map((r: any) => {
          const parent = (r.subpath || "/")
            .replace(/^\/+/, "/")
            .replace(/\/+$/, "");
          const name = r.shortname;
          return parent === "" || parent === "/"
            ? `/${name}`
            : `${parent}/${name}`;
        });
      paths.sort((a: string, b: string) => a.localeCompare(b));
      folderList = ["/", ...paths];
    } catch (err) {
      console.error("Error loading folders:", err);
      folderList = ["/"];
    }
  }

  async function handleSpaceChange(e: Event) {
    const value = (e.target as HTMLSelectElement).value;
    selectedSpace = value;
    selectedSubpath = "/";
    await loadFoldersForSpace(value);
  }

  function normalizeSubpath(raw: string): string {
    const trimmed = (raw ?? "").trim();
    if (!trimmed) return "/";
    return trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
  }

  async function handleSubmit() {
    if (!selectedSpace) {
      errorMessage = "Choose a destination space";
      return;
    }
    if (!records || records.length === 0) {
      errorMessage = "Nothing to " + action;
      return;
    }

    errorMessage = null;
    isSubmitting = true;

    const destSubpath = normalizeSubpath(selectedSubpath);

    try {
      if (isMove) {
        // Move: src_* + dest_* attributes, sent against the SOURCE space.
        // Mirrors csdmart/cxb's move payload.
        const payload: ActionRequestRecord[] = records.map((r: any) => ({
          resource_type: r.resource_type,
          shortname: r.shortname,
          subpath: r.subpath || destSubpath,
          attributes: {
            src_space_name: sourceSpace,
            src_subpath: r.subpath,
            src_shortname: r.shortname,
            dest_space_name: selectedSpace,
            dest_subpath: destSubpath,
            dest_shortname: r.shortname,
          },
        }));

        const response = await Dmart.request({
          space_name: sourceSpace,
          request_type: RequestType.move,
          records: payload,
        });

        if (response?.status === "success") {
          successToastMessage(
            $_("admin_content.bulk_actions.move_success", {
              values: { count: records.length },
            }) || `Moved ${records.length} item(s)`,
          );
          onDone?.();
          onClose();
          return;
        }
        errorMessage =
          (response as any)?.error?.message || "Move failed";
        errorToastMessage(errorMessage || "Move failed");
      } else {
        // Copy: create at destination, strip uuid so the server generates
        // a fresh identity instead of colliding with the source.
        const payload: ActionRequestRecord[] = records.map((r: any) => {
          const { uuid: _uuid, ...cleanAttributes } = r.attributes ?? {};
          return {
            resource_type: r.resource_type,
            shortname: r.shortname,
            subpath: destSubpath,
            attributes: cleanAttributes,
          };
        });

        const response = await Dmart.request({
          space_name: selectedSpace,
          request_type: RequestType.create,
          records: payload,
        });

        if (response?.status === "success") {
          successToastMessage(
            $_("admin_content.bulk_actions.copy_success", {
              values: { count: records.length },
            }) || `Copied ${records.length} item(s)`,
          );
          onDone?.();
          onClose();
          return;
        }
        errorMessage =
          (response as any)?.error?.message ||
          "Copy failed. The destination may already contain items with the same shortnames.";
        errorToastMessage(errorMessage || "Copy failed");
      }
    } catch (err: any) {
      console.error(`${action} error:`, err);
      errorMessage =
        err?.response?.data?.error?.message ||
        err?.message ||
        `${actionLabel} failed`;
      errorToastMessage(errorMessage || `${actionLabel} failed`);
    } finally {
      isSubmitting = false;
    }
  }
</script>

<Modal
  {title}
  bind:open
  size="lg"
  class="bg-white dark:bg-white max-h-[90vh]"
  headerClass="text-gray-900 dark:text-gray-900"
  bodyClass="bg-white dark:bg-white text-gray-700 p-4 md:p-5 space-y-4 overflow-y-auto overscroll-contain max-h-[70vh]"
  footerClass="bg-white dark:bg-white flex items-center p-4 md:p-5 space-x-3 rtl:space-x-reverse rounded-b-lg shrink-0"
  placement="center"
  autoclose={false}
  dismissable={!isSubmitting}
>
  <p class="text-sm text-gray-500 -mt-2">
    {#if isMove}
      The {records.length === 1 ? "item" : "items"} will be relocated to the
      destination. The source entries will no longer appear in this folder.
    {:else}
      The {records.length === 1 ? "item" : "items"} will be copied to the
      destination. New entries receive fresh UUIDs.
    {/if}
  </p>

  {#if errorMessage}
    <div class="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
      {errorMessage}
    </div>
  {/if}

  <div class="grid grid-cols-1 gap-3">
    <div>
      <label for="copy-space" class="block text-xs font-semibold text-gray-600 uppercase tracking-wide mb-1">
        Destination space
      </label>
      <select
        id="copy-space"
        class="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm focus:ring-2 focus:ring-indigo-500"
        value={selectedSpace}
        onchange={handleSpaceChange}
        disabled={isSubmitting}
      >
        {#if spacesList.length === 0}
          <option value="">Loading…</option>
        {/if}
        {#each spacesList as space}
          <option value={space.shortname}>{space.shortname}</option>
        {/each}
      </select>
    </div>

    <div>
      <label for="copy-subpath" class="block text-xs font-semibold text-gray-600 uppercase tracking-wide mb-1">
        Destination subpath
      </label>
      <select
        id="copy-subpath"
        class="w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm focus:ring-2 focus:ring-indigo-500 font-mono"
        bind:value={selectedSubpath}
        disabled={isSubmitting || folderList.length === 0}
      >
        {#each folderList as folder}
          <option value={folder}>{folder}</option>
        {/each}
      </select>
    </div>
  </div>

  <div class="rounded-xl border border-gray-100 bg-gray-50 px-4 py-3 text-xs text-gray-600 max-h-40 overflow-y-auto">
    <div class="font-semibold text-gray-700 mb-1">Items ({records.length})</div>
    <ul class="space-y-0.5 font-mono {$isRTL ? 'text-right' : ''}">
      {#each records as r}
        <li class="truncate">
          <span class="text-gray-400">[{r.resource_type}]</span>
          {r.shortname}
        </li>
      {/each}
    </ul>
  </div>

  {#snippet footer()}
    <button
      onclick={onClose}
      class="rounded-lg border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
      disabled={isSubmitting}
    >
      Cancel
    </button>
    <button
      onclick={handleSubmit}
      disabled={isSubmitting || !selectedSpace || records.length === 0}
      class="inline-flex items-center gap-2 rounded-lg {isMove
        ? 'bg-amber-600 hover:bg-amber-700'
        : 'bg-indigo-600 hover:bg-indigo-700'} px-4 py-2 text-sm font-semibold text-white disabled:opacity-60"
    >
      {#if isSubmitting}
        <span class="h-4 w-4 animate-spin rounded-full border-2 border-white/40 border-t-white"></span>
        {submittingLabel}
      {:else}
        {actionLabel}
      {/if}
    </button>
  {/snippet}
</Modal>
