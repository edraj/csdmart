<script lang="ts">
    import { resolveTotal } from "@shared/query-total";
    import {functionCreateDatatable, Sort} from "@/components/management/datatable";
    import Pagination from "@/components/ui/Pagination.svelte";
    import {rowKey} from "@/utils/rowKey";
    import {Dmart, DmartScope, type ApiResponseRecord, type QueryRequest, QueryType, SortyType,} from "@edraj/tsdmart";
    import cols from "@/utils/jsons/list_cols.json";
    import {searchListView} from "@/stores/management/triggers";
    import Prism from "@/components/Prism.svelte";
    import {goto, params} from "@roxi/routify";
    import {fade} from "svelte/transition";
    import {isDeepEqual} from "@/utils/compare";
    import {folderRenderingColsToListCols, type ListColumn} from "@/utils/columnsUtils";
    import {
        Button,
        Checkbox,
        ListPlaceholder,
        Modal,
        Spinner,
        Table,
        TableBody,
        TableBodyCell,
        TableBodyRow,
        TableHead,
        TableHeadCell,
    } from "flowbite-svelte";
    import {bulkBucket} from "@/stores/management/bulk_bucket";
    import {spaces} from "@/stores/management/spaces";
    import {getSpaces} from "@/lib/dmart_services";
    import {Level, showToast} from "@/utils/toast";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import ListViewActionBar from "@/components/management/ListViewActionBar.svelte";
    import {currentListView} from "@/stores/global";
    import {untrack, onDestroy} from "svelte";
    import {filterRequestHeaders, getAttributeValue, getRowsPerPageSetting} from "@/utils/listViewUtils";
    import {website} from "@/config";
    import {resolveBackendBase} from "@shared/backend-url";
    import {clampPage} from "@/utils/paging";
    import {_} from "@/i18n";

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
        scope?: DmartScope;
        stream?: boolean;
        onStreamUpdate?: ((message: any) => void) | undefined;
    } = $props();

    $currentListView = {fetchPageRecords};

    let _initColumns: Record<string, ListColumn>;
    if (folderColumns === null || folderColumns.length === 0) {
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

    // null until the first response: nothing is known yet (placeholder).
    let total: number | null = $state(null);
    let fetchError: string | null = $state(null);
    let isFetching = $state(false);

    const {sortBy, sortOrder, page, search} = $params;
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
                {...listQuery, type: QueryType.counters, retrieve_total: true},
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

                let newParams = {...$params};
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

    onDestroy(closeStream);

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
                void fetchPageRecordsTotal({...queryObject}, seq);
            }
            const resp = await Dmart.query({...queryObject}, scope);
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
        } catch (e: any) {
            if (seq !== fetchSeq) return;
            fetchError = e?.response?.data?.error?.message ?? e?.message ?? $_("list_fetch_failed");
        } finally {
            if (seq === fetchSeq) isFetching = false;
        }
    }

    let modalData: any = $state({});
    let open = $state(false);

    async function onListClick(event: any, record: any) {
        if (!is_clickable) {
            return;
        }

        if (type === QueryType.events) {
            open = true;

            modalData = $state.snapshot(record);

            if (modalData?.attributes?.attributes?.request_headers) {
                modalData.attributes.attributes.request_headers = filterRequestHeaders(
                    modalData.attributes.attributes.request_headers,
                );
            }
            return;
        }

        if (record.resource_type === "folder") {
            let _subpath = `${record.subpath}/${record.shortname}`.replace(
                /\/+/g,
                "/",
            );

            if (_subpath.length > 0 && subpath?.[0] === "/") {
                _subpath = _subpath.substring(1);
            }
            if (_subpath.length > 0 && _subpath[_subpath.length - 1] === "/") {
                _subpath = _subpath.slice(0, -1);
            }

            $goto("/management/content/[space_name]/[subpath]", {
                space_name: space_name ?? "",
                subpath: _subpath.replaceAll("/", "-"),
            });

            return;
        }

        redirectToEntry(record);
    }

    /**
     * Sets query parameters for navigation
     */
    export function setQueryParam(params: any) {
        $goto("$leaf", {...params});
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
                setActivePage(1);
                void fetchPageRecords(true, {}).then(() => {
                    handleAllBulk(null, isAllBulkChecked);
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

    const toggleModal = () => {
        open = !open;
    };

    function handleBulk(event: any) {
        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation();
        try {
            const {name, checked} = event.target;
            const record = objectDatatable.arrayRawData[name] as ApiResponseRecord;
            if (checked) {
                $bulkBucket = [
                    ...$bulkBucket,
                    {
                        ...record,
                        shortname: record.shortname,
                        resource_type: record.resource_type,
                    },
                ];
            } else {
                $bulkBucket = $bulkBucket.filter(
                    (e) => e.shortname !== record.shortname,
                );
            }
        } catch (e: any) {
            showToast(Level.warn, "Error processing bulk selection");
            if (e?.target) e.target.checked = false;
        }
    }

    let isAllBulkChecked = false;

    function handleAllBulk(e: any, override: boolean | null = null) {
        isAllBulkChecked = override === null ? !isAllBulkChecked : override;
        if (e) {
            e.target.checked = isAllBulkChecked;
        }

        if (isAllBulkChecked) {
            // Select all — build the full list in one pass.
            // Guard against the auto-call from the rowsPerPage $effect
            // landing while objectDatatable is mid-rebuild (arrayRawData
            // momentarily undefined despite the factory's setter coercion).
            $bulkBucket = (objectDatatable.arrayRawData ?? []).map((row: any) => ({
                shortname: row.shortname,
                resource_type: row.resource_type,
                ...row,
            }));
        } else {
            // Deselect all
            $bulkBucket = [];
        }
    }

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
        return getAttributeValue(row, key);
    }

    void fetchPageRecords(true, {});
</script>

<Modal bind:open size="lg">
    <div class="modal-header">
        <h5 class="modal-title">
            {modalData.shortname}
        </h5>
        <button
                type="button"
                onclick={toggleModal}
                class="btn-close"
                aria-label={$_("close")}
        >
        </button>
    </div>

    <div>
        <Prism code={modalData}/>
    </div>
    <div>
        <Button color="secondary" onclick={() => (open = false)}>{$_("close")}</Button>
        <Button
                color="primary"
                onclick={() => {
        open = false;
        redirectToEntry(modalData);
      }}>Entry
        </Button
        >
    </div>
</Modal>

{#if type !== QueryType.events}
    <ListViewActionBar space_name={space_name ?? ""} subpath={subpath ?? ""}/>
{/if}

<div class="w-full">
    {#if fetchError}
        <div
            class="mx-3 mt-2 flex flex-wrap items-center justify-between gap-3 rounded-[var(--radius-md)] border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-700 dark:bg-red-900/20 dark:text-red-300"
            role="alert"
        >
            <span>{$_("list_fetch_failed")} <span class="opacity-80">{fetchError}</span></span>
            <Button size="xs" color="light" onclick={() => fetchPageRecords(true, {})}>{$_("retry")}</Button>
        </div>
    {/if}

    {#if total === null}
        {#if isFetching}
            <div class="flex flex-col w-full">
                <ListPlaceholder class="m-5" size="lg" style="width: 100%"/>
            </div>
        {/if}
    {:else}
        <!-- Loading never blanks the table: the rows stay and a spinner overlays them. -->
        <div class="mx-3 relative" aria-busy={isFetching} transition:fade={{ delay: 25 }}>
            {#if isFetching}
                <div
                    class="absolute inset-0 z-10 flex items-start justify-center pt-10 bg-[color:var(--color-bg)]/60"
                    aria-hidden="true"
                >
                    <Spinner size="8" />
                </div>
            {/if}
            {#if objectDatatable.arrayRawData.length === 0 && total === 0}
                <div class="py-6">
                    <EmptyState
                        title={$_("no_records_found")}
                        hint={$searchListView ? $_("search_empty_hint") : $_("folder_empty_hint")}
                    />
                </div>
            {:else}
                <div class="rounded-[var(--radius-md)] border border-[color:var(--color-border)] overflow-x-auto mt-2 shadow-[var(--shadow-card)]">
                <Table
                        striped={true}
                        class="border-collapse w-full"
                >
                    <TableHead class="bg-[color:var(--color-surface)] text-[color:var(--color-text-muted)]">
                        {#if canDelete}
                            <TableHeadCell class="p-2 border-b border-[color:var(--color-border)] w-10">
                                <Checkbox class="bg-[color:var(--color-bg)]" onchange={handleAllBulk}/>
                            </TableHeadCell>
                        {/if}
                        {#each Object.keys(columns ?? {}) as col (col)}
                            <TableHeadCell class="p-2 border-b border-[color:var(--color-border)] font-semibold text-xs uppercase tracking-wide">
                                <Sort bind:propDatatable={objectDatatable} propColumn={col}>
                                    {columns?.[col]?.title}
                                </Sort>
                            </TableHeadCell>
                        {/each}
                    </TableHead>
                    <TableBody>
                        {#each objectDatatable.arrayRawData as row, index (rowKey(row))}
                            {@const typedRow = row as any}
                            <TableBodyRow
                                    class="hover:bg-[color:var(--color-surface-hover)] transition-colors"
                                    onclick={(e) => onListClick(e, typedRow)}
                            >
                                <div style="all: unset;display: contents;">
                                    {#if canDelete}
                                        <span
                                                style="all: unset;display: contents;"
                                                role="presentation"
                                                onclick={(e) => {
                                                    e.stopPropagation();
                                                    const checkbox = e.currentTarget.querySelector(
                                                        'input[type="checkbox"]',
                                                    ) as HTMLInputElement | null;
                                                    if (checkbox) {
                                                        checkbox.checked = !checkbox.checked;
                                                        const event = new Event("change", {
                                                            bubbles: true,
                                                        });
                                                        checkbox.dispatchEvent(event);
                                                    }
                                                }}
                                        >
                                            <TableBodyCell class="p-2 border-b border-[color:var(--color-border)]">
                                                <Checkbox
                                                        class="bg-[color:var(--color-bg)]"
                                                        id={typedRow.shortname}
                                                        name={index.toString()}
                                                        checked={$bulkBucket.some(
                                                            (e) => e.shortname === typedRow.shortname,
                                                        )}
                                                        onchange={handleBulk}
                                                        onclick={(e) => e.stopPropagation()}
                                                />
                                            </TableBodyCell>
                                        </span>
                                    {/if}
                                    {#each Object.keys(columns ?? {}) as col (col)}
                                        {@const value = cellText(typedRow, col)}
                                        <TableBodyCell
                                                class="p-2 border-b border-[color:var(--color-border)] cursor-pointer max-w-xs"
                                        >
                                            <span class="block truncate" title={value}>{value}</span>
                                        </TableBodyCell>
                                    {/each}
                                </div>
                            </TableBodyRow>
                        {/each}
                    </TableBody>
                </Table>
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
        </div>
    {/if}
</div>
