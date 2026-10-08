<script lang="ts">
    import { resolveTotal } from "@shared/query-total";
    import { functionCreateDatatable, Sort } from "@/components/management/datatable";
    import Pagination from "@/components/ui/Pagination.svelte";
    import { rowKey } from "@/utils/rowKey";
    import { Dmart, DmartScope, type ApiResponseRecord, type QueryRequest, QueryType, SortyType } from "@edraj/tsdmart";
    import cols from "@/utils/jsons/list_cols.json";
    import { searchListView } from "@/stores/management/triggers";
    import Prism from "@/components/Prism.svelte";
    import { goto, params, url } from "@roxi/routify";
    import { isDeepEqual } from "@/utils/compare";
    import { folderRenderingColsToListCols, type ListColumn } from "@/utils/columnsUtils";
    import { Button, Modal } from "flowbite-svelte";
    import { bulkBucket } from "@/stores/management/bulk_bucket";
    import { spaces } from "@/stores/management/spaces";
    import { getSpaces } from "@/lib/dmart_services";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import ListViewActionBar from "@/components/management/ListViewActionBar.svelte";
    import { currentListView } from "@/stores/global";
    import { untrack, onDestroy } from "svelte";
    import { filterRequestHeaders, getAttributeValue, getRowsPerPageSetting } from "@/utils/listViewUtils";
    import { limitJsonForDisplay } from "@/utils/displayJson";
    import { errorMessage } from "@/utils/errorMessage";
    import { website } from "@/config";
    import { resolveBackendBase } from "@shared/backend-url";
    import { clampPage } from "@/utils/paging";
    import { _, locale } from "@/i18n";

    $bulkBucket = [];

    let {
        space_name = $bindable(),
        subpath = $bindable(),
        shortname = $bindable(null),
        type = $bindable(QueryType.search),
        folderColumns = $bindable(null),
        sort_by = $bindable(null),
        sort_order = $bindable(null),
        query = $bindable(null),
        is_clickable = $bindable(true),
        canDelete = $bindable(false),
        exact_subpath = $bindable(true),
        emptyHint = undefined,
        scope = $bindable(DmartScope.managed),
        stream = $bindable(false),
        onStreamUpdate = undefined,
    }: {
        space_name?: string;
        subpath?: string;
        shortname?: string | null;
        type?: QueryType;
        folderColumns?: any;
        sort_by?: string | null;
        sort_order?: string | null;
        query?: any;
        is_clickable?: boolean;
        canDelete?: boolean;
        exact_subpath?: boolean;
        // Hint under "No records found" for lists that are not a folder (Trash).
        emptyHint?: string;
        scope?: DmartScope;
        stream?: boolean;
        onStreamUpdate?: ((message: any) => void) | undefined;
    } = $props();

    $currentListView = { fetchPageRecords };

    // The default columns are titled from the locale; a folder's own
    // `index_attributes` columns carry the admin's names and are shown as is.
    const usesDefaultColumns = folderColumns === null || Object.keys(folderColumns).length === 0;
    const DEFAULT_COLUMN_TITLES: Record<string, string> = {
        shortname: "shortname",
        resource_type: "resource_type",
        schema_shortname: "schema_shortname",
        created_at: "created_at",
        updated_at: "updated_at",
    };

    let _initColumns: Record<string, ListColumn>;
    if (usesDefaultColumns) {
        _initColumns = cols;
    } else {
        _initColumns = folderRenderingColsToListCols(folderColumns);
    }
    if (Object.keys(_initColumns).includes("undefined")) {
        _initColumns = {
            shortname: {
                path: "shortname",
                title: "shortname",
                type: "string",
                width: "20%",
            },
        };
    }
    let columns: Record<string, ListColumn> | null = $state(_initColumns);
    const columnKeys = $derived(Object.keys(columns ?? {}));

    function columnTitle(col: string): string {
        const key = DEFAULT_COLUMN_TITLES[col];
        if (usesDefaultColumns && key) return $_(key);
        return columns?.[col]?.title ?? col;
    }

    // null until the first response: nothing is known yet (skeleton).
    let total: number | null = $state(null);
    let fetchError: string | null = $state(null);
    let isFetching = $state(false);

    const { sortBy, sortOrder, page, search } = $params;
    if (search) {
        $searchListView = search;
    }
    let sort = {
        sort_by: (sortBy ?? sort_by) || "shortname", // descending
        sort_order: (sortOrder ?? sort_order) || "ascending",
    };

    const initialRowsPerPage = getRowsPerPageSetting();
    let objectDatatable = $state(
        functionCreateDatatable({
            parData: [],
            parRowsPerPage: initialRowsPerPage,
            parSearchString: "",
            parSortBy: (sortBy ?? sort_by) || "shortname",
            parSortOrder: (sortOrder ?? sort_order) || "ascending",
            // The URL may carry anything; a stale page past the end is clamped
            // once the first response tells us the total.
            parActivePage: clampPage(page, null, initialRowsPerPage),
        }),
    );

    $effect(() => {
        if (columns) objectDatatable.arraySearchableColumns = Object.keys(columns);
    });

    // Last values the effects below acted on, so they only react to changes.
    let numberActivePage: number = clampPage(page, null, initialRowsPerPage);
    let numberRowsPerPage: number = initialRowsPerPage;

    function syncPageParam(pageNo: number) {
        const { page: _page, ...rest } = $params;
        $goto("$leaf", pageNo > 1 ? { ...rest, page: pageNo.toString() } : rest);
    }

    /** Move to a page without the page effect re-fetching (callers fetch themselves). */
    function setActivePage(pageNo: number) {
        numberActivePage = pageNo;
        objectDatatable.numberActivePage = pageNo;
        syncPageParam(pageNo);
    }

    function publishTotal(value: number) {
        total = value;
        if ($currentListView) {
            $currentListView.total = value;
        }
    }

    let queryObject: any = {};

    // Monotonic request id: a slow response for an earlier page, sort or
    // search must never overwrite a newer one, and only the newest request
    // may clear the loading flag.
    let fetchSeq = 0;

    /**
     * With `delay_total_count`, the list query skips the count and this
     * counters query supplies it afterwards. It is tagged with the list
     * request's id so a count for a superseded request is dropped, and a
     * server that still did not count (-1) leaves the provisional total alone.
     */
    async function fetchPageRecordsTotal(listQuery: QueryRequest, seq: number) {
        try {
            const resp = await Dmart.query(
                { ...listQuery, type: QueryType.counters, retrieve_total: true },
                scope,
            );
            if (seq !== fetchSeq) return;
            const counted = resolveTotal(resp?.attributes?.total, -1);
            if (counted < 0) return;
            publishTotal(counted);
            const pageNo = objectDatatable.numberActivePage;
            if (objectDatatable.arrayRawData.length === 0 && counted > 0 && pageNo > 1) {
                setActivePage(clampPage(pageNo, counted, objectDatatable.numberRowsPerPage));
                void fetchPageRecords(true, {});
            }
        } catch {
            // The provisional total (what this page showed) stands.
        }
    }

    /* Listen for changes to space_name or subpath when component is reused */
    let old_subpath = subpath;
    let old_space_name = space_name;
    $effect(() => {
        if (subpath !== old_subpath || space_name !== old_space_name) {
            untrack(() => {
                objectDatatable.numberActivePage = 1;
                numberActivePage = 1;

                $searchListView = "";
                objectDatatable.stringSortBy = "shortname";
                objectDatatable.stringSortOrder = "ascending";

                let newParams = { ...$params };
                delete newParams.page;
                delete newParams.search;
                delete newParams.sortBy;
                delete newParams.sortOrder;
                $goto("$leaf", newParams);

                old_subpath = subpath;
                old_space_name = space_name;

                void fetchPageRecords(true, {});
            });
        }
    });

    let streamSocket: WebSocket | null = null;

    function closeStream() {
        if (streamSocket) {
            try { streamSocket.close(); } catch { /* already closed */ }
            streamSocket = null;
        }
    }

    // The handshake authenticates with the HttpOnly auth_token cookie, which
    // the browser attaches to a same-origin WebSocket; nothing goes in the URL.
    function buildStreamUrl(): string | null {
        const backendBase = resolveBackendBase(website.backend,
            typeof window !== "undefined" ? window.location.origin : "");
        if (!backendBase) return null;
        const parsed = new URL(backendBase);
        const wsProtocol = parsed.protocol === "https:" ? "wss:" : "ws:";
        const path = parsed.pathname.replace(/\/+$/, "");
        const wsPath = path.endsWith("/ws") ? path : `${path}/ws`;
        return `${wsProtocol}//${parsed.host}${wsPath}`;
    }

    $effect(() => {
        const enabled = stream === true && !!space_name && subpath !== undefined && subpath !== null;
        if (!enabled) {
            closeStream();
            return;
        }

        // subpath arrives in cxb route-encoded form (slashes replaced with dashes by the
        // router); reverse the encoding before sending to the backend.
        const slashSubpath = (subpath ?? "").replaceAll("-", "/");
        const normalizedSubpath = slashSubpath.startsWith("/") ? slashSubpath : `/${slashSubpath}`;
        const url = buildStreamUrl();
        if (!url) return;

        closeStream();
        const socket = new WebSocket(url);
        streamSocket = socket;

        socket.onopen = () => {
            if (socket.readyState !== WebSocket.OPEN) return;
            socket.send(JSON.stringify({
                type: "notification_subscription",
                space_name,
                subpath: normalizedSubpath,
            }));
        };
        socket.onmessage = (event) => {
            // Drop frames from a socket the effect has already rotated past — otherwise a
            // stale connection still firing while a reconnect is in flight could trigger
            // onStreamUpdate against the wrong subscription.
            if (streamSocket !== socket) return;
            try {
                const data = JSON.parse(event.data);
                if (data?.type === "notification_subscription" && data?.message?.action_type) {
                    onStreamUpdate?.(data.message);
                }
            } catch { /* ignore malformed frames */ }
        };

        return () => {
            if (streamSocket === socket) streamSocket = null;
            try { socket.close(); } catch { /* already closed */ }
        };
    });

    onDestroy(() => {
        closeStream();
        // The global store must not keep this instance (its records, its
        // closures) alive once the list is gone, nor hand the action bar a
        // stale fetchPageRecords.
        if ($currentListView?.fetchPageRecords === fetchPageRecords) {
            currentListView.set(null);
        }
    });

    /**
     * Load the current page. Never throws: failures land in `fetchError` with
     * a Retry button, and the rows already on screen are kept.
     */
    async function fetchPageRecords(isSetPage = true, requestExtra = {}): Promise<void> {
        const seq = ++fetchSeq;
        const delayTotalCount = website.delay_total_count === true;

        isFetching = true;
        fetchError = null;
        try {
            let _search = $searchListView;

            if (subpath === "/") {
                if ($spaces === null || $spaces.length === 0) {
                    await getSpaces();
                    if (seq !== fetchSeq) return;
                }
                const currentSpace = $spaces?.find((e) => e.shortname === space_name);
                const hideFolders = currentSpace?.attributes?.hide_folders;

                if (hideFolders?.length) {
                    _search += ` -@shortname:${hideFolders.join("|")}`;
                }
            }

            if (query?.type && query?.search) {
                _search += ` ${query.search.trim()}`;
            }
            const limit = objectDatatable.numberRowsPerPage;
            const pageNo = objectDatatable.numberActivePage;
            const _subpath = (subpath ?? '').replaceAll('-', '/');
            queryObject = {
                filter_shortnames: shortname ? [shortname] : [],
                type,
                space_name: space_name,
                subpath: _subpath,
                exact_subpath: exact_subpath,
                limit,
                sort_by: (objectDatatable.stringSortBy ?? "shortname").toString(),
                sort_type: SortyType[objectDatatable.stringSortOrder],
                offset: limit * (pageNo - 1),
                search: _search.trim(),
                ...requestExtra,
                retrieve_json_payload: true,
                retrieve_total: !delayTotalCount,
            };
            if ($currentListView) {
                $currentListView.query = queryObject;
            }
            if (delayTotalCount) {
                void fetchPageRecordsTotal({ ...queryObject }, seq);
            }
            const resp = await Dmart.query({ ...queryObject }, scope);
            if (seq !== fetchSeq) return;

            const records = (resp?.records ?? []) as ApiResponseRecord[];
            const seenSoFar = limit * (pageNo - 1) + records.length;

            if (!delayTotalCount) {
                publishTotal(resolveTotal(resp?.attributes?.total, seenSoFar));
            } else if (total === null || total < seenSoFar) {
                // Provisional until the counters query lands; never -1.
                publishTotal(seenSoFar);
            }

            // An empty page while the total says there are rows: the page is
            // past the end (stale ?page= after a rows-per-page change, or the
            // last page emptied by a bulk delete). Step back and reload.
            if (records.length === 0 && total !== null && total > 0 && pageNo > 1) {
                const target = clampPage(pageNo, total, limit);
                if (target !== pageNo) {
                    setActivePage(target);
                    return fetchPageRecords(isSetPage, requestExtra);
                }
            }

            objectDatatable.arrayRawData = records as any;
        } catch (e: unknown) {
            if (seq !== fetchSeq) return;
            fetchError = errorMessage(e, $_("list_fetch_failed"));
        } finally {
            if (seq === fetchSeq) isFetching = false;
        }
    }

    // ── Events modal ────────────────────────────────────────────────────────
    let modalData: any = $state({});
    let open = $state(false);
    const eventPreview = $derived(limitJsonForDisplay(modalData));

    function openEvent(record: any) {
        open = true;
        modalData = $state.snapshot(record);
        if (modalData?.attributes?.attributes?.request_headers) {
            modalData.attributes.attributes.request_headers = filterRequestHeaders(
                modalData.attributes.attributes.request_headers,
            );
        }
    }

    const isEvents = $derived(type === QueryType.events);

    /** Where a row leads; null when rows are not links (events, read-only lists). */
    function rowHref(record: any): string | null {
        if (!is_clickable || isEvents) return null;

        if (record.resource_type === "folder") {
            let _subpath = `${record.subpath}/${record.shortname}`.replace(/\/+/g, "/");
            if (_subpath.length > 0 && subpath?.[0] === "/") {
                _subpath = _subpath.substring(1);
            }
            if (_subpath.length > 0 && _subpath[_subpath.length - 1] === "/") {
                _subpath = _subpath.slice(0, -1);
            }
            return $url("/management/content/[space_name]/[subpath]", {
                space_name: space_name ?? "",
                subpath: _subpath.replaceAll("/", "-"),
            });
        }

        return $url(
            "/management/content/[space_name]/[subpath]/[shortname]/[resource_type]",
            {
                space_name: space_name ?? "",
                subpath: record.subpath.replaceAll("/", "-"),
                shortname: record.shortname,
                resource_type: record.resource_type,
            },
        );
    }

    /**
     * Sets query parameters for navigation
     */
    export function setQueryParam(params: any) {
        $goto("$leaf", { ...params });
    }

    /**
     * Redirects to entry detail page
     */
    export function redirectToEntry(record: any) {
        const shortname = record.shortname;
        const tmp_subpath = record.subpath.replaceAll("/", "-");

        $goto(
            "/management/content/[space_name]/[subpath]/[shortname]/[resource_type]",
            {
                space_name: space_name ?? "",
                subpath: tmp_subpath,
                shortname: shortname,
                resource_type: record.resource_type,
            },
        );
    }

    $effect(() => {
        if (objectDatatable) {
            if (
                !isDeepEqual(sort, {
                    sort_by: objectDatatable.stringSortBy,
                    sort_order: objectDatatable.stringSortOrder,
                })
            ) {
                const x = {
                    sort_by: (objectDatatable.stringSortBy ?? "shortname").toString(),
                    sort_order: objectDatatable.stringSortOrder,
                };
                setQueryParam({
                    ...$params,
                    sortBy: (objectDatatable.stringSortBy ?? "shortname").toString(),
                    sortOrder: objectDatatable.stringSortOrder,
                });

                untrack(() => {
                    void fetchPageRecords(true, {
                        sort_by: (objectDatatable.stringSortBy ?? "shortname").toString(),
                        sort_type: objectDatatable.stringSortOrder,
                    });
                });
                sort = structuredClone(x);
            }
        }
    });

    $effect(() => {
        const limit = objectDatatable.numberRowsPerPage;
        if (limit !== numberRowsPerPage) {
            numberRowsPerPage = limit;
            untrack(() => {
                if (typeof localStorage !== "undefined") {
                    localStorage.setItem("rowPerPage", limit.toString());
                }
                // A new page size starts from page 1: keeping page N would send
                // an offset past the end of the smaller set of pages.
                const reselectAll = allChecked;
                setActivePage(1);
                void fetchPageRecords(true, {}).then(() => {
                    if (reselectAll) toggleAll(true);
                });
            });
        }
    });

    $effect(() => {
        const pageNo = objectDatatable.numberActivePage;
        if (pageNo !== numberActivePage) {
            numberActivePage = pageNo;
            untrack(() => {
                syncPageParam(pageNo);
                void fetchPageRecords(true, {});
            });
        }
    });

    // ── Bulk selection ──────────────────────────────────────────────────────
    const rows = $derived(objectDatatable.arrayRawData as ApiResponseRecord[]);
    const selectedKeys = $derived(new Set($bulkBucket.map((b) => b.shortname)));
    const allChecked = $derived(rows.length > 0 && rows.every((r) => selectedKeys.has(r.shortname)));
    const someChecked = $derived(!allChecked && rows.some((r) => selectedKeys.has(r.shortname)));

    function toggleRow(record: ApiResponseRecord, checked: boolean) {
        if (checked) {
            if (selectedKeys.has(record.shortname)) return;
            $bulkBucket = [...$bulkBucket, { ...record }];
        } else {
            $bulkBucket = $bulkBucket.filter((e) => e.shortname !== record.shortname);
        }
    }

    function toggleAll(checked: boolean) {
        $bulkBucket = checked
            ? (rows ?? []).map((row) => ({ ...row }))
            : [];
    }

    // ── Cells ───────────────────────────────────────────────────────────────
    // Read the translator and locale once per render, not once per cell.
    const valueContext = $derived({ t: $_, locale: $locale });

    function cellText(row: any, col: string): string {
        const path = columns?.[col]?.path;
        const type = columns?.[col]?.type ?? "string";
        const key = path ?? col;
        if (type === "json") {
            const segments = key.split(".");
            let current: any = row;
            for (const seg of segments) {
                if (current == null || typeof current !== "object") {
                    current = undefined;
                    break;
                }
                current = current[seg];
            }
            if (current === undefined || current === null) return "";
            return JSON.stringify(current, undefined, 1);
        }
        return getAttributeValue(row, key, valueContext);
    }

    function rowClass(selected: boolean): string {
        const base = "relative border-b border-border last:border-b-0 transition-colors hover:bg-surface-3 focus-within:bg-surface-3";
        return selected ? `${base} bg-primary-soft` : `${base} even:bg-surface/60`;
    }

    const checkboxClass =
        "h-4 w-4 rounded-control border-border-strong bg-surface-2 text-primary focus:ring-primary focus:ring-offset-0 cursor-pointer";

    void fetchPageRecords(true, {});
</script>

<Modal bind:open size="lg" title={modalData?.shortname ?? $_("event")} class="rounded-modal shadow-modal">
    <div class="space-y-3">
        {#if eventPreview.truncated}
            <p class="text-xs text-text-muted">{$_("preview_truncated")}</p>
        {/if}
        <div class="max-h-[60vh] overflow-auto">
            <Prism code={eventPreview.value as object | string} />
        </div>
    </div>
    <div class="flex items-center justify-end gap-2 mt-6">
        <Button color="alternative" onclick={() => (open = false)}>{$_("close")}</Button>
        <Button
            color="primary"
            onclick={() => {
                open = false;
                redirectToEntry(modalData);
            }}
        >
            {$_("open_entry")}
        </Button>
    </div>
</Modal>

{#if !isEvents}
    <ListViewActionBar space_name={space_name ?? ""} subpath={subpath ?? ""} />
{/if}

<div class="w-full px-3 pb-3">
    {#if fetchError}
        <ErrorState
            compact
            class="mt-2"
            title={$_("list_fetch_failed")}
            message={fetchError}
            onRetry={() => fetchPageRecords(true, {})}
        />
    {/if}

    {#if total === null}
        {#if isFetching}
            <div class="mt-2 rounded-card border border-border bg-surface-2 p-4">
                <LoadingState variant="skeleton" rows={8} />
            </div>
        {/if}
    {:else}
        <!-- Loading never blanks the table: the rows stay and a spinner overlays them. -->
        <LoadingState variant="overlay" loading={isFetching} class="mt-2">
            {#if rows.length === 0 && total === 0}
                <div class="py-6">
                    <EmptyState
                        title={$_("no_records_found")}
                        hint={$searchListView ? $_("search_empty_hint") : (emptyHint ?? $_("folder_empty_hint"))}
                    />
                </div>
            {:else}
                <div class="rounded-card border border-border bg-surface-2 shadow-card overflow-x-auto max-h-[calc(100vh-14rem)]">
                    <table class="w-full text-sm text-start border-collapse tabular-nums" aria-busy={isFetching}>
                        <thead class="sticky top-0 z-10 bg-surface text-text-muted text-xs font-semibold shadow-[inset_0_-1px_0_var(--color-border)]">
                            <tr>
                                {#if canDelete}
                                    <th scope="col" class="w-10 p-2 text-start">
                                        <input
                                            type="checkbox"
                                            class={checkboxClass}
                                            checked={allChecked}
                                            indeterminate={someChecked}
                                            aria-label={$_("select_all")}
                                            onchange={(e) => toggleAll(e.currentTarget.checked)}
                                        />
                                    </th>
                                {/if}
                                {#each columnKeys as col (col)}
                                    <th scope="col" class="p-2 text-start whitespace-nowrap" aria-sort={objectDatatable.stringSortBy === col ? (objectDatatable.stringSortOrder === "ascending" ? "ascending" : "descending") : undefined}>
                                        <Sort bind:propDatatable={objectDatatable} propColumn={col}>
                                            {columnTitle(col)}
                                        </Sort>
                                    </th>
                                {/each}
                            </tr>
                        </thead>
                        <tbody>
                            {#each rows as row (rowKey(row))}
                                {@const typedRow = row as any}
                                {@const selected = selectedKeys.has(row.shortname)}
                                {@const href = rowHref(typedRow)}
                                <tr class={rowClass(selected)} aria-selected={canDelete ? selected : undefined}>
                                    {#if canDelete}
                                        <!-- Positioned above the row-wide link so the box stays clickable. -->
                                        <td class="relative z-10 p-2">
                                            <input
                                                type="checkbox"
                                                class={checkboxClass}
                                                checked={selected}
                                                aria-label={$_("select_entry", { values: { shortname: row.shortname } })}
                                                onchange={(e) => toggleRow(row, e.currentTarget.checked)}
                                            />
                                        </td>
                                    {/if}
                                    {#each columnKeys as col, ci (col)}
                                        {@const value = cellText(typedRow, col)}
                                        <td class="p-2 max-w-xs text-text">
                                            {#if ci === 0 && href}
                                                <!-- The one real link per row; its ::after stretches over
                                                     the row so a click anywhere still opens the entry. -->
                                                <a
                                                    {href}
                                                    class="block truncate font-medium text-text hover:text-primary rounded-control after:absolute after:inset-0 after:content-['']"
                                                    title={value}
                                                >
                                                    {value}
                                                </a>
                                            {:else if ci === 0 && isEvents && is_clickable}
                                                <button
                                                    type="button"
                                                    class="block w-full truncate text-start font-medium text-text hover:text-primary cursor-pointer rounded-control after:absolute after:inset-0 after:content-['']"
                                                    title={value}
                                                    onclick={() => openEvent(typedRow)}
                                                >
                                                    {value}
                                                </button>
                                            {:else}
                                                <span class="block truncate" title={value}>{value}</span>
                                            {/if}
                                        </td>
                                    {/each}
                                </tr>
                            {/each}
                        </tbody>
                    </table>
                </div>
                <!-- The pager stays whenever there is anything to page, even
                     while a page is momentarily empty. -->
                <Pagination
                    class="mt-4"
                    page={objectDatatable.numberActivePage}
                    pageSize={objectDatatable.numberRowsPerPage}
                    {total}
                    onPageChange={(p) => (objectDatatable.numberActivePage = p)}
                    onPageSizeChange={(size) => (objectDatatable.numberRowsPerPage = size)}
                />
            {/if}
        </LoadingState>
    {/if}
</div>
