<script lang="ts">
    import {
        Dmart,
        type ActionRequestRecord,
        type ApiResponseRecord,
        type QueryRequest,
        type ResponseEntry,
        QueryType,
        RequestType,
        ResourceType,
    } from "@edraj/tsdmart";
    import {
        Button,
        Checkbox,
        Input,
        Label,
        Select,
        Spinner,
        Table,
        TableBody,
        TableBodyCell,
        TableBodyRow,
        TableHead,
        TableHeadCell,
    } from "flowbite-svelte";
    import { CloseOutline, RefreshOutline, SearchOutline, TrashBinOutline, UserRemoveOutline } from "flowbite-svelte-icons";
    import { _ } from "@/i18n";
    import Prism from "@/components/Prism.svelte";
    import { getSpaces } from "@/lib/dmart_services";
    import { deleteEntry } from "@/utils/entryManagement";
    import { checkAccess } from "@/utils/checkAccess";
    import { Level, showToast } from "@/utils/toast";
    import { errorMessage } from "@/utils/errorMessage";
    import { formatNumber } from "@/utils/format";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import Badge from "@/components/ui/Badge.svelte";
    import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
    import IconButton from "@/components/ui/IconButton.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";

    type SearchType = "shortname" | "email" | "msisdn";

    type OwnedEntry = {
        key: string;
        space_name: string;
        subpath: string;
        shortname: string;
        resource_type: ResourceType;
        raw: unknown;
    };

    type PendingDelete = {
        targets: OwnedEntry[];
        mode: "one" | "many" | "retry";
    };

    type FailedDelete = {
        entry: OwnedEntry;
        error: string;
    };

    let searchType: SearchType = $state("shortname");
    let searchValue: string = $state("");

    let userMatches: ApiResponseRecord[] = $state([]);
    let userSearched: boolean = $state(false);
    let selectedUserShortname: string | null = $state(null);

    let userEntry = $state<ResponseEntry | null>(null);
    let ownedEntries: OwnedEntry[] = $state([]);
    let ownedSearched: boolean = $state(false);

    let selectedKeys: Record<string, boolean> = $state({});
    let batchSize: number = $state(10);

    let isSearching: boolean = $state(false);
    let isFetching: boolean = $state(false);
    let isDeleting: boolean = $state(false);
    let deleteProgress: { done: number; total: number } = $state({ done: 0, total: 0 });

    let confirmOpen: boolean = $state(false);
    let pendingDelete = $state<PendingDelete | null>(null);

    let failedDeletes: FailedDelete[] = $state([]);
    let lastRunSucceeded: number = $state(0);
    let lastRunFailed: number = $state(0);
    let showLastRunReport: boolean = $state(false);

    let deleteUserOpen: boolean = $state(false);
    let isDeletingUser: boolean = $state(false);

    let lastFetchedShortname: string | null = $state(null);
    let fetchedAll: boolean = $state(false);

    const OWNED_PAGE_SIZE = 1000;

    const selectedUser = $derived(userMatches.find((u) => u.shortname === selectedUserShortname) ?? null);
    const selectedTargets = $derived(ownedEntries.filter((e) => selectedKeys[e.key]));
    const allSelected = $derived(ownedEntries.length > 0 && ownedEntries.every((e) => selectedKeys[e.key]));

    let forceDelete: boolean = $state(false);

    const showForce = $derived(
        (pendingDelete?.targets ?? []).some(
            (t) => t.resource_type === ResourceType.folder || t.resource_type === ResourceType.user,
        ),
    );

    const effectiveBatch = $derived(Math.max(1, Math.floor(batchSize) || 1));

    function contactLine(match: ApiResponseRecord): string {
        const attrs = (match.attributes ?? {}) as { payload?: { body?: Record<string, unknown> }; email?: string; msisdn?: string };
        const email = (attrs.payload?.body?.email as string | undefined) ?? attrs.email ?? "";
        const msisdn = (attrs.payload?.body?.msisdn as string | undefined) ?? attrs.msisdn ?? "";
        return [email, msisdn].filter(Boolean).join(" · ");
    }

    async function handleSearchUser() {
        if (!searchValue.trim()) return;
        isSearching = true;
        userEntry = null;
        ownedEntries = [];
        ownedSearched = false;
        selectedKeys = {};
        selectedUserShortname = null;
        lastFetchedShortname = null;
        failedDeletes = [];
        showLastRunReport = false;
        try {
            const query: QueryRequest = {
                type: QueryType.search,
                space_name: "management",
                subpath: "users",
                exact_subpath: true,
                search: `@${searchType}:${searchValue.trim()}`,
                retrieve_json_payload: true,
                limit: 50,
                offset: 0,
            };
            const res = await Dmart.query(query);
            userMatches = res?.records ?? [];
            userSearched = true;
            if (userMatches.length === 1) {
                selectedUserShortname = userMatches[0].shortname;
            }
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, $_("user_search_failed")));
            userMatches = [];
            userSearched = true;
        } finally {
            isSearching = false;
        }
    }

    async function handleFetch() {
        if (!selectedUser) return;
        isFetching = true;
        ownedEntries = [];
        selectedKeys = {};
        try {
            userEntry = await Dmart.retrieveEntry({
                resource_type: ResourceType.user,
                space_name: "management",
                subpath: "users",
                shortname: selectedUser.shortname,
                retrieve_json_payload: true,
                retrieve_attachments: false,
                validate_schema: null,
            });
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, $_("user_fetch_failed")));
            userEntry = null;
            isFetching = false;
            return;
        }
        await loadOwnedEntries(selectedUser.shortname);
        isFetching = false;
    }

    async function loadOwnedEntries(userShortname: string) {
        ownedSearched = false;
        fetchedAll = false;
        let allFetched = true;
        try {
            const spacesResp = await getSpaces();
            const spaceList = spacesResp?.records ?? [];
            const results = await Promise.all(
                spaceList.map(async (space) => {
                    // Paginate per space — a user owning more than one page of
                    // entries in a single space must not be silently truncated.
                    // We stop when a page returns fewer than the page size, or
                    // when one page throws (and mark allFetched=false so the
                    // post-delete user-account prompt can't false-positive).
                    const acc: OwnedEntry[] = [];
                    let offset = 0;
                    while (true) {
                        try {
                            const res = await Dmart.query({
                                type: QueryType.search,
                                space_name: space.shortname,
                                subpath: "/",
                                exact_subpath: false,
                                search: `@owner_shortname:${userShortname}`,
                                retrieve_json_payload: false,
                                limit: OWNED_PAGE_SIZE,
                                offset,
                            });
                            const records = res?.records ?? [];
                            for (const r of records) {
                                acc.push({
                                    key: `${space.shortname}|${r.subpath ?? "/"}|${r.shortname}`,
                                    space_name: space.shortname,
                                    subpath: r.subpath ?? "/",
                                    shortname: r.shortname,
                                    resource_type: r.resource_type as ResourceType,
                                    raw: r,
                                });
                            }
                            if (records.length < OWNED_PAGE_SIZE) break;
                            offset += OWNED_PAGE_SIZE;
                        } catch {
                            allFetched = false;
                            break;
                        }
                    }
                    return acc;
                }),
            );
            ownedEntries = results.flat();
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, $_("owned_entries_load_failed")));
            ownedEntries = [];
            allFetched = false;
        }
        fetchedAll = allFetched;
        ownedSearched = true;
    }

    function toggleAll(checked: boolean) {
        const next: Record<string, boolean> = {};
        if (checked) {
            for (const e of ownedEntries) next[e.key] = true;
        }
        selectedKeys = next;
    }

    function askDelete(targets: OwnedEntry[], mode: PendingDelete["mode"]) {
        if (targets.length === 0) return;
        forceDelete = false;
        pendingDelete = { targets, mode };
        confirmOpen = true;
    }

    function chunk<T>(arr: T[], size: number): T[][] {
        const out: T[][] = [];
        const s = Math.max(1, Math.floor(size) || 1);
        for (let i = 0; i < arr.length; i += s) {
            out.push(arr.slice(i, i + s));
        }
        return out;
    }

    async function deleteChunkGrouped(c: OwnedEntry[]): Promise<{ succeededKeys: Set<string>; failures: FailedDelete[] }> {
        // eslint-disable-next-line svelte/prefer-svelte-reactivity -- plain local accumulator, never rendered
        const succeededKeys = new Set<string>();
        const failures: FailedDelete[] = [];

        // eslint-disable-next-line svelte/prefer-svelte-reactivity -- plain local accumulator, never rendered
        const bySpace = new Map<string, OwnedEntry[]>();
        for (const entry of c) {
            const list = bySpace.get(entry.space_name) ?? [];
            list.push(entry);
            bySpace.set(entry.space_name, list);
        }

        await Promise.all(
            Array.from(bySpace.entries()).map(async ([space_name, items]) => {
                const records: ActionRequestRecord[] = items.map((e) => ({
                    resource_type: e.resource_type,
                    shortname: e.shortname,
                    // `entry.subpath` is the parent path returned by the query
                    // response — already what Dmart.request expects.
                    subpath: e.subpath || "/",
                    attributes: {},
                }));
                try {
                    const request = {
                        space_name,
                        request_type: RequestType.delete,
                        force: showForce && forceDelete,
                        records,
                    } as unknown as Parameters<typeof Dmart.request>[0];
                    const response = await Dmart.request(request);
                    if (response?.status === "success") {
                        for (const e of items) succeededKeys.add(e.key);
                    } else {
                        const err = errorMessage(response?.error ?? response, $_("unknown_error"));
                        for (const e of items) failures.push({ entry: e, error: err });
                    }
                } catch (err: unknown) {
                    const msg = errorMessage(err, $_("unknown_error"));
                    for (const e of items) failures.push({ entry: e, error: msg });
                }
            }),
        );

        return { succeededKeys, failures };
    }

    async function confirmDelete() {
        if (!pendingDelete) return;
        const targets = pendingDelete.targets;
        isDeleting = true;
        deleteProgress = { done: 0, total: targets.length };
        const chunks = chunk(targets, batchSize);
        // eslint-disable-next-line svelte/prefer-svelte-reactivity -- plain local accumulator, never rendered
        const deletedKeys = new Set<string>();
        const newFailures: FailedDelete[] = [];
        for (const c of chunks) {
            const { succeededKeys, failures } = await deleteChunkGrouped(c);
            for (const k of succeededKeys) deletedKeys.add(k);
            for (const f of failures) newFailures.push(f);
            deleteProgress = { done: deleteProgress.done + c.length, total: targets.length };
        }
        if (newFailures.length > 0) {
            showToast(Level.warn, $_("deletes_failed", { values: { count: newFailures.length } }));
        } else if (targets.length > 0) {
            showToast(Level.info, $_("entries_deleted", { values: { count: targets.length } }));
        }

        const targetKeys = new Set(targets.map((t) => t.key));
        const carriedFailures = failedDeletes.filter((f) => !targetKeys.has(f.entry.key));
        failedDeletes = [...carriedFailures, ...newFailures];

        lastRunSucceeded = targets.length - newFailures.length;
        lastRunFailed = newFailures.length;
        showLastRunReport = true;

        ownedEntries = ownedEntries.filter((e) => !deletedKeys.has(e.key));
        const remainingSelection: Record<string, boolean> = {};
        for (const k of Object.keys(selectedKeys)) {
            if (!deletedKeys.has(k) && selectedKeys[k]) remainingSelection[k] = true;
        }
        selectedKeys = remainingSelection;
        isDeleting = false;
        confirmOpen = false;
        pendingDelete = null;

        maybePromptDeleteUser();
    }

    function maybePromptDeleteUser() {
        // `fetchedAll` is required so a paginated search that bailed out part-way
        // (or hit an error mid-pages) can't trigger a false "all entries gone"
        // signal. The in-memory ownedEntries list reflects only what we saw.
        if (
            userEntry &&
            selectedUser &&
            fetchedAll &&
            ownedEntries.length === 0 &&
            failedDeletes.length === 0 &&
            lastRunFailed === 0 &&
            lastRunSucceeded > 0
        ) {
            deleteUserOpen = true;
        }
    }

    async function confirmDeleteUser() {
        const entry = userEntry;
        if (!selectedUser || isDeletingUser || !entry) return;
        isDeletingUser = true;
        const shortname = selectedUser.shortname;
        const result = await deleteEntry(entry, "management", "users", ResourceType.user);
        isDeletingUser = false;
        deleteUserOpen = false;

        if (result.success) {
            userEntry = null;
            userMatches = [];
            userSearched = false;
            ownedSearched = false;
            ownedEntries = [];
            selectedUserShortname = null;
            lastFetchedShortname = null;
            searchValue = "";
            showLastRunReport = false;
            lastRunSucceeded = 0;
            lastRunFailed = 0;
        } else {
            failedDeletes = [
                ...failedDeletes,
                {
                    entry: {
                        key: `management|users|${shortname}`,
                        space_name: "management",
                        subpath: "users",
                        shortname,
                        resource_type: ResourceType.user,
                        raw: userEntry,
                    },
                    error: errorMessage(result.errorMessage, $_("unknown_error")),
                },
            ];
            lastRunFailed = lastRunFailed + 1;
            showLastRunReport = true;
        }
    }

    function cancelDelete() {
        if (isDeleting) return;
        confirmOpen = false;
        pendingDelete = null;
        forceDelete = false;
    }

    function canDeleteEntry(entry: OwnedEntry): boolean {
        return checkAccess("delete", entry.space_name, entry.subpath, entry.resource_type);
    }

    $effect(() => {
        const sn = selectedUserShortname;
        if (!sn || sn === lastFetchedShortname || isFetching) return;
        lastFetchedShortname = sn;
        handleFetch();
    });

    const confirmBody = $derived.by(() => {
        if (!pendingDelete) return "";
        if (pendingDelete.mode === "one") return $_("confirm_delete_one");
        const values = { count: pendingDelete.targets.length, batch: effectiveBatch };
        return pendingDelete.mode === "retry"
            ? $_("confirm_retry_failed", { values })
            : $_("confirm_delete_many", { values });
    });
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title={$_("entry_deletion")}
        description={$_("entry_deletion_description")}
        icon={UserRemoveOutline}
        iconTone="danger"
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    />

    <div class="space-y-4">
        <!-- Search card -->
        <Card>
            <form
                class="grid grid-cols-1 md:grid-cols-12 gap-4 items-end"
                onsubmit={(e) => { e.preventDefault(); void handleSearchUser(); }}
            >
                <div class="md:col-span-3">
                    <Label for="search_type" class="mb-2">{$_("search_user_by")}</Label>
                    <Select id="search_type" bind:value={searchType}>
                        <option value="shortname">{$_("shortname")}</option>
                        <option value="email">{$_("email")}</option>
                        <option value="msisdn">{$_("msisdn")}</option>
                    </Select>
                </div>
                <div class="md:col-span-7">
                    <Label for="search_value" class="mb-2">{$_("search")}</Label>
                    <Input id="search_value" type="text" bind:value={searchValue} placeholder={$_(searchType)} />
                </div>
                <div class="md:col-span-2">
                    <Button type="submit" class="w-full" color="primary" disabled={isSearching || !searchValue.trim()}>
                        {#if isSearching}
                            <Spinner class="me-2" size="4" />
                        {:else}
                            <SearchOutline size="sm" class="me-2" aria-hidden="true" />
                        {/if}
                        {$_("search")}
                    </Button>
                </div>
            </form>
        </Card>

        <!-- Matches list -->
        {#if userSearched}
            {#if userMatches.length === 0}
                <EmptyState title={$_("no_users_found")} />
            {:else}
                <Card>
                    <h3 class="text-base font-semibold text-text mb-3">
                        {$_("select_user_match")} <Badge size="sm">{formatNumber(userMatches.length)}</Badge>
                    </h3>
                    <div class="flex flex-col gap-2 max-h-64 overflow-auto" role="radiogroup" aria-label={$_("select_user_match")}>
                        {#each userMatches as match (match.shortname)}
                            <label class="flex items-center gap-3 p-2 rounded-control border border-border hover:bg-surface-3 cursor-pointer">
                                <input type="radio" name="user_match" value={match.shortname} bind:group={selectedUserShortname} />
                                <span class="flex flex-col min-w-0">
                                    <span class="font-medium text-text">{match.shortname}</span>
                                    {#if contactLine(match)}
                                        <span class="text-xs text-text-muted truncate" dir="ltr">{contactLine(match)}</span>
                                    {/if}
                                </span>
                            </label>
                        {/each}
                    </div>
                    {#if isFetching}
                        <p class="mt-4 text-sm text-text-muted text-end flex items-center justify-end gap-2" role="status">
                            <Spinner size="4" /> {$_("loading")}
                        </p>
                    {/if}
                </Card>
            {/if}
        {/if}

        <!-- User JSON view -->
        {#if userEntry}
            <Card>
                <h3 class="text-base font-semibold text-text mb-3">{$_("details")}</h3>
                <div class="max-h-96 overflow-auto">
                    <Prism code={userEntry as object} />
                </div>
            </Card>
        {/if}

        <!-- Owned entries table + batch + bulk actions -->
        {#if userEntry && ownedSearched}
            <Card>
                <div class="flex flex-col md:flex-row md:items-end md:justify-between gap-4 mb-4">
                    <h3 class="text-base font-semibold text-text">
                        {$_("owned_entries")} <Badge size="sm">{formatNumber(ownedEntries.length)}</Badge>
                    </h3>
                    <div class="flex flex-wrap items-end gap-3">
                        <div>
                            <Label for="batch_size" class="mb-2">{$_("batch_size")}</Label>
                            <Input id="batch_size" type="number" min="1" max="100" class="w-24" bind:value={batchSize} />
                        </div>
                        <Button color="red" onclick={() => askDelete(selectedTargets, "many")} disabled={selectedTargets.length === 0 || isDeleting}>
                            <TrashBinOutline size="sm" class="me-2" aria-hidden="true" />
                            {$_("delete_selected_count", { values: { count: selectedTargets.length } })}
                        </Button>
                        <Button color="red" outline onclick={() => askDelete([...ownedEntries], "many")} disabled={ownedEntries.length === 0 || isDeleting}>
                            <TrashBinOutline size="sm" class="me-2" aria-hidden="true" />
                            {$_("delete_all_owned")}
                        </Button>
                    </div>
                </div>

                {#if isDeleting}
                    <p class="text-sm text-text-muted mb-3" role="status">
                        {$_("deleting_progress", { values: { done: deleteProgress.done, total: deleteProgress.total } })}
                    </p>
                {/if}

                {#if ownedEntries.length === 0}
                    <EmptyState title={$_("no_owned_entries")} />
                {:else}
                    <div class="overflow-x-auto">
                        <Table>
                            <TableHead>
                                <TableHeadCell class="w-10">
                                    <Checkbox
                                        checked={allSelected}
                                        aria-label={$_("select_all")}
                                        onchange={(e: Event) => toggleAll((e.currentTarget as HTMLInputElement).checked)}
                                    />
                                </TableHeadCell>
                                <TableHeadCell>{$_("space_name")}</TableHeadCell>
                                <TableHeadCell>{$_("subpath")}</TableHeadCell>
                                <TableHeadCell>{$_("shortname")}</TableHeadCell>
                                <TableHeadCell>{$_("resource_type")}</TableHeadCell>
                                <TableHeadCell><span class="sr-only">{$_("actions")}</span></TableHeadCell>
                            </TableHead>
                            <TableBody>
                                {#each ownedEntries as entry (entry.key)}
                                    <TableBodyRow>
                                        <TableBodyCell>
                                            <Checkbox
                                                checked={!!selectedKeys[entry.key]}
                                                aria-label={$_("select_entry", { values: { shortname: entry.shortname } })}
                                                onchange={(e: Event) => {
                                                    selectedKeys = { ...selectedKeys, [entry.key]: (e.currentTarget as HTMLInputElement).checked };
                                                }}
                                            />
                                        </TableBodyCell>
                                        <TableBodyCell>{entry.space_name}</TableBodyCell>
                                        <TableBodyCell><span class="font-mono text-xs" dir="ltr">{entry.subpath}</span></TableBodyCell>
                                        <TableBodyCell>{entry.shortname}</TableBodyCell>
                                        <TableBodyCell>{entry.resource_type}</TableBodyCell>
                                        <TableBodyCell>
                                            <IconButton
                                                label={canDeleteEntry(entry) ? $_("delete") : $_("no_delete_permission")}
                                                variant="danger"
                                                size="sm"
                                                disabled={isDeleting || !canDeleteEntry(entry)}
                                                onclick={() => askDelete([entry], "one")}
                                            >
                                                <TrashBinOutline size="sm" />
                                            </IconButton>
                                        </TableBodyCell>
                                    </TableBodyRow>
                                {/each}
                            </TableBody>
                        </Table>
                    </div>
                {/if}
            </Card>
        {/if}

        <!-- Last run report + failures -->
        {#if showLastRunReport || failedDeletes.length > 0}
            <Card>
                {#if showLastRunReport}
                    <div class="flex items-start justify-between gap-4 mb-3">
                        <div>
                            <h3 class="text-base font-semibold text-text">{$_("delete_results")}</h3>
                            <p class="text-sm mt-1 flex items-center gap-2">
                                <Badge variant="success" size="sm">{$_("succeeded")}: {formatNumber(lastRunSucceeded)}</Badge>
                                <Badge variant="danger" size="sm">{$_("failed")}: {formatNumber(lastRunFailed)}</Badge>
                            </p>
                        </div>
                        <IconButton label={$_("dismiss")} size="sm" onclick={() => (showLastRunReport = false)}>
                            <CloseOutline size="sm" />
                        </IconButton>
                    </div>
                {/if}

                {#if failedDeletes.length > 0}
                    <div class="flex flex-wrap items-end justify-between gap-3 mb-3">
                        <h4 class="text-sm font-semibold text-danger">
                            {$_("failed_deletes")} ({formatNumber(failedDeletes.length)})
                        </h4>
                        <div class="flex gap-2">
                            <Button size="xs" color="alternative" onclick={() => (failedDeletes = [])} disabled={isDeleting}>
                                {$_("clear_failed")}
                            </Button>
                            <Button size="xs" color="red" onclick={() => askDelete(failedDeletes.map((f) => f.entry), "retry")} disabled={isDeleting}>
                                <RefreshOutline size="sm" class="me-2" aria-hidden="true" />
                                {$_("retry_failed")}
                            </Button>
                        </div>
                    </div>
                    <div class="overflow-x-auto">
                        <Table>
                            <TableHead>
                                <TableHeadCell>{$_("space_name")}</TableHeadCell>
                                <TableHeadCell>{$_("subpath")}</TableHeadCell>
                                <TableHeadCell>{$_("shortname")}</TableHeadCell>
                                <TableHeadCell>{$_("resource_type")}</TableHeadCell>
                                <TableHeadCell class="w-1/3">{$_("error")}</TableHeadCell>
                            </TableHead>
                            <TableBody>
                                {#each failedDeletes as f (f.entry.key)}
                                    <TableBodyRow>
                                        <TableBodyCell>{f.entry.space_name}</TableBodyCell>
                                        <TableBodyCell><span class="font-mono text-xs" dir="ltr">{f.entry.subpath}</span></TableBodyCell>
                                        <TableBodyCell>{f.entry.shortname}</TableBodyCell>
                                        <TableBodyCell>{f.entry.resource_type}</TableBodyCell>
                                        <TableBodyCell class="text-danger text-xs break-all">{f.error}</TableBodyCell>
                                    </TableBodyRow>
                                {/each}
                            </TableBody>
                        </Table>
                    </div>
                {/if}
            </Card>
        {/if}
    </div>
</div>

<!-- Confirm: one entry, many, or a retry -->
<ConfirmDialog
    bind:open={confirmOpen}
    title={$_("confirm")}
    body={confirmBody}
    variant="danger"
    loading={isDeleting}
    loadingLabel={$_("deleting_progress", { values: { done: deleteProgress.done, total: deleteProgress.total } })}
    onConfirm={confirmDelete}
    onCancel={cancelDelete}
>
    {#if pendingDelete?.mode === "one"}
        {@const t = pendingDelete.targets[0]}
        <p class="text-sm text-text font-mono break-all" dir="ltr">
            {t.space_name} / {t.subpath} / <span class="font-bold">{t.shortname}</span>
        </p>
    {/if}
    {#if showForce && !isDeleting}
        <label class="flex items-start gap-2 text-sm cursor-pointer">
            <input type="checkbox" bind:checked={forceDelete} class="mt-0.5" />
            <span>
                <span class="font-semibold text-text">{$_("force_delete")}</span>
                <span class="block text-text-muted">{$_("force_delete_help")}</span>
            </span>
        </label>
    {/if}
</ConfirmDialog>

<!-- Delete the user account too (auto-prompts after a clean run) -->
<ConfirmDialog
    bind:open={deleteUserOpen}
    title={$_("delete_user")}
    body={$_("confirm_delete_user")}
    variant="danger"
    confirmLabel={$_("delete_user")}
    cancelLabel={$_("keep_user")}
    loading={isDeletingUser}
    loadingLabel={$_("deleting")}
    onConfirm={confirmDeleteUser}
>
    {#if selectedUser}
        <p class="text-sm font-bold text-text">{selectedUser.shortname}</p>
    {/if}
</ConfirmDialog>
