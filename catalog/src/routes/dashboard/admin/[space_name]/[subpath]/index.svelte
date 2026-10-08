<script lang="ts">
  import { resolveTotal } from "@shared/query-total";
  import { goto as gotoStore, params } from "@roxi/routify";
  import { can, permissions } from "@/stores/permissions";
  import { visibleColumns } from "@/lib/access-fields";
  import {
    deleteEntity,
    getEntity,
    getSpaceHideFolders,
    buildHideFoldersSearch,
    mergeSearch,
    getSpaceTags,
  } from "@/lib/dmart_services";
  import { buildFieldFilterClause } from "@/lib/searchFilters";
  import { asFormSchema, pruneEmptyFormValues } from "@/lib/formUtils";
  import {
    parseValueByType,
    getFieldType,
    formatValueForEdit,
    setNestedValue,
  } from "@/lib/schemaTypes";
  import { createFolder } from "@/lib/dmart_services/entries";
  import { asSchemaNode, collectSchemaPropertyBags, resolveSchemaDef } from "@/lib/jsonSchema";
  import {
    asResourceType,
    bodyObject,
    isIndexable,
    isJsonObject,
    recordsOf,
    type EntryDetail,
    type EntryRecord,
    type IndexAttribute,
    type JsonObject,
    type Schema,
  } from "@/lib/types";
  import { errorMessage, serverMessage } from "@/lib/apiError";
  import type { Breadcrumb } from "@/lib/paths";
  import type { MetaFormData } from "@/components/forms/MetaForm.svelte";
  import type { UserFormData } from "@/components/management/forms/MetaUserForm.svelte";
  import { _, locale } from "@/i18n";
  import { setTitle } from "@/lib/title";
  import { formatDate } from "@/lib/format";
  import {
    Dmart,
    RequestType,
    ResourceType,
    QueryType,
    DmartScope,
    SortType,
    ContentType,
    type ActionRequestRecord,
  } from "@edraj/tsdmart";
  import { writable } from "svelte/store";
  import MetaForm from "@/components/forms/MetaForm.svelte";
  import FolderForm from "@/components/forms/FolderForm.svelte";
  import { formatNumber, getParentPath } from "@/lib/helpers";
  import { parseBreadcrumbPath } from "@/lib/breadcrumb";
  import { stripServerManagedFields } from "@/lib/duplicate";
  import { applyFolderContentDefaults } from "@/lib/folder_defaults";
  import SchemaForm from "@/components/forms/SchemaForm.svelte";
  import DynamicSchemaBasedForms from "@/components/forms/DynamicSchemaBasedForms.svelte";
  import MetaUserForm from "@/components/management/forms/MetaUserForm.svelte";
  import MetaRoleForm from "@/components/forms/MetaRoleForm.svelte";
  import MetaPermissionForm from "@/components/forms/MetaPermissionForm.svelte";
  import { MANAGEMENT_SPACE } from "@/lib/constants";
  import WorkflowForm, { normalizeWorkflowContent, type WorkflowContent } from "@/components/forms/WorkflowForm.svelte";
  import {
    errorToastMessage,
    successToastMessage,
  } from "@/lib/toasts_messages";
  import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
  import { log } from "@/lib/logger";
  import ModalCSVUpload from "@/components/management/Modals/ModalCSVUpload.svelte";
  import ModalCSVDownload from "@/components/management/Modals/ModalCSVDownload.svelte";
  import ModalCopy from "@/components/management/Modals/ModalCopy.svelte";
  import { UploadOutline, DownloadOutline } from "flowbite-svelte-icons";
  import DataTable from "@/components/DataTable.svelte";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  /**
   * The new-item form's data. The meta form and whichever resource-type form
   * the location calls for (user / role / permission) all bind to it, so it
   * carries the fields of each.
   */
  type CreateItemMeta = UserFormData & {
    permissions?: string[];
    subpaths?: Record<string, string[]>;
    resource_types?: string[];
    actions?: string[];
    conditions?: string[];
    restricted_fields?: string[];
    allowed_fields_values?: Record<string, unknown>;
  };

  let isLoading = writable(false);
  let allContents = writable<EntryRecord[]>([]);
  let displayedContents = $state<EntryRecord[]>([]);
  let folderMetadata = $state<EntryDetail | null>(null);
  // The folder's listing settings (its payload body), or null.
  const folderBody = $derived(bodyObject(folderMetadata?.payload));
  let spaceHideFolders = $state<string[]>([]);
  let error = $state<string | null>(null);
  let spaceName = $state("");
  let subpath = "";
  let actualSubpath = writable("");
  let breadcrumbs = $state<Breadcrumb[]>([]);

  $effect(() => setTitle(breadcrumbs[breadcrumbs.length - 1]?.name, spaceName));

  const ITEMS_PER_PAGE_KEY = "itemsPerPage";
  const SORT_BY_KEY = "admin_sortBy";
  const SORT_ORDER_KEY = "admin_sortOrder";

  let currentPage = $state(1);
  let itemsPerPage = $state(
    typeof localStorage !== "undefined"
      ? parseInt(localStorage.getItem(ITEMS_PER_PAGE_KEY) || "10", 10)
      : 10,
  );
  let sortBy = $state(
    typeof localStorage !== "undefined"
      ? localStorage.getItem(SORT_BY_KEY) || "name"
      : "name",
  );
  let sortOrder = $state(
    typeof localStorage !== "undefined"
      ? localStorage.getItem(SORT_ORDER_KEY) || "asc"
      : "asc",
  );
  // Checkbox filter panel state. `is_active` is a universal meta field so it
  // gets its own group; everything else is discovered from the folder's
  // content schema(s) (boolean/enum properties only — see schemaFilterFields).
  let showFilterPanel = $state(false);
  let statusFilter = $state<{ active: boolean; inactive: boolean }>({
    active: false,
    inactive: false,
  });
  let schemaFilterFields = $state<
    Array<{
      key: string;
      label: string;
      type: "boolean" | "enum";
      options: string[];
    }>
  >([]);
  let schemaFieldFilters = $state<Record<string, Record<string, boolean>>>({});
  let totalItemsCount = $state(0);
  let totalPages = $state(1);
  let isInitialLoad = $state(true);
  const itemsPerPageOptions = [10, 25, 50, 100];



  // Bulk selection state
  let selectedItems = $state(new Set<string>());
  let isBulkDeleting = $state(false);
  let showBulkDeleteConfirm = $state(false);
  let showBulkTrashConfirm = $state(false);
  let showBulkEditModal = $state(false);
  let isBulkSaving = $state(false);
  // One row of editable fields per selected item, keyed by its shortname.
  let bulkEditData = $state<Record<string, JsonObject>>({});

  // Copy / Move (single + bulk)
  let showCopyModal = $state(false);
  let copyRecords = $state<EntryRecord[]>([]);
  let copyAction = $state<"copy" | "move">("copy");

  function openCopyModal(items: EntryRecord[], action: "copy" | "move" = "copy") {
    if (!items || items.length === 0) return;
    copyRecords = items.map((r) => $state.snapshot(r));
    copyAction = action;
    showCopyModal = true;
  }
  function closeCopyModal() {
    showCopyModal = false;
    copyRecords = [];
  }
  async function handleCopyOrMoveDone() {
    selectedItems = new Set();
    await loadContents(true);
  }

  let duplicatingShortname = $state<string | null>(null);
  async function handleDuplicateItem(item: EntryRecord) {
    if (!item || duplicatingShortname) return;
    duplicatingShortname = item.shortname;
    try {
      const cleanAttributes = stripServerManagedFields(item.attributes);
      const response = await Dmart.request({
        space_name: spaceName,
        request_type: RequestType.create,
        records: [
          {
            resource_type: asResourceType(item.resource_type),
            shortname: "auto",
            subpath: `/${$actualSubpath}`,
            attributes: cleanAttributes,
          },
        ],
      });
      if (response?.status === "success") {
        successToastMessage($_("admin_content.actions.duplicate_success"));
        await loadContents(true);
      } else {
        errorToastMessage(
          response?.error?.message ||
            $_("admin_content.actions.duplicate_failed"),
        );
      }
    } catch (err) {
      log.error("Duplicate error:", err);
      errorToastMessage(
        serverMessage(err) ||
          $_("admin_content.actions.duplicate_failed"),
      );
    } finally {
      duplicatingShortname = null;
    }
  }

  let searchQuery = $state("");
  let searchTimeout: ReturnType<typeof setTimeout> | undefined;

  function handleSearchInput() {
    clearTimeout(searchTimeout);
    searchTimeout = setTimeout(() => {
      loadContents(true);
    }, 1500);
  }

  // Tags filtering state
  let availableTags = $state<string[]>([]);
  let selectedTags = $state<string[]>([]);
  let tagCounts = $state<JsonObject>({});
  let showAllTags = $state(false);


  const sortOptions = [
    { value: "name", label: $_("admin_dashboard.sort.name") },
    { value: "created", label: $_("admin_dashboard.sort.created") },
    { value: "updated", label: $_("admin_dashboard.sort.updated") },
    { value: "owner", label: $_("admin_dashboard.sort.owner") },
  ];

  // The server sorts the whole folder (sort_by/sort_type in loadContents);
  // re-sorting one page on the client put the wrong items first.
  const SERVER_SORT_FIELD: Record<string, string> = {
    name: "shortname",
    created: "created_at",
    updated: "updated_at",
    owner: "owner_shortname",
    type: "resource_type",
  };

  async function initializeContent() {
    spaceName = $params.space_name;
    subpath = $params.subpath;
    if (!subpath) return;
    $actualSubpath = subpath.replace(/-/g, "/");

    const pathParts = $actualSubpath
      .split("/")
      .filter((part) => part.length > 0);
    breadcrumbs = [
      { name: $_("admin_content.breadcrumb.admin"), path: "/dashboard/admin" },
      { name: spaceName, path: `/dashboard/admin/${spaceName}` },
    ];

    let currentUrlPath = "";
    pathParts.forEach((part, index) => {
      currentUrlPath += (index === 0 ? "" : "-") + part;
      breadcrumbs.push({
        name: part,
        path:
          index === pathParts.length - 1
            ? null
            : `/dashboard/admin/${spaceName}/${currentUrlPath}`,
      });
    });

    currentPage = 1;
    // The folder entity, the space tags and the hidden-folder list change only
    // with the subpath, so they are fetched once here, in parallel, and every
    // page / search / filter change below sends the one list query (perf #10).
    const parts = [...pathParts];
    const folderShortname = parts.pop();
    const parentPath = "/" + parts.join("/");
    const [hide, folderMeta, tagsResponse] = await Promise.all([
      getSpaceHideFolders(spaceName, DmartScope.managed),
      folderShortname
        ? getEntity(folderShortname, spaceName, parentPath, ResourceType.folder, DmartScope.managed).catch(() => null)
        : Promise.resolve(null),
      getSpaceTags(spaceName).catch(() => null),
    ]);
    spaceHideFolders = hide;
    folderMetadata = folderMeta;

    const folderBody = bodyObject(folderMeta?.payload);
    const schemaShortnames: string[] = Array.isArray(folderBody?.content_schema_shortnames)
      ? folderBody.content_schema_shortnames
      : [];
    const schemaShortnamesKey = schemaShortnames.slice().sort().join(",");
    if (schemaShortnamesKey !== _prevSchemaShortnamesKey) {
      _prevSchemaShortnamesKey = schemaShortnamesKey;
      loadSchemaFilterFields(schemaShortnames);
    }

    const tagsData: JsonObject = tagsResponse?.records?.[0]?.attributes ?? {};
    availableTags = Array.isArray(tagsData.tags)
      ? tagsData.tags.filter((tag): tag is string => typeof tag === "string")
      : [];
    tagCounts = isJsonObject(tagsData.tag_counts) ? tagsData.tag_counts : {};

    await loadContents(true);
  }

  let _prevSubpath = "";
  let _prevSpaceName = "";
  let _prevSchemaShortnamesKey = "";

  $effect(() => {
    const currentSubpath = $params.subpath;
    const currentSpaceName = $params.space_name;

    // Re-initialize whenever the routing params actually change
    if (
      currentSubpath !== _prevSubpath ||
      currentSpaceName !== _prevSpaceName
    ) {
      _prevSubpath = currentSubpath;
      _prevSpaceName = currentSpaceName;
      initializeContent();
    }
  });

  // Checked-but-not-all-checked is a real filter; none-checked or
  // all-checked both mean "no constraint" (same UX as the old "All" option).
  function buildStatusFilterSearch(): string {
    const { active, inactive } = statusFilter;
    if (active && !inactive) return "@is_active:true";
    if (inactive && !active) return "@is_active:false";
    return "";
  }

  function buildSchemaFieldFiltersSearch(): string {
    const parts: string[] = [];
    for (const field of schemaFilterFields) {
      const selections = schemaFieldFilters[field.key];
      if (!selections) continue;
      const selected = field.options.filter((opt) => selections[opt]);
      if (selected.length === 0 || selected.length === field.options.length)
        continue;
      const clause = buildFieldFilterClause(
        `payload.body.${field.key}`,
        selected,
      );
      if (clause) parts.push(clause);
    }
    return parts.join(" ");
  }

  async function loadSchemaFilterFields(schemaShortnames: string[]) {
    if (!schemaShortnames || schemaShortnames.length === 0) {
      schemaFilterFields = [];
      schemaFieldFilters = {};
      return;
    }

    try {
      const schemas = await Promise.all(
        schemaShortnames.map((sn) =>
          getEntity(
            sn,
            spaceName,
            "/schema",
            ResourceType.content,
            DmartScope.managed,
            true,
            true,
          ).catch(() => null),
        ),
      );

      const fieldsByKey = new Map<
        string,
        {
          key: string;
          label: string;
          type: "boolean" | "enum";
          options: string[];
        }
      >();

      for (const schema of schemas) {
        const body = asSchemaNode(schema?.payload?.body);
        // Discriminated-union schemas (e.g. "subaccount") have no top-level
        // `properties` — each oneOf/anyOf branch defines its own. Collect
        // every bag so filters cover fields from any branch.
        const propertyBags = collectSchemaPropertyBags(body);

        for (const properties of propertyBags) {
          for (const [propKey, propDefRaw] of Object.entries(properties)) {
            const propDef = resolveSchemaDef(body, propDefRaw);
            if (!propDef || typeof propDef !== "object") continue;

            if (propDef.type === "boolean") {
              if (!fieldsByKey.has(propKey)) {
                fieldsByKey.set(propKey, {
                  key: propKey,
                  label: propDef.title || propKey,
                  type: "boolean",
                  options: ["true", "false"],
                });
              }
              continue;
            }

            const items = asSchemaNode(propDef.items);
            const enumValues: unknown[] | undefined = Array.isArray(propDef.enum)
              ? propDef.enum
              : Array.isArray(items?.enum)
                ? items.enum
                : undefined;

            if (enumValues && enumValues.length > 0) {
              const existing = fieldsByKey.get(propKey);
              const optionSet = new Set(existing?.options ?? []);
              enumValues.forEach((v) => optionSet.add(String(v)));
              fieldsByKey.set(propKey, {
                key: propKey,
                label: propDef.title || existing?.label || propKey,
                type: "enum",
                options: Array.from(optionSet),
              });
            }
          }
        }
      }

      schemaFilterFields = Array.from(fieldsByKey.values());
      schemaFieldFilters = Object.fromEntries(
        Object.entries(schemaFieldFilters).filter(([key]) =>
          fieldsByKey.has(key),
        ),
      );
    } catch (err) {
      log.error("Error loading schema filter fields:", err);
      schemaFilterFields = [];
    }
  }

  async function loadContents(reset = false) {
    if ($isLoading) return;

    if (reset) {
      $isLoading = true;
      currentPage = 1;
      isInitialLoad = true;
    }

    error = null;

    try {
      const offset = (currentPage - 1) * itemsPerPage;
      const expandChildren = folderBody?.expand_children === true;

      const statusSearch = buildStatusFilterSearch();
      const schemaFieldsSearch = buildSchemaFieldFiltersSearch();
      const tagsSearch = buildFieldFilterClause("tags", selectedTags);

      const response = await Dmart.query(
          {
            type: QueryType.search,
            space_name: spaceName,
            subpath: `/${$actualSubpath}`,
            search: mergeSearch(
              searchQuery,
              buildHideFoldersSearch(spaceHideFolders),
              statusSearch,
              schemaFieldsSearch,
              tagsSearch,
            ),
            limit: itemsPerPage,
            sort_by: SERVER_SORT_FIELD[sortBy] ?? "shortname",
            sort_type:
              sortOrder === "desc" ? SortType.descending : SortType.ascending,
            offset: offset,
            retrieve_json_payload: true,
            retrieve_attachments: false,
            exact_subpath: !expandChildren,
          },
          DmartScope.managed,
        );

      if (response && response.records) {
        $allContents = recordsOf(response);
        totalItemsCount = resolveTotal(response.attributes?.total, response.records.length);
        totalPages = Math.ceil(totalItemsCount / itemsPerPage) || 1;

        applyFilters();
      } else {
        $allContents = [];
        displayedContents = [];
        totalItemsCount = 0;
        totalPages = 1;
      }
    } catch (err) {
      log.error("Error fetching space contents:", err);
      error = $_("admin_content.error.failed_load_contents");
      $allContents = [];
      displayedContents = [];
      totalItemsCount = 0;
      totalPages = 1;
    } finally {
      $isLoading = false;
      isInitialLoad = false;
    }
  }

  function applyFilters() {
    // Already in server order — see SERVER_SORT_FIELD.
    const filtered = [...$allContents];

    displayedContents = filtered;
  }

  function goToPage(page: number) {
    if (page >= 1 && page <= totalPages) {
      currentPage = page;
      loadContents(false);
      // Scroll to top of table
      const tableContainer = document.querySelector(".overflow-x-auto");
      if (tableContainer) {
        tableContainer.scrollIntoView({ behavior: "smooth", block: "start" });
      }
    }
  }

  function handleItemsPerPageChange(newItemsPerPage: number) {
    itemsPerPage = newItemsPerPage;
    if (typeof localStorage !== "undefined") {
      localStorage.setItem(ITEMS_PER_PAGE_KEY, String(newItemsPerPage));
    }
    currentPage = 1;
    loadContents(true);
  }

  function handleItemClick(item: EntryRecord) {
    if (item.resource_type === "folder") {
      const newSubpath = `${subpath}-${item.shortname}`;
      goto("/dashboard/admin/[space_name]/[subpath]", {
        space_name: spaceName,
        subpath: newSubpath,
      });
    } else {
      goto(
        "/dashboard/admin/[space_name]/[subpath]/[shortname]/[resource_type]",
        {
          space_name: spaceName,
          subpath: subpath,
          shortname: item.shortname,
          resource_type: item.resource_type,
        },
      );
    }
  }

  let showCreateItemModal = $state(false);
  let isCreatingItem = $state(false);
  let createItemMeta = $state<CreateItemMeta>({});
  let validateCreateItemForm = $state<(() => boolean) | null>(null);
  // Validation for the resource-type-specific form (user/role/permission).
  let validateCreateRTForm = $state<(() => boolean) | null>(null);

  // The resource type the modal creates, derived from the current location
  // (cxb style): in the management space, /users -> user, /roles -> role,
  // /permissions -> permission; everywhere else -> content.
  let createItemResourceType = $state<ResourceType>(ResourceType.content);

  function detectCreateResourceType(): ResourceType {
    const sp = ($actualSubpath || "").replace(/^\/+|\/+$/g, "");
    if (spaceName === MANAGEMENT_SPACE) {
      if (sp === "users") return ResourceType.user;
      if (sp === "roles") return ResourceType.role;
      if (sp === "permissions") return ResourceType.permission;
    }
    return ResourceType.content;
  }

  // The "create" permission gate must use the SAME resource type the modal
  // will submit — gating on content in management/users would hide the button
  // from operators with user-create grants and show it to operators whose
  // create request the backend then rejects.
  const createResourceType = $derived(detectCreateResourceType());

  // Dynamic schema (payload) for the new item, driven by the folder's
  // `content_schema_shortnames`: 0 -> none, 1 -> auto, >1 -> user selects.
  let createItemSchemaShortnames = $state<string[]>([]);
  let selectedCreateSchemaShortname = $state("");
  let createSchema = $state<Schema | null>(null);
  let createSchemaFormData = $state<JsonObject>({});
  let loadingCreateSchema = $state(false);

  async function loadCreateItemSchema(shortname: string) {
    if (!shortname) {
      createSchema = null;
      createSchemaFormData = {};
      return;
    }
    loadingCreateSchema = true;
    createSchema = null;
    createSchemaFormData = {};
    try {
      const response = await getEntity(
        shortname,
        spaceName,
        "/schema",
        ResourceType.content,
        DmartScope.managed,
        true,
        true,
      );
      createSchema = asFormSchema(response?.payload?.body);
    } catch (err) {
      log.error("Error loading schema for new item:", err);
    } finally {
      loadingCreateSchema = false;
    }
  }

  function handleCreateSchemaChange(event: Event) {
    selectedCreateSchemaShortname = (event.target as HTMLSelectElement).value;
    loadCreateItemSchema(selectedCreateSchemaShortname);
  }

  function handleCreateItem() {
    createItemResourceType = detectCreateResourceType();
    createItemMeta = {
      shortname: null,
      is_active: true,
      displayname: { en: null, ar: null, ku: null },
      description: { en: null, ar: null, ku: null },
    };
    validateCreateRTForm = null;

    // Reset dynamic-schema state.
    createItemSchemaShortnames = [];
    selectedCreateSchemaShortname = "";
    createSchema = null;
    createSchemaFormData = {};

    // The dynamic schema form only applies to plain content entries. Roles,
    // permissions and users carry their own meta props via their forms.
    if (createItemResourceType === ResourceType.content) {
      const schemaShortnames = folderBody?.content_schema_shortnames;
      createItemSchemaShortnames = Array.isArray(schemaShortnames)
        ? schemaShortnames.filter((s): s is string => typeof s === "string")
        : [];
      // 0 schemas -> nothing; 1 -> fetch & render; >1 -> wait for selection.
      if (createItemSchemaShortnames.length === 1) {
        selectedCreateSchemaShortname = createItemSchemaShortnames[0];
        loadCreateItemSchema(selectedCreateSchemaShortname);
      }
    }

    showCreateItemModal = true;
  }

  async function handleSaveItem(event: Event) {
    event.preventDefault();
    if (validateCreateItemForm && !validateCreateItemForm()) return;
    if (validateCreateRTForm && !validateCreateRTForm()) return;
    isCreatingItem = true;

    try {
      // The meta form and the resource-type-specific form both bind to
      // `createItemMeta`, so it already holds the merged attributes
      // (shortname + base meta + user/role/permission props).
      const attributes: JsonObject = { ...$state.snapshot(createItemMeta) };
      const shortname =
        typeof attributes.shortname === "string" && attributes.shortname
          ? attributes.shortname
          : "auto";
      delete attributes.shortname;

      // Persist the dynamic-schema form as the item's JSON payload (content only).
      if (
        createItemResourceType === ResourceType.content &&
        createSchema &&
        selectedCreateSchemaShortname
      ) {
        attributes.payload = {
          content_type: ContentType.json,
          schema_shortname: selectedCreateSchemaShortname,
          // Only send the inputs the user actually filled — the schema form
          // pre-initializes every property ('' / null / [] / {}).
          body:
            pruneEmptyFormValues($state.snapshot(createSchemaFormData)) ?? {},
        };
      }

      const response = await Dmart.request({
        space_name: spaceName,
        request_type: RequestType.create,
        records: [
          {
            resource_type: createItemResourceType,
            shortname,
            subpath: `/${$actualSubpath}`,
            attributes,
          },
        ],
      });

      if (response?.status === "success") {
        showCreateItemModal = false;
        successToastMessage($_("toast.item_created"));
        const createdShortname = response.records?.[0]?.shortname;
        // Only plain content has a generic edit page in this browser; for
        // user/role/permission just refresh the listing.
        if (
          createItemResourceType === ResourceType.content &&
          createdShortname
        ) {
          goto(
            "/dashboard/admin/[space_name]/[subpath]/[shortname]/[resource_type]",
            {
              space_name: spaceName,
              subpath: subpath,
              shortname: createdShortname,
              resource_type: ResourceType.content,
            },
          );
        } else {
          await loadContents(true);
        }
      } else {
        errorToastMessage($_("toast.item_create_failed"));
      }
    } catch (err) {
      log.error("Error creating item:", err);
      errorToastMessage(
        serverMessage(err) || $_("toast.item_create_failed"),
      );
    } finally {
      isCreatingItem = false;
    }
  }

  // Delete confirmation dialog state
  let showDeleteDialog = $state(false);
  let itemToDelete = $state<EntryRecord | null>(null);
  let forceDelete = $state(false);

  function openDeleteDialog(item: EntryRecord, event: Event) {
    event.stopPropagation();
    itemToDelete = item;
    forceDelete = false;
    showDeleteDialog = true;
  }

  function closeDeleteDialog() {
    showDeleteDialog = false;
    itemToDelete = null;
  }

  async function performDelete() {
    if (!itemToDelete) return;
    const success = await deleteEntity(
      itemToDelete.shortname,
      spaceName,
      `/${$actualSubpath}`,
      asResourceType(itemToDelete.resource_type),
      forceDelete,
    );
    if (!success) throw new Error($_("toast.item_delete_failed"));
  }

  async function afterDelete() {
    successToastMessage($_("toast.item_deleted"));
    closeDeleteDialog();
    await loadContents(true);
  }

  function getItemIcon(item: EntryRecord) {
    switch (item.resource_type) {
      case "folder":
        return "📁";
      case "content":
        return "📄";
      case "post":
        return "📝";
      case "ticket":
        return "🎫";
      case "user":
        return "👤";
      case "media":
        return "🖼️";
      default:
        return "📋";
    }
  }

  function getResourceTypeColor(resourceType: string) {
    switch (resourceType) {
      case "folder":
        return "bg-info-soft text-info";
      case "content":
        return "bg-success-soft text-success";
      case "post":
        return "bg-primary-soft text-primary";
      case "ticket":
        return "bg-warning-soft text-warning";
      case "user":
        return "bg-primary-soft text-primary";
      case "media":
        return "bg-primary-soft text-primary";
      default:
        return "bg-surface-3 text-text-muted";
    }
  }

  function getDisplayName(item: EntryRecord) {
    if (item.attributes?.displayname) {
      return (
        item.attributes.displayname.ar ||
        item.attributes.displayname.en ||
        item.shortname
      );
    }
    return item.shortname;
  }

  function navigateToBreadcrumb(path: string | null) {
    const target = parseBreadcrumbPath(path);
    if (!target) return;
    if (target.kind === "admin-root") {
      goto("/dashboard/admin");
    } else if (target.kind === "space-root") {
      goto("/dashboard/admin/[space_name]", {
        space_name: target.spaceName,
      });
    } else {
      goto("/dashboard/admin/[space_name]/[subpath]", {
        space_name: target.spaceName,
        subpath: target.subpath,
      });
    }
  }

  function clearFilters() {
    searchQuery = "";
    statusFilter = { active: false, inactive: false };
    schemaFieldFilters = {};
    selectedTags = [];
    sortBy = "name";
    sortOrder = "asc";
    currentPage = 1;

    // Clear localStorage for filters
    if (typeof localStorage !== "undefined") {
      localStorage.removeItem(SORT_BY_KEY);
      localStorage.removeItem(SORT_ORDER_KEY);
    }

    loadContents(true);
  }

  function toggleStatusFilter(key: "active" | "inactive") {
    statusFilter = { ...statusFilter, [key]: !statusFilter[key] };
    currentPage = 1;
    loadContents(true);
  }

  function toggleSchemaFieldOption(fieldKey: string, optionValue: string) {
    const current = { ...(schemaFieldFilters[fieldKey] || {}) };
    current[optionValue] = !current[optionValue];
    schemaFieldFilters = { ...schemaFieldFilters, [fieldKey]: current };
    currentPage = 1;
    loadContents(true);
  }

  function clearPanelFilters() {
    statusFilter = { active: false, inactive: false };
    schemaFieldFilters = {};
    currentPage = 1;
    loadContents(true);
  }

  function toggleTag(tag: string) {
    if (selectedTags.includes(tag)) {
      selectedTags = selectedTags.filter((t) => t !== tag);
    } else {
      selectedTags = [...selectedTags, tag];
    }
    currentPage = 1;
    loadContents(true);
  }

  const displayedTags = $derived.by(() => {
    if (showAllTags) return availableTags;
    return availableTags.slice(0, 12);
  });

  // Bulk selection functions
  function toggleItemSelection(shortname: string) {
    if (selectedItems.has(shortname)) {
      selectedItems.delete(shortname);
    } else {
      selectedItems.add(shortname);
    }
    selectedItems = new Set(selectedItems);
  }

  function clearSelection() {
    selectedItems.clear();
    selectedItems = new Set();
  }

  async function handleBulkDelete() {
    if (selectedItems.size === 0) return;

    isBulkDeleting = true;
    let successCount = 0;
    let failCount = 0;

    try {
      for (const shortname of selectedItems) {
        const item = displayedContents.find((i) => i.shortname === shortname);
        if (item) {
          try {
            const success = await deleteEntity(
              item.shortname,
              spaceName,
              `/${$actualSubpath}`,
              asResourceType(item.resource_type),
            );
            if (success) {
              successCount++;
            } else {
              failCount++;
            }
          } catch {
            failCount++;
          }
        }
      }

      if (successCount > 0) {
        successToastMessage(
          $_("admin_content.bulk_actions.delete_success", {
            values: { count: successCount },
          }),
        );
      }
      if (failCount > 0) {
        errorToastMessage(
          $_("admin_content.bulk_actions.delete_failed", {
            values: { count: failCount },
          }),
        );
      }

      clearSelection();
      await loadContents(true);
    } catch (err) {
      log.error("Error in bulk delete:", err);
      errorToastMessage($_("admin_content.bulk_actions.delete_error"));
    } finally {
      isBulkDeleting = false;
    }
  }

  async function handleBulkTrash() {
    if (selectedItems.size === 0) return;

    isBulkDeleting = true;
    let successCount = 0;
    let failCount = 0;

    try {
      for (const shortname of selectedItems) {
        const item = displayedContents.find((i) => i.shortname === shortname);
        if (item) {
          try {
            // TODO: Replace with actual trashEntity function when available
            const success = await deleteEntity(
              item.shortname,
              spaceName,
              `/${$actualSubpath}`,
              asResourceType(item.resource_type),
            );
            if (success) {
              successCount++;
            } else {
              failCount++;
            }
          } catch {
            failCount++;
          }
        }
      }

      if (successCount > 0) {
        successToastMessage(
          $_("admin_content.bulk_actions.trash_success", {
            values: { count: successCount },
          }),
        );
      }
      if (failCount > 0) {
        errorToastMessage(
          $_("admin_content.bulk_actions.trash_failed", {
            values: { count: failCount },
          }),
        );
      }

      clearSelection();
      await loadContents(true);
    } catch (err) {
      log.error("Error in bulk trash:", err);
      errorToastMessage($_("admin_content.bulk_actions.trash_error"));
    } finally {
      isBulkDeleting = false;
    }
  }

  function toggleSortOrder() {
    sortOrder = sortOrder === "asc" ? "desc" : "asc";
    if (typeof localStorage !== "undefined") {
      localStorage.setItem(SORT_ORDER_KEY, sortOrder);
    }
    loadContents(true);
  }

  // Bulk edit functions
  function openBulkEditModal() {
    if (selectedItems.size === 0) return;

    // Get effective columns to determine which fields to include
    // Must match the table view logic exactly
    const effectiveColumns =
      indexAttributes &&
      indexAttributes.length > 0 &&
      indexAttributes.some((attr) => attr && Object.keys(attr).length > 0)
        ? indexAttributes
        : [
            { key: "status", name: "Status" },
            { key: "created_at", name: $_("data_table.columns.created_at") },
            { key: "updated_at", name: $_("data_table.columns.updated_at") },
            { key: "author", name: "Author" },
          ];

    const initialData: Record<string, JsonObject> = {};
    const selectedShortnames = Array.from(selectedItems);
    for (const shortname of selectedShortnames) {
      const item = $allContents.find((i) => i.shortname === shortname);
      if (item) {
        const editData: JsonObject = {
          shortname: item.shortname,
          new_shortname: item.shortname,
          is_active: item.attributes?.is_active ?? true,
          tags: [...(item.attributes?.tags || [])],
          displayname: { ...(item.attributes?.displayname || {}) },
          description: { ...(item.attributes?.description || {}) },
          owner_shortname: item.attributes?.owner_shortname || "",
          created_at: item.attributes?.created_at || "",
          updated_at: item.attributes?.updated_at || "",
          schema_shortname: item.attributes?.schema_shortname || "",
        };

        for (const col of effectiveColumns) {
          const key = col.key;
          const fieldType = getFieldType(key);

          if (key === "displayname" || key === "attributes.displayname") {
            editData[key] = formatValueForEdit(
              item.attributes?.displayname,
              "localized",
            );
          } else if (
            key === "description" ||
            key === "attributes.description"
          ) {
            editData[key] = formatValueForEdit(
              item.attributes?.description,
              "localized",
            );
          } else if (key === "status") {
            editData.is_active = formatValueForEdit(
              item.attributes?.is_active,
              "boolean",
            );
          } else if (key === "tags") {
            editData.tags = formatValueForEdit(item.attributes?.tags, "array");
          } else if (key === "shortname") {
            editData.new_shortname = item.shortname;
          } else if (key === "author" || key === "owner_shortname") {
            editData.owner_shortname = formatValueForEdit(
              item.attributes?.owner_shortname,
              "string",
            );
          } else if (key === "created_at") {
            editData.created_at = formatValueForEdit(
              item.attributes?.created_at,
              "string",
            );
          } else if (key === "updated_at") {
            editData.updated_at = formatValueForEdit(
              item.attributes?.updated_at,
              "string",
            );
          } else if (key === "schema_shortname") {
            editData.schema_shortname = formatValueForEdit(
              item.attributes?.schema_shortname,
              "string",
            );
          } else if (key.includes(".")) {
            const parts = key.split(".");
            let current: unknown = item;
            for (const part of parts) {
              if (current === null || current === undefined) break;
              current = isIndexable(current) ? current[part] : undefined;
            }
            const rawValue = current !== undefined ? current : "";
            editData[key] = formatValueForEdit(rawValue, fieldType);
          } else {
            const rawValue = getAttributeValue(item, key);
            editData[key] = formatValueForEdit(rawValue, fieldType);
          }
        }

        initialData[shortname] = editData;
      }
    }

    bulkEditData = { ...initialData };
    showBulkEditModal = true;
  }

  /** A bulk-edit cell read as a list (tags and other array fields). */
  function editList(row: JsonObject, key: string): unknown[] {
    const value = row[key];
    return Array.isArray(value) ? value : [];
  }

  /** A bulk-edit cell read as text, "" when it holds none. */
  function editText(row: JsonObject, key: string): string {
    const value = row[key];
    return typeof value === "string" ? value : "";
  }

  /** One language of a localized bulk-edit cell, "" when unset. */
  function editLocale(row: JsonObject, key: string, lang: string): string {
    const value = row[key];
    if (!isJsonObject(value)) return "";
    const text = value[lang];
    return typeof text === "string" ? text : "";
  }

  function closeBulkEditModal() {
    showBulkEditModal = false;
    bulkEditData = {};
  }

  function updateBulkEditField(shortname: string, field: string, value: unknown) {
    if (bulkEditData[shortname]) {
      bulkEditData[shortname] = { ...bulkEditData[shortname], [field]: value };
      bulkEditData = { ...bulkEditData };
    }
  }

  function updateBulkEditLocalizedField(
    shortname: string,
    field: string,
    locale: string,
    value: string,
  ) {
    if (bulkEditData[shortname]) {
      const existing = bulkEditData[shortname][field];
      const currentField = isJsonObject(existing) ? existing : {};
      bulkEditData[shortname] = {
        ...bulkEditData[shortname],
        [field]: { ...currentField, [locale]: value },
      };
      bulkEditData = { ...bulkEditData };
    }
  }

  async function handleBulkSave() {
    if (Object.keys(bulkEditData).length === 0) return;

    isBulkSaving = true;

    try {
      const records: ActionRequestRecord[] = [];

      for (const [shortname, editData] of Object.entries(bulkEditData)) {
        const item = $allContents.find((i) => i.shortname === shortname);
        if (item) {
          const attributes: JsonObject = {};

          for (const [key, value] of Object.entries(editData)) {
            if (key === "shortname" || key === "new_shortname") continue;

            const baseKey = key.startsWith("attributes.") ? key.slice(11) : key;
            if (
              (baseKey === "displayname" || baseKey === "description") &&
              (!value ||
                (typeof value === "object" && Object.keys(value).length === 0))
            ) {
              continue;
            }

            const fieldType = getFieldType(key);
            const parsedValue = parseValueByType(value, fieldType);

            const attrKey = key.startsWith("attributes.") ? key.slice(11) : key;

            setNestedValue(attributes, attrKey, parsedValue);
          }

          records.push({
            resource_type: asResourceType(item.resource_type),
            shortname: item.shortname,
            subpath: `/${$actualSubpath}`,
            attributes,
          });
        }
      }

      const response = await Dmart.request({
        space_name: spaceName,
        request_type: RequestType.update,
        records,
      });

      let successCount = 0;
      let failCount = 0;

      if (response && response.status === "success") {
        successCount = records.length;
      } else {
        failCount = records.length;
      }

      if (successCount > 0) {
        successToastMessage(
          $_("admin_content.bulk_actions.edit_success", {
            values: {
              count: successCount,
            },
          }),
        );
      }
      if (failCount > 0) {
        errorToastMessage(
          $_("admin_content.bulk_actions.edit_failed", {
            values: {
              count: failCount,
            },
          }),
        );
      }

      closeBulkEditModal();
      clearSelection();
      await loadContents(true);
    } catch (err) {
      log.error("Error in bulk edit:", err);
      errorToastMessage($_("admin_content.bulk_actions.edit_error"));
    } finally {
      isBulkSaving = false;
    }
  }

  const totalItemsDerived = $derived.by(() => totalItemsCount);

  let isCreatingFolder = $state(false);
  let metaContent = $state<MetaFormData>({});
  let showCreateFolderModal = $state(false);
  let validateMetaForm = $state<(() => boolean) | null>(null);
  // A new folder's listing settings: the defaults, sorted newest first. The
  // FolderForm applies the same defaults on mount, so the shape is the one it
  // has always bound to.
  const newFolderContent = () =>
    applyFolderContentDefaults({
      title: "",
      content: "",
      is_active: true,
      tags: [],
      sort_by: "created_at",
      sort_type: "descending",
    });
  let folderContent = $state(newFolderContent());

  function handleCreateFolder() {
    folderContent = newFolderContent();
    showCreateFolderModal = true;
  }

  async function handleSaveFolder(event: Event) {
    event.preventDefault();
    isCreatingFolder = true;

    try {
      const data = {
        shortname: metaContent.shortname || "auto",
        displayname: metaContent.displayname,
        description: metaContent.description,
        folderContent: folderContent,
        is_active: true,
      };
      const response = await createFolder(spaceName, $actualSubpath, data);

      if (response) {
        showCreateFolderModal = false;
        successToastMessage($_("toast.folder_created"));
        await loadContents(true);
      } else {
        errorToastMessage($_("toast.folder_create_failed"));
      }
    } catch (err) {
      log.error("Error creating folder:", err);
      errorToastMessage(
        $_("toast.folder_create_failed") + ": " + errorMessage(err),
      );
    } finally {
      isCreatingFolder = false;
    }
  }

  let showCreateSchemaModal = $state(false);
  let schemaContent = $state<JsonObject>({});
  let isCreatingSchema = $state(false);
  let showCreateWorkflowModal = $state(false);
  let workflowContent = $state<WorkflowContent>(normalizeWorkflowContent({}));
  let isCreatingWorkflow = $state(false);

  // Column Settings
  let showColumnSettingsModal = $state(false);
  let editingIndexAttributes = $state<IndexAttribute[]>([]);
  let isSavingColumns = $state(false);
  let editingMeta = $state<{
    displayname: { en: string | null; ar: string | null; ku: string | null };
    description: { en: string | null; ar: string | null; ku: string | null };
    is_active: boolean;
  }>({
    displayname: { en: null, ar: null, ku: null },
    description: { en: null, ar: null, ku: null },
    is_active: true,
  });

  // CSV Import/Export
  let isCSVUploadModalOpen = $state(false);
  let isCSVDownloadModalOpen = $state(false);

  // Computed permissions from folder metadata
  let canUploadCSV = $derived(folderBody?.allow_upload_csv === true);
  let canDownloadCSV = $derived(folderBody?.allow_csv === true);

  function handleOpenColumnSettings() {
    // Preserve the saved index_attributes exactly. Auto-filling defaults here
    // caused the table to silently change layout after editing unrelated
    // fields (e.g. display name) because the defaults differed from the
    // render-time fallback in the table.
    editingIndexAttributes = JSON.parse(JSON.stringify(indexAttributes));
    const meta = folderMetadata;
    editingMeta = {
      displayname: {
        en: meta?.displayname?.en ?? null,
        ar: meta?.displayname?.ar ?? null,
        ku: meta?.displayname?.ku ?? null,
      },
      description: {
        en: meta?.description?.en ?? null,
        ar: meta?.description?.ar ?? null,
        ku: meta?.description?.ku ?? null,
      },
      is_active: meta?.is_active === false ? false : true,
    };
    showColumnSettingsModal = true;
  }

  function addColumnSetting() {
    editingIndexAttributes = [...editingIndexAttributes, { key: "", name: "" }];
  }

  function removeColumnSetting(index: number) {
    editingIndexAttributes = editingIndexAttributes.filter(
      (_, i) => i !== index,
    );
  }

  /** A column's label in the current language (its key when it has none). */
  function columnLabel(attr: IndexAttribute): string {
    if (typeof attr.name === "string") return attr.name;
    return (
      attr.name[$locale ?? ""] ||
      attr.name.en ||
      attr.name.ar ||
      attr.name.ku ||
      attr.key
    );
  }

  /** True when a column has a label in some language. */
  function hasColumnName(name: IndexAttribute["name"] | undefined): boolean {
    if (typeof name === "string") return name.trim() !== "";
    return Object.values(name ?? {}).some((text) => !!text?.trim());
  }

  async function handleUpdateColumns() {
    const folder = folderMetadata;
    if (!folder) {
      errorToastMessage($_("toast.folder_update_failed"));
      return;
    }
    isSavingColumns = true;
    try {
      // Drop empty locales so saving English-only edits doesn't wipe out
      // pre-existing Arabic/Kurdish translations on the server.
      const cleanLocaleMap = (m: Record<string, string | null>) => {
        const out: Record<string, string> = {};
        for (const [k, v] of Object.entries(m)) {
          if (typeof v === "string" && v.trim()) out[k] = v;
        }
        return out;
      };
      const cleanedDisplayname = cleanLocaleMap(editingMeta.displayname);
      const cleanedDescription = cleanLocaleMap(editingMeta.description);

      const response = await Dmart.request({
        space_name: spaceName,
        request_type: RequestType.update,
        records: [
          {
            resource_type: ResourceType.folder,
            shortname: folder.shortname,
            subpath: getParentPath(`/${$actualSubpath}`),
            attributes: {
              is_active: editingMeta.is_active,
              ...(Object.keys(cleanedDisplayname).length
                ? { displayname: cleanedDisplayname }
                : {}),
              ...(Object.keys(cleanedDescription).length
                ? { description: cleanedDescription }
                : {}),
              payload: {
                ...folder.payload,
                body: {
                  ...bodyObject(folder.payload),
                  index_attributes: editingIndexAttributes.filter(
                    (a) => a?.key?.trim() && hasColumnName(a?.name),
                  ),
                },
              },
            },
          },
        ],
      });

      if (response && response.status === "success") {
        showColumnSettingsModal = false;
        successToastMessage($_("toast.folder_updated"));
        await loadContents(true);
      } else {
        errorToastMessage($_("toast.folder_update_failed"));
      }
    } catch (err) {
      log.error("Error updating columns:", err);
      errorToastMessage(
        $_("toast.folder_update_failed") + ": " + errorMessage(err),
      );
    } finally {
      isSavingColumns = false;
    }
  }

  function handleCreateSchema() {
    schemaContent = {};
    showCreateSchemaModal = true;
  }

  function handleCreateWorkflow() {
    workflowContent = normalizeWorkflowContent({});
    showCreateWorkflowModal = true;
  }

  async function handleSaveschema(event: Event) {
    event.preventDefault();
    isCreatingSchema = true;

    try {
      const response = await Dmart.request({
        space_name: spaceName,
        request_type: RequestType.create,
        records: [
          {
            resource_type: ResourceType.schema,
            shortname: metaContent.shortname || "auto",
            subpath: `/${$actualSubpath}`,
            attributes: {
              displayname: metaContent.displayname,
              description: metaContent.description,
              payload: {
                body: schemaContent,
                content_type: "json",
              },
              is_active: true,
            },
          },
        ],
      });

      if (response) {
        showCreateSchemaModal = false;
        successToastMessage($_("toast.schema_created"));
        await loadContents(true);
      } else {
        errorToastMessage($_("toast.schema_create_failed"));
      }
    } catch (err) {
      log.error("Error creating schema:", err);
      errorToastMessage(
        $_("toast.schema_create_failed") + ": " + errorMessage(err),
      );
    } finally {
      isCreatingSchema = false;
    }
  }

  async function handleSaveWorkflow(event: Event) {
    event.preventDefault();
    isCreatingWorkflow = true;

    try {
      const response = await Dmart.request({
        space_name: spaceName,
        request_type: RequestType.create,
        records: [
          {
            resource_type: ResourceType.content,
            shortname: metaContent.shortname || "auto",
            subpath: `/${$actualSubpath}`,
            attributes: {
              displayname:
                metaContent.displayname || {
                  ar: workflowContent.name || "",
                  en: workflowContent.name || "",
                },
              description: metaContent.description || {},
              payload: {
                body: workflowContent,
                content_type: "json",
              },
              is_active: true,
            },
          },
        ],
      });

      if (response) {
        showCreateWorkflowModal = false;
        successToastMessage($_("toast.workflow_created"));
        await loadContents(true);
      } else {
        errorToastMessage($_("toast.workflow_create_failed"));
      }
    } catch (err) {
      log.error("Error creating workflow:", err);
      errorToastMessage(
        $_("toast.workflow_create_failed") + ": " + errorMessage(err),
      );
    } finally {
      isCreatingWorkflow = false;
    }
  }
  const indexAttributes = $derived(
    applyFolderContentDefaults(folderBody).index_attributes,
  );

  // Count of distinct filter groups currently constraining results — a
  // group only counts when it's neither "nothing checked" nor "everything
  // checked" (both mean "no constraint", matching the old "All" option UX).
  const activeFilterCount = $derived(
    (statusFilter.active !== statusFilter.inactive ? 1 : 0) +
      schemaFilterFields.reduce((count, field) => {
        const selections = schemaFieldFilters[field.key];
        if (!selections) return count;
        const selectedCount = field.options.filter((o) => selections[o]).length;
        return (
          count +
          (selectedCount > 0 && selectedCount < field.options.length ? 1 : 0)
        );
      }, 0),
  );

  function getAttributeValue(item: EntryRecord | null | undefined, key: string): string {
    if (!item) return "";
    if (!key) return "";
    if (key === "displayname") return getDisplayName(item);
    if (key === "status") {
      return item.attributes?.is_active
        ? $_("admin_content.status.active")
        : $_("admin_content.status.inactive");
    }
    if (key === "author") return item.attributes?.owner_shortname || "Unknown";
    if (key === "updated_at" || key === "created_at") {
      return formatDate(item.attributes?.[key], "date", $locale);
    }

    const findValue = (obj: unknown, k: string): unknown => {
      if (!isIndexable(obj)) return undefined;
      if (obj[k] !== undefined) return obj[k];
      const tk = k.toLowerCase();
      const foundKey = Object.keys(obj).find((ok) => ok.toLowerCase() === tk);
      return foundKey ? obj[foundKey] : undefined;
    };

    let value: unknown;
    if (key.includes(".")) {
      const parts = key.split(".");
      let current: unknown = item;
      for (const part of parts) {
        current = findValue(current, part);
        if (current === undefined || current === null) break;
      }
      value = current;
    } else {
      value =
        findValue(item.attributes?.payload?.body, key) ??
        findValue(item.attributes?.payload, key) ??
        findValue(item.attributes, key) ??
        findValue(item, key);
    }

    if (value === null || value === undefined) return "";

    if (isJsonObject(value)) {
      const localized =
        value[$locale ?? ""] || value.en || value.ar || value.ku;
      if (localized !== undefined) return String(localized);
      return JSON.stringify(value);
    }

    return String(value);
  }
</script>

<div class="min-h-screen bg-surface">
  <div class="bg-surface">
    <div class="mx-auto py-8 max-w-375">
      <div
        class="flex flex-col md:flex-row md:items-center justify-between gap-4"
      >
        <div class="flex items-center gap-4">
          <button
            onclick={() =>
              navigateToBreadcrumb(breadcrumbs[1]?.path || "/dashboard/admin")}
            class="w-10 h-10 bg-primary-soft hover:bg-primary-soft text-primary rounded-xl flex items-center justify-center transition-colors shadow-sm"
            aria-label={$_("admin_space.navigation.go_back")}
          >
            <svg
              class="w-5 h-5"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M15 19l-7-7 7-7"
              ></path>
            </svg>
          </button>
          <div>
            <h1 class="admin-page-title">
              {$_("admin_content.title", {
                values: {
                  name:
                    folderMetadata?.displayname?.[$locale ?? ""] ||
                    folderMetadata?.displayname?.en ||
                    folderMetadata?.displayname?.ar ||
                    folderMetadata?.displayname?.ku ||
                    breadcrumbs[breadcrumbs.length - 1]?.name ||
                    $actualSubpath.split("/").pop(),
                },
              })}
            </h1>
            <nav
              class="flex text-sm text-text-muted font-medium mb-1"
              aria-label={$_("ui.breadcrumb")}
            >
              <ol class="inline-flex items-center space-x-2">
                {#each breadcrumbs as crumb, index (index)}
                  <li class="inline-flex items-center">
                    {#if index > 0}
                      <svg
                        class="w-4 h-4 mx-1 text-text-faint"
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M9 5l7 7-7 7"
                        ></path>
                      </svg>
                    {/if}
                    {#if crumb.path}
                      <button
                        onclick={() => navigateToBreadcrumb(crumb.path)}
                        class="admin-breadcrumb-link"
                      >
                        {crumb.name}
                      </button>
                    {:else}
                      <span class="admin-breadcrumb-current">{crumb.name}</span>
                    {/if}
                  </li>
                {/each}
              </ol>
            </nav>
          </div>
        </div>

        <div class="flex items-center gap-3">
          {#if $actualSubpath !== "/" && $actualSubpath !== ""}
            {#if $actualSubpath !== "schema"}
              {#if $can("create", spaceName, $actualSubpath, ResourceType.folder)}
                <button
                  onclick={handleCreateFolder}
                  class="bg-surface-2 hover:bg-surface border border-border text-text px-4 py-2 rounded-[14px] cursor-pointer font-medium transition-colors duration-200 flex items-center gap-2 shadow-sm"
                >
                  <svg
                    class="w-4 h-4 text-text-muted"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z"
                    ></path>
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M12 10v4m2-2h-4"
                    ></path>
                  </svg>
                  {$_("admin_content.actions.create_folder")}
                </button>
              {/if}

              {#if $can("create", spaceName, $actualSubpath, createResourceType)}
                <button
                  onclick={handleCreateItem}
                  class="bg-primary hover:bg-primary-hover text-text-on-primary px-4 py-2 rounded-[14px] cursor-pointer font-medium transition-colors duration-200 flex items-center gap-2 shadow-sm"
                >
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M12 4v16m8-8H4m4-8h8v8H8z"
                    ></path>
                  </svg>
                  {$_("admin_content.actions.create_new_item")}
                </button>
              {/if}
            {/if}

            {#if $actualSubpath === "schema" && $can("create", spaceName, $actualSubpath, ResourceType.schema)}
              <button
                onclick={handleCreateSchema}
                class="bg-primary hover:bg-primary-hover text-text-on-primary px-4 py-2 rounded-xl font-medium transition-colors shadow-sm"
              >
                {$_("admin_content.actions.create_schema")}
              </button>
            {/if}
            {#if $actualSubpath === "workflows" && $can("create", spaceName, $actualSubpath, ResourceType.content)}
              <button
                onclick={handleCreateWorkflow}
                class="bg-primary hover:bg-primary-hover text-text-on-primary px-4 py-2 rounded-xl font-semibold transition-colors shadow-sm"
              >
                Workflow
              </button>
            {/if}
          {/if}
        </div>
      </div>
    </div>
  </div>

  <div class="mx-auto pb-12 max-w-375">
    {#if $isLoading || isInitialLoad}
      <div class="flex justify-center py-16">
        <div class="spinner spinner-lg"></div>
      </div>
    {:else if error}
      <div
        class="bg-surface-2 rounded-3xl shadow-[0_2px_8px_rgba(0,0,0,0.04)] border border-border p-12 text-center max-w-lg mx-auto"
      >
        <div
          class="w-16 h-16 bg-danger-soft text-danger rounded-2xl flex items-center justify-center mx-auto mb-4"
        >
          <svg
            class="w-8 h-8"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
            ></path>
          </svg>
        </div>
        <h3 class="text-xl font-bold text-text mb-2">
          {$_("admin_content.error.title")}
        </h3>
        <p class="text-text-muted mb-6">{error}</p>
        <button
          onclick={() => loadContents(true)}
          class="bg-text hover:bg-text text-text-on-primary px-6 py-2.5 rounded-xl font-medium transition-colors"
        >
          {$_("admin_content.error.try_again")}
        </button>
      </div>
    {:else}
      <!-- Stats -->
      <div class="flex gap-2 mb-3">
        <div class="admin-stat-card">
          <div class="flex items-center justify-between gap-2">
            <div>
              <svg
                class="w-4 h-4"
                fill="none"
                stroke="var(--color-primary-500)"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10"
                ></path>
              </svg>
            </div>
            <p class="admin-stat-label">Total</p>
            <h3 class="admin-stat-value">
              {formatNumber(totalItemsDerived, $locale ?? "")}
            </h3>
          </div>
        </div>
        <div class="admin-stat-card">
          <div class="flex items-center justify-between gap-2">
            <div>
              <svg
                class="w-4 h-4"
                fill="none"
                stroke="var(--color-success)"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"
                ></path>
              </svg>
            </div>
            <p class="admin-stat-label">Active</p>
            <h3 class="admin-stat-value">
              {formatNumber(
                $allContents.filter((i) => i.attributes?.is_active).length,
                $locale ?? "",
              )}
            </h3>
          </div>
        </div>
      </div>

      <!-- Tags Section -->
      {#if availableTags.length > 0}
        <div class="tags-section mb-6">
          <div class="tags-label">
            <svg
              class="w-4 h-4 me-1 text-text-faint"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M7 7h.01M7 3h5c.512 0 1.024.195 1.414.586l7 7a2 2 0 010 2.828l-7 7a2 2 0 01-2.828 0l-7-7A1.994 1.994 0 013 12V7a4 4 0 014-4z"
              />
            </svg>
            {$_("space.filter_by_tag_label")}
          </div>
          <div class="tag-pills">
            {#each displayedTags as tag (tag)}
              <button
                onclick={() => toggleTag(tag)}
                class="tag-pill {selectedTags.includes(tag)
                  ? 'tag-pill-active'
                  : ''}"
              >
                <div
                  class="bullet {selectedTags.includes(tag)
                    ? 'bullet-active'
                    : ''}"
                ></div>
                <span>{tag}</span>
                <span class="tag-count">{tagCounts[tag] || 0}</span>
              </button>
            {/each}
            {#if availableTags.length > 12}
              <button
                onclick={() => (showAllTags = !showAllTags)}
                class="tag-pill tag-pill-more"
              >
                {showAllTags
                  ? $_("space.show_less_tags")
                  : $_("space.show_all_tags")}
              </button>
            {/if}
          </div>
        </div>
      {/if}

      <!-- Main Content Container -->
      <div
        class="bg-surface-2 rounded-3xl shadow-[0_2px_8px_rgba(0,0,0,0.04)] border border-border overflow-hidden"
      >
        <!-- Search and Filters Bar -->
        <div class="p-6 border-b border-border">
          <div
            class="flex flex-col md:flex-row md:items-center justify-between gap-4"
          >
            <div class="relative max-w-sm flex-1">
              <div
                class="absolute inset-y-0 start-0 ps-4 flex items-center pointer-events-none"
              >
                <svg
                  class="h-5 w-5 text-text-faint"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                  ></path>
                </svg>
              </div>
              <input
                type="text"
                bind:value={searchQuery}
                oninput={handleSearchInput}
                placeholder={$_("admin_content.search.placeholder")}
                class="block w-full ps-11 pe-10 py-2.5 bg-surface border-none rounded-xl text-sm focus:ring-2 focus:ring-primary focus:bg-surface-2 transition-colors"
                title={$_("admin_content.search.placeholder")}
                aria-label={$_("admin_content.search.placeholder")}
              />
              {#if searchQuery}
                <button
                  onclick={() => {
                    searchQuery = "";
                    loadContents(true);
                  }}
                  aria-label={$_("ui.clear_search")}
                  class="absolute inset-y-0 end-0 pe-3 flex items-center"
                >
                  <svg
                    class="h-5 w-5 text-text-faint hover:text-text-muted transition-colors"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M6 18L18 6M6 6l12 12"
                    ></path>
                  </svg>
                </button>
              {/if}
            </div>

            <div class="flex flex-wrap items-center gap-3">
              <select
                bind:value={sortBy}
                onchange={(e) => {
                  if (typeof localStorage !== "undefined") {
                    localStorage.setItem(
                      SORT_BY_KEY,
                      (e.target as HTMLSelectElement).value,
                    );
                  }
                  currentPage = 1;
                  loadContents(true);
                }}
                class="bg-surface border-none text-sm font-medium text-text rounded-xl px-4 py-2.5 focus:ring-2 focus:ring-primary cursor-pointer"
                title={$_("catalog_contents.filters.sort_by")}
                aria-label={$_("catalog_contents.filters.sort_by")}
              >
                {#each sortOptions as option (option.value)}
                  <option value={option.value}>{option.label}</option>
                {/each}
              </select>

              <button
                onclick={toggleSortOrder}
                class="p-2.5 bg-surface text-text-muted hover:text-text hover:bg-surface-3 rounded-xl transition-colors"
                title={$_("admin_content.filters.toggle_sort")}
                aria-label={$_("admin_content.filters.toggle_sort")}
              >
                <svg
                  class="w-5 h-5 {sortOrder === 'desc'
                    ? 'rotate-180'
                    : ''} transition-transform duration-200"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M7 16V4m0 0L3 8m4-4l4 4m6 0v12m0 0l4-4m-4 4l-4-4"
                  ></path>
                </svg>
              </button>

              <div class="relative">
                <button
                  onclick={() => (showFilterPanel = !showFilterPanel)}
                  class="p-2.5 bg-surface text-text-muted hover:text-primary hover:bg-primary-soft rounded-xl transition-all border border-transparent hover:border-primary/30 relative"
                  title={$_("admin_content.filters.filter_by_column")}
                  aria-label={$_("admin_content.filters.filter_by_column")}
                >
                  <svg
                    class="w-5 h-5"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M3 4h18M6 8h12M9 12h6M11 16h2"
                    ></path>
                  </svg>
                  {#if activeFilterCount > 0}
                    <span
                      class="absolute -top-1 -end-1 bg-primary text-text-on-primary text-[10px] font-bold rounded-full w-4 h-4 flex items-center justify-center"
                    >
                      {activeFilterCount}
                    </span>
                  {/if}
                </button>

                {#if showFilterPanel}
                  <button
                    type="button"
                    class="fixed inset-0 z-10 cursor-default"
                    onclick={() => (showFilterPanel = false)}
                    aria-label={$_("admin_content.modal.close")}
                  ></button>
                  <div
                    class="absolute end-0 mt-2 w-72 bg-surface-2 border border-border rounded-xl shadow-lg z-20 p-4 max-h-96 overflow-y-auto"
                  >
                    <div class="flex items-center justify-between mb-3">
                      <span class="text-sm font-semibold text-text">
                        {$_("admin_content.filters.filter_by_column")}
                      </span>
                      {#if activeFilterCount > 0}
                        <button
                          onclick={clearPanelFilters}
                          class="text-xs text-primary hover:text-primary font-medium"
                        >
                          {$_("admin_content.filters.clear_all")}
                        </button>
                      {/if}
                    </div>

                    <div class="mb-4">
                      <div
                        class="text-xs font-semibold text-text-faint uppercase tracking-wider mb-2"
                      >
                        {$_("catalog_contents.filters.status")}
                      </div>
                      <label
                        class="flex items-center gap-2 mb-1.5 cursor-pointer"
                      >
                        <input
                          type="checkbox"
                          checked={statusFilter.active}
                          onchange={() => toggleStatusFilter("active")}
                          class="w-4 h-4 text-primary border-border-strong rounded focus:ring-primary cursor-pointer"
                        />
                        <span class="text-sm text-text"
                          >{$_("admin_content.status.active")}</span
                        >
                      </label>
                      <label class="flex items-center gap-2 cursor-pointer">
                        <input
                          type="checkbox"
                          checked={statusFilter.inactive}
                          onchange={() => toggleStatusFilter("inactive")}
                          class="w-4 h-4 text-primary border-border-strong rounded focus:ring-primary cursor-pointer"
                        />
                        <span class="text-sm text-text"
                          >{$_("admin_content.status.inactive")}</span
                        >
                      </label>
                    </div>

                    {#each schemaFilterFields as field (field.key)}
                      <div class="mb-4">
                        <div
                          class="text-xs font-semibold text-text-faint uppercase tracking-wider mb-2"
                        >
                          {field.label}
                        </div>
                        {#each field.options as option (option)}
                          <label
                            class="flex items-center gap-2 mb-1.5 cursor-pointer"
                          >
                            <input
                              type="checkbox"
                              checked={!!schemaFieldFilters[field.key]?.[
                                option
                              ]}
                              onchange={() =>
                                toggleSchemaFieldOption(field.key, option)}
                              class="w-4 h-4 text-primary border-border-strong rounded focus:ring-primary cursor-pointer"
                            />
                            <span class="text-sm text-text">
                              {field.type === "boolean"
                                ? option === "true"
                                  ? $_("common.yes")
                                  : $_("common.no")
                                : option}
                            </span>
                          </label>
                        {/each}
                      </div>
                    {/each}

                    {#if schemaFilterFields.length === 0}
                      <p class="text-xs text-text-faint">
                        {$_("admin_content.filters.no_schema_filters")}
                      </p>
                    {/if}
                  </div>
                {/if}
              </div>

              <button
                onclick={handleOpenColumnSettings}
                class="p-2.5 bg-surface text-text-muted hover:text-primary hover:bg-primary-soft rounded-xl transition-all border border-transparent hover:border-primary/30"
                title={$_("admin_content.settings_modal.title")}
                aria-label={$_("admin_content.settings_modal.title")}
              >
                <svg
                  class="w-5 h-5"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z"
                  ></path>
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
                  ></path>
                </svg>
              </button>

              <!-- CSV Upload Button -->
              {#if canUploadCSV}
                <button
                  onclick={() => (isCSVUploadModalOpen = true)}
                  class="p-2.5 bg-surface text-text-muted hover:text-success hover:bg-success-soft rounded-xl transition-all border border-transparent hover:border-success/30"
                  title={$_("users_page.upload_csv")}
                  aria-label={$_("users_page.upload_csv")}
                >
                  <UploadOutline class="w-5 h-5" />
                </button>
              {/if}

              <!-- CSV Download Button -->
              {#if canDownloadCSV}
                <button
                  onclick={() => (isCSVDownloadModalOpen = true)}
                  class="p-2.5 bg-surface text-text-muted hover:text-info hover:bg-info-soft rounded-xl transition-all border border-transparent hover:border-info/30"
                  title={$_("users_page.download_csv")}
                  aria-label={$_("users_page.download_csv")}
                >
                  <DownloadOutline class="w-5 h-5" />
                </button>
              {/if}
            </div>
          </div>
        </div>

        {#if displayedContents.length === 0}
          <div class="text-center py-16">
            <div
              class="w-16 h-16 bg-surface rounded-2xl flex items-center justify-center mx-auto mb-4 text-text-faint"
            >
              <svg
                class="w-8 h-8"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M9 13h6m-3-3v6m5 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
                ></path>
              </svg>
            </div>
            <h3 class="text-lg font-bold text-text mb-2">
              {$_("admin_content.empty.title")}
            </h3>
            <p class="text-text-muted mb-6">
              {searchQuery || activeFilterCount > 0 || selectedTags.length > 0
                ? $_("admin_content.empty.no_matches")
                : $_("admin_content.empty.description")}
            </p>
            {#if searchQuery || activeFilterCount > 0 || selectedTags.length > 0}
              <button
                onclick={clearFilters}
                class="text-primary hover:text-primary font-medium"
              >
                {$_("admin_content.filters.clear_all")}
              </button>
            {/if}
          </div>
        {:else}
          <DataTable
            items={displayedContents}
            indexAttributes={visibleColumns(
              indexAttributes,
              $permissions,
              spaceName,
              $actualSubpath,
              ResourceType.content,
            )}
            selectable={true}
            {selectedItems}
            onSelectAll={(checked) => {
              if (checked) {
                selectedItems = new Set(
                  displayedContents.map((item) => item.shortname),
                );
              } else {
                selectedItems = new Set();
              }
            }}
            onSelectItem={(shortname) => toggleItemSelection(shortname)}
            onRowClick={(item) => handleItemClick(item)}
            loading={$isLoading || isInitialLoad}
            {currentPage}
            {totalPages}
            totalItems={totalItemsDerived}
            {itemsPerPage}
            onPageChange={(page) => goToPage(page)}
            onItemsPerPageChange={(count) => handleItemsPerPageChange(count)}
            {itemsPerPageOptions}
          >
            {#snippet cell({ item, attr })}
              {#if attr.key === "displayname"}
                <div class="flex flex-col">
                  <span
                    class="inline-flex w-fit items-center px-1.5 py-0.5 rounded text-[10px] font-bold uppercase tracking-wider mb-1.5 {getResourceTypeColor(
                      item.resource_type,
                    )}"
                  >
                    {item.resource_type}
                  </span>
                  <div class="flex items-center gap-2">
                    <span class="text-lg text-text-faint"
                      >{getItemIcon(item)}</span
                    >
                    <span
                      class="text-sm font-semibold text-text group-hover:text-primary transition-colors truncate max-w-xs"
                      >{getDisplayName(item)}</span
                    >
                  </div>
                </div>
              {:else if attr.key === "status"}
                <span
                  class="inline-flex items-center px-2.5 py-1 rounded-md text-xs font-medium {item
                    .attributes?.is_active
                    ? 'bg-success-soft text-success'
                    : 'bg-danger-soft text-danger'}"
                >
                  <span
                    class="w-1.5 h-1.5 rounded-full {item.attributes?.is_active
                      ? 'bg-success'
                      : 'bg-danger'} me-1.5"
                  ></span>
                  {item.attributes?.is_active
                    ? $_("admin_content.status.active")
                    : $_("admin_content.status.inactive")}
                </span>
              {:else if attr.key === "author"}
                <div class="flex items-center gap-2">
                  {#if item.attributes?.owner_shortname}
                    <div
                      class="w-6 h-6 rounded-full bg-surface-3 flex items-center justify-center text-[10px] font-medium text-text-muted"
                    >
                      {item.attributes?.owner_shortname.charAt(0).toUpperCase()}
                    </div>
                    <span class="text-sm font-medium text-text"
                      >{item.attributes?.owner_shortname}</span
                    >
                  {:else}
                    <div
                      class="w-6 h-6 rounded-full bg-surface-3 flex items-center justify-center text-[10px] font-medium text-text-faint"
                    >
                      ?
                    </div>
                    <span class="text-sm text-text-muted"
                      >{$_("common.unknown")}</span
                    >
                  {/if}
                </div>
              {:else}
                <span class="text-sm text-text-muted font-medium"
                  >{getAttributeValue(item, attr.key)}</span
                >
              {/if}
            {/snippet}

            {#snippet actions({ item })}
              {#if item.resource_type === "folder"}
                <button
                  onclick={(e) => {
                    e.stopPropagation();
                    handleItemClick(item);
                  }}
                  class="text-[12px] font-semibold text-primary hover:text-primary flex items-center gap-1.5"
                >
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                    ><path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z"
                    ></path></svg
                  > Open
                </button>
              {:else}
                <button
                  title={$_("actions.view")}
                  aria-label={$_("actions.view")}
                  onclick={(e) => {
                    e.stopPropagation();
                    handleItemClick(item);
                  }}
                  class="text-[12px] font-semibold text-primary hover:text-primary flex items-center"
                >
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                    ><path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
                    ></path><path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z"
                    ></path></svg
                  >
                </button>
              {/if}
              {#if $can("update", spaceName, $actualSubpath, item.resource_type)}
                <button
                  title={$_("admin_content.actions.duplicate")}
                  aria-label={$_("admin_content.actions.duplicate")}
                  onclick={(e) => {
                    e.stopPropagation();
                    handleDuplicateItem(item);
                  }}
                  disabled={duplicatingShortname === item.shortname}
                  class="text-[12px] font-semibold text-primary hover:text-primary flex items-center disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {#if duplicatingShortname === item.shortname}
                    <div class="spinner spinner-sm"></div>
                  {:else}
                    <svg
                      class="w-4 h-4"
                      fill="none"
                      stroke="currentColor"
                      viewBox="0 0 24 24"
                    >
                      <path
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        stroke-width="2"
                        d="M8 7v8a2 2 0 002 2h6M8 7V5a2 2 0 012-2h4.586a1 1 0 01.707.293l4.414 4.414a1 1 0 01.293.707V15a2 2 0 01-2 2h-2M8 7H6a2 2 0 00-2 2v10a2 2 0 002 2h8a2 2 0 002-2v-2"
                      ></path>
                    </svg>
                  {/if}
                </button>
                <button
                  title={$_("admin_content.bulk_actions.copy")}
                  aria-label={$_("admin_content.bulk_actions.copy")}
                  onclick={(e) => {
                    e.stopPropagation();
                    openCopyModal([item], "copy");
                  }}
                  class="text-[12px] font-semibold text-success hover:text-success flex items-center"
                >
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"
                    ></path>
                  </svg>
                </button>
                <button
                  title={$_("admin_content.bulk_actions.move")}
                  aria-label={$_("admin_content.bulk_actions.move")}
                  onclick={(e) => {
                    e.stopPropagation();
                    openCopyModal([item], "move");
                  }}
                  class="text-[12px] font-semibold text-warning hover:text-warning flex items-center"
                >
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M4 8l4-4m0 0l4 4m-4-4v12m8 0l-4 4m0 0l-4-4m4 4V8"
                    ></path>
                  </svg>
                </button>
              {/if}
              {#if $can("delete", spaceName, $actualSubpath, item.resource_type)}
                <button
                  title={$_("delete")}
                  aria-label={$_("delete")}
                  onclick={(e) => openDeleteDialog(item, e)}
                  class="text-[12px] font-semibold text-danger hover:text-danger flex items-center"
                >
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                    ><path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                    ></path></svg
                  >
                </button>
              {/if}
            {/snippet}

            {#snippet bulkActions()}
              <button
                onclick={clearSelection}
                class="bulk-btn bulk-btn-secondary"
                disabled={isBulkDeleting}
              >
                <svg
                  class="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M6 18L18 6M6 6l12 12"
                  />
                </svg>
                {$_("admin_content.bulk_actions.clear_selection")}
              </button>
              <button
                onclick={() => openBulkEditModal()}
                class="bulk-btn bulk-btn-primary"
                disabled={isBulkDeleting}
              >
                <svg
                  class="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
                  />
                </svg>
                {$_("admin_content.bulk_actions.edit")}
              </button>
              <button
                onclick={() =>
                  openCopyModal(
                    $allContents.filter((item) =>
                      selectedItems.has(item.shortname),
                    ),
                    "copy",
                  )}
                class="bulk-btn bulk-btn-secondary"
                disabled={isBulkDeleting}
              >
                <svg
                  class="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"
                  />
                </svg>
                {$_("admin_content.bulk_actions.copy") || "Copy"}
              </button>
              <button
                onclick={() =>
                  openCopyModal(
                    $allContents.filter((item) =>
                      selectedItems.has(item.shortname),
                    ),
                    "move",
                  )}
                class="bulk-btn bulk-btn-secondary"
                disabled={isBulkDeleting}
              >
                <svg
                  class="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M4 8l4-4m0 0l4 4m-4-4v12m8 0l-4 4m0 0l-4-4m4 4V8"
                  />
                </svg>
                {$_("admin_content.bulk_actions.move") || "Move"}
              </button>
              <button
                onclick={() => (showBulkTrashConfirm = true)}
                class="bulk-btn bulk-btn-warning"
                disabled={isBulkDeleting}
              >
                <svg
                  class="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                  />
                </svg>
                {$_("admin_content.bulk_actions.trash")}
              </button>
              <button
                onclick={() => (showBulkDeleteConfirm = true)}
                class="bulk-btn bulk-btn-danger"
                disabled={isBulkDeleting}
              >
                {#if isBulkDeleting}
                  <div
                    class="w-4 h-4 border-2 border-text-on-primary/30 border-t-white rounded-full animate-spin"
                  ></div>
                  {$_("admin_content.bulk_actions.deleting")}
                {:else}
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                    />
                  </svg>
                  {$_("admin_content.bulk_actions.delete")}
                {/if}
              </button>
            {/snippet}
          </DataTable>
        {/if}
      </div>
    {/if}
  </div>
</div>

<!-- Bulk Delete Confirmation Dialog -->
{#if showBulkDeleteConfirm}
  <div class="modal-overlay">
    <div class="modal-container">
      <div class="modal-header">
        <div class="modal-header-content">
          <h3 class="modal-title">
            {$_("admin_content.bulk_actions.confirm_delete_title")}
          </h3>
          <p class="modal-subtitle">
            {$_("admin_content.bulk_actions.confirm_delete_message", {
              values: { count: selectedItems.size },
            })}
          </p>
        </div>
        <button
          onclick={() => (showBulkDeleteConfirm = false)}
          class="modal-close-btn"
          aria-label={$_("admin_content.modal.close")}
        >
          <svg
            class="w-6 h-6"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M6 18L18 6M6 6l12 12"
            />
          </svg>
        </button>
      </div>

      <div class="modal-footer">
        <button
          onclick={() => (showBulkDeleteConfirm = false)}
          class="btn btn-secondary"
          disabled={isBulkDeleting}
        >
          {$_("common.cancel")}
        </button>
        <button
          onclick={async () => {
            showBulkDeleteConfirm = false;
            await handleBulkDelete();
          }}
          class="btn btn-danger"
          disabled={isBulkDeleting}
        >
          {#if isBulkDeleting}
            <div class="spinner"></div>
            {$_("admin_content.bulk_actions.deleting")}
          {:else}
            {$_("admin_content.bulk_actions.confirm_delete")}
          {/if}
        </button>
      </div>
    </div>
  </div>
{/if}

<!-- Bulk Trash Confirmation Dialog -->
{#if showBulkTrashConfirm}
  <div class="modal-overlay">
    <div class="modal-container">
      <div class="modal-header">
        <div class="modal-header-content">
          <h3 class="modal-title">
            {$_("admin_content.bulk_actions.confirm_trash_title")}
          </h3>
          <p class="modal-subtitle">
            {$_("admin_content.bulk_actions.confirm_trash_message", {
              values: { count: selectedItems.size },
            })}
          </p>
        </div>
        <button
          onclick={() => (showBulkTrashConfirm = false)}
          class="modal-close-btn"
          aria-label={$_("admin_content.modal.close")}
        >
          <svg
            class="w-6 h-6"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M6 18L18 6M6 6l12 12"
            />
          </svg>
        </button>
      </div>

      <div class="modal-footer">
        <button
          onclick={() => (showBulkTrashConfirm = false)}
          class="btn btn-secondary"
          disabled={isBulkDeleting}
        >
          {$_("common.cancel")}
        </button>
        <button
          onclick={async () => {
            showBulkTrashConfirm = false;
            await handleBulkTrash();
          }}
          class="btn btn-warning"
          disabled={isBulkDeleting}
        >
          {#if isBulkDeleting}
            <div class="spinner"></div>
            {$_("admin_content.bulk_actions.trashing")}
          {:else}
            {$_("admin_content.bulk_actions.confirm_trash")}
          {/if}
        </button>
      </div>
    </div>
  </div>
{/if}

<!-- Bulk Edit Modal -->
{#if showBulkEditModal}
  {@const effectiveColumns =
    indexAttributes &&
    indexAttributes.length > 0 &&
    indexAttributes.some((attr) => attr && Object.keys(attr).length > 0)
      ? indexAttributes
      : [
          { key: "shortname", name: "Shortname" },
          { key: "schema_shortname", name: "Schema" },
          { key: "status", name: "Status" },
          { key: "created_at", name: $_("data_table.columns.created_at") },
          { key: "updated_at", name: $_("data_table.columns.updated_at") },
        ]}
  <!-- Key only re-renders when items are added/removed, not on every edit -->
  {#key Object.keys(bulkEditData).length}
    <div class="modal-overlay bulk-edit-overlay">
      <div class="modal-container bulk-edit-container">
        <div class="modal-header">
          <div class="modal-header-content">
            <h3 class="modal-title">
              {$_("admin_content.bulk_actions.edit_title")}
            </h3>
          </div>
          <button
            onclick={closeBulkEditModal}
            class="modal-close-btn"
            aria-label={$_("admin_content.modal.close")}
          >
            <svg
              class="w-6 h-6"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M6 18L18 6M6 6l12 12"
              />
            </svg>
          </button>
        </div>

        <div class="modal-content bulk-edit-content">
          <div class="bulk-edit-table-wrapper">
            <table class="bulk-edit-table">
              <thead>
                <tr>
                  {#each effectiveColumns as attr (attr.key)}
                    <th class="bulk-edit-th">{columnLabel(attr)}</th>
                  {/each}
                </tr>
              </thead>
              <tbody>
                {#each Object.entries(bulkEditData) as [shortname, editData] (shortname)}
                  {@const item = $allContents.find(
                    (i) => i.shortname === shortname,
                  )}
                  <tr class="bulk-edit-row">
                    {#each effectiveColumns as attr (attr.key)}
                      <td class="bulk-edit-td">
                        {#if attr.key === "status"}
                          <!-- Status Toggle -->
                          <label class="status-toggle">
                            <input
                              type="checkbox"
                              checked={editData.is_active === true}
                              onchange={(e) =>
                                updateBulkEditField(
                                  shortname,
                                  "is_active",
                                  e.currentTarget.checked,
                                )}
                              class="sr-only"
                            />
                            <span
                              class="status-toggle-slider"
                              class:active={editData.is_active === true}
                            ></span>
                            <span class="status-toggle-label">
                              {editData.is_active === true
                                ? $_("admin_content.status.active")
                                : $_("admin_content.status.inactive")}
                            </span>
                          </label>
                        {:else if attr.key === "tags" || getFieldType(attr.key) === "array"}
                          <!-- Array Editor (tags, conditions, etc.) -->
                          {@const arrayKey = attr.key}
                          <div class="tags-editor">
                            <div class="tags-list compact">
                              {#each editList(editData, arrayKey) as tagItem, idx (idx)}
                                <span class="edit-tag">
                                  {tagItem}
                                  <button
                                    onclick={() => {
                                      const arr = [
                                        ...editList(editData, arrayKey),
                                      ];
                                      arr.splice(idx, 1);
                                      updateBulkEditField(
                                        shortname,
                                        arrayKey,
                                        arr,
                                      );
                                    }}
                                    class="edit-tag-remove"
                                    type="button"
                                    aria-label={$_("route_labels.search_remove_tag")}
                                  >
                                    <svg
                                      class="w-3 h-3"
                                      fill="none"
                                      stroke="currentColor"
                                      viewBox="0 0 24 24"
                                    >
                                      <path
                                        stroke-linecap="round"
                                        stroke-linejoin="round"
                                        stroke-width="2"
                                        d="M6 18L18 6M6 6l12 12"
                                      />
                                    </svg>
                                  </button>
                                </span>
                              {/each}
                            </div>
                            <div class="tag-input-wrapper">
                              <input
                                type="text"
                                placeholder={$_("admin_content.bulk_actions.placeholders.add_tag")}
                                onkeydown={(e) => {
                                  if (e.key === "Enter") {
                                    e.preventDefault();
                                    const val = e.currentTarget.value.trim();
                                    if (val) {
                                      const arr = [
                                        ...editList(editData, arrayKey),
                                      ];
                                      arr.push(val);
                                      updateBulkEditField(
                                        shortname,
                                        arrayKey,
                                        arr,
                                      );
                                      e.currentTarget.value = "";
                                    }
                                  }
                                }}
                                class="bulk-edit-input tag-input"
                              />
                            </div>
                          </div>
                        {:else if attr.key === "displayname" || attr.key === "description" || attr.key === "attributes.displayname" || attr.key === "attributes.description"}
                          <!-- Localized fields (displayname, description) -->
                          {@const fieldName = attr.key.startsWith("attributes.")
                            ? attr.key.slice(11)
                            : attr.key}
                          <div class="localized-inputs compact">
                            {#each ["en", "ar", "ku"] as lang (lang)}
                              <div class="localized-input-row">
                                <span class="locale-badge">{lang}</span>
                                <input
                                  type="text"
                                  value={editLocale(editData, fieldName, lang)}
                                  oninput={(e) =>
                                    updateBulkEditLocalizedField(
                                      shortname,
                                      fieldName,
                                      lang,
                                      e.currentTarget.value,
                                    )}
                                  placeholder={lang}
                                  class="bulk-edit-input"
                                />
                              </div>
                            {/each}
                          </div>
                        {:else if attr.key === "author" || attr.key === "owner_shortname"}
                          <!-- Author/Owner (editable) -->
                          <input
                            type="text"
                            value={editText(editData, "owner_shortname") ||
                              item?.attributes?.owner_shortname ||
                              ""}
                            oninput={(e) =>
                              updateBulkEditField(
                                shortname,
                                "owner_shortname",
                                e.currentTarget.value,
                              )}
                            class="bulk-edit-input"
                            placeholder={$_("admin_dashboard.columns.owner")}
                          />
                        {:else if attr.key === "created_at"}
                          <!-- Created At (editable date) -->
                          <input
                            type="datetime-local"
                            value={item?.attributes?.created_at
                              ? new Date(item.attributes.created_at)
                                  .toISOString()
                                  .slice(0, 16)
                              : ""}
                            oninput={(e) =>
                              updateBulkEditField(
                                shortname,
                                "created_at",
                                e.currentTarget.value,
                              )}
                            class="bulk-edit-input"
                          />
                        {:else if attr.key === "updated_at"}
                          <!-- Updated At (editable date) -->
                          <input
                            type="datetime-local"
                            value={item?.attributes?.updated_at
                              ? new Date(item.attributes.updated_at)
                                  .toISOString()
                                  .slice(0, 16)
                              : ""}
                            oninput={(e) =>
                              updateBulkEditField(
                                shortname,
                                "updated_at",
                                e.currentTarget.value,
                              )}
                            class="bulk-edit-input"
                          />
                        {:else if attr.key === "schema_shortname"}
                          <!-- Schema (editable) -->
                          <input
                            type="text"
                            value={editText(editData, "schema_shortname") ||
                              item?.attributes?.schema_shortname ||
                              ""}
                            oninput={(e) =>
                              updateBulkEditField(
                                shortname,
                                "schema_shortname",
                                e.currentTarget.value,
                              )}
                            class="bulk-edit-input"
                            placeholder={$_("templates.form.schema_label")}
                          />
                        {:else if getFieldType(attr.key) === "object" || getFieldType(attr.key) === "array-object"}
                          <!-- Object/Array-Object - JSON editor -->
                          {@const objType = getFieldType(attr.key)}
                          <textarea
                            value={JSON.stringify(
                              editData[attr.key] ||
                                (objType === "array-object" ? [] : {}),
                              null,
                              2,
                            )}
                            oninput={(e) => {
                              try {
                                const parsed = JSON.parse(
                                  e.currentTarget.value,
                                );
                                updateBulkEditField(
                                  shortname,
                                  attr.key,
                                  parsed,
                                );
                              } catch {
                                // Invalid JSON, store as string temporarily
                                updateBulkEditField(
                                  shortname,
                                  attr.key,
                                  e.currentTarget.value,
                                );
                              }
                            }}
                            class="bulk-edit-input"
                            placeholder={`{ "key": "value" }`}
                            rows="3"
                          ></textarea>
                        {:else}
                          <!-- Generic editable field - any dynamic attribute from index_attributes -->
                          {@const currentValue =
                            editData[attr.key] !== undefined
                              ? editData[attr.key]
                              : getAttributeValue(item, attr.key)}
                          <input
                            type="text"
                            value={currentValue}
                            oninput={(e) =>
                              updateBulkEditField(
                                shortname,
                                attr.key,
                                e.currentTarget.value,
                              )}
                            class="bulk-edit-input"
                            placeholder={columnLabel(attr)}
                          />
                        {/if}
                      </td>
                    {/each}
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
        </div>

        <div class="modal-footer">
          <button
            onclick={closeBulkEditModal}
            class="btn btn-secondary"
            disabled={isBulkSaving}
          >
            {$_("common.cancel")}
          </button>
          <button
            onclick={handleBulkSave}
            class="btn btn-primary"
            disabled={isBulkSaving}
          >
            {#if isBulkSaving}
              <div class="spinner"></div>
              {$_("admin_content.bulk_actions.saving")}
            {:else}
              <svg
                class="w-4 h-4"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M5 13l4 4L19 7"
                />
              </svg>
              {$_("admin_content.bulk_actions.save_changes")}
            {/if}
          </button>
        </div>
      </div>
    </div>
  {/key}
{/if}

{#if showCreateFolderModal}
  <div class="modal-overlay">
    <div class="modal-container">
      <div class="modal-header">
        <div class="modal-header-content">
          <h3 class="modal-title">{$_("admin_content.modal.create.title")}</h3>
          <p class="modal-subtitle">
            {$_("admin_content.modal.create.subtitle")}
          </p>
        </div>
        <button
          onclick={() => (showCreateFolderModal = false)}
          class="modal-close-btn"
          aria-label={$_("admin_content.modal.close")}
        >
          <svg
            class="w-6 h-6"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M6 18L18 6M6 6l12 12"
            ></path>
          </svg>
        </button>
      </div>

      <div class="modal-content">
        <div class="form-section">
          <div class="section-header">
            <h4 class="section-title">
              {$_("admin_content.modal.basic_info.title")}
            </h4>
            <p class="section-description">
              {$_("admin_content.modal.basic_info.description")}
            </p>
          </div>
          <MetaForm
            bind:formData={metaContent}
            bind:validateFn={validateMetaForm}
            isCreate={true}
            fullWidth={true}
          />
        </div>

        <div class="form-section">
          <div class="section-header">
            <h4 class="section-title">
              {$_("admin_content.modal.folder_config.title")}
            </h4>
            <p class="section-description">
              {$_("admin_content.modal.folder_config.description")}
            </p>
          </div>
          <FolderForm
            bind:content={folderContent}
            space_name={spaceName}
            fullWidth={true}
          />
        </div>
      </div>

      <div class="modal-footer">
        <button
          type="button"
          onclick={() => (showCreateFolderModal = false)}
          class="btn btn-secondary"
          disabled={isCreatingFolder}
        >
          {$_("admin_content.modal.cancel")}
        </button>
        <button
          onclick={handleSaveFolder}
          class="btn btn-primary"
          disabled={isCreatingFolder}
        >
          {#if isCreatingFolder}
            <div class="spinner"></div>
            {$_("admin_content.modal.creating")}
          {:else}
            {$_("admin_content.modal.create_folder")}
          {/if}
        </button>
      </div>
    </div>
  </div>
{/if}

{#if showCreateSchemaModal}
  <div class="modal-overlay">
    <div class="modal-container">
      <div class="modal-header">
        <div class="modal-header-content">
          <h3 class="modal-title">{$_("admin_content.modal.create.title")}</h3>
          <p class="modal-subtitle">
            {$_("admin_content.modal.create.subtitle")}
          </p>
        </div>
        <button
          onclick={() => (showCreateSchemaModal = false)}
          class="modal-close-btn"
          aria-label={$_("admin_content.modal.close")}
        >
          <svg
            class="w-6 h-6"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M6 18L18 6M6 6l12 12"
            ></path>
          </svg>
        </button>
      </div>

      <form
        onsubmit={(event) => {
          event.preventDefault();
          handleSaveschema(event);
        }}
      >
        <div class="modal-content">
          <div class="form-section">
            <div class="section-header">
              <h4 class="section-title">
                {$_("admin_content.modal.basic_info.title")}
              </h4>
              <p class="section-description">
                {$_("admin_content.modal.basic_info.description")}
              </p>
            </div>
            <MetaForm
              bind:formData={metaContent}
              bind:validateFn={validateMetaForm}
              isCreate={true}
              fullWidth={true}
            />
          </div>

          <div class="form-section">
            <div class="section-header">
              <h4 class="section-title">{$_("admin_content.schema_definition")}</h4>
              <p class="section-description">
                Define the JSON schema structure for this resource.
              </p>
            </div>
            <SchemaForm bind:content={schemaContent} />
          </div>
        </div>

        <div class="modal-footer">
          <button
            type="button"
            onclick={() => (showCreateSchemaModal = false)}
            class="btn btn-secondary"
            disabled={isCreatingSchema}
          >
            {$_("admin_content.modal.cancel")}
          </button>
          <button
            type="submit"
            class="btn btn-primary"
            disabled={isCreatingSchema}
          >
            {#if isCreatingSchema}
              <div class="spinner"></div>
              {$_("admin_content.modal.creating")}
            {:else}
              {$_("admin_content.actions.create_schema")}
            {/if}
          </button>
        </div>
      </form>
    </div>
  </div>
{/if}

{#if showCreateWorkflowModal}
  <div class="modal-overlay">
    <div class="modal-container">
      <div class="modal-header">
        <div class="modal-header-content">
          <h3 class="modal-title">{$_("admin_content.modal.create.title")}</h3>
          <p class="modal-subtitle">
            {$_("admin_content.modal.create.subtitle")}
          </p>
        </div>
        <button
          onclick={() => (showCreateWorkflowModal = false)}
          class="modal-close-btn"
          aria-label={$_("admin_content.modal.close")}
        >
          <svg
            class="w-6 h-6"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M6 18L18 6M6 6l12 12"
            ></path>
          </svg>
        </button>
      </div>

      <form
        onsubmit={(event) => {
          event.preventDefault();
          handleSaveWorkflow(event);
        }}
      >
        <div class="modal-content">
          <div class="form-section">
            <div class="section-header">
              <h4 class="section-title">
                {$_("admin_content.modal.basic_info.title")}
              </h4>
              <p class="section-description">
                {$_("admin_content.modal.basic_info.description")}
              </p>
            </div>
            <MetaForm
              bind:formData={metaContent}
              bind:validateFn={validateMetaForm}
              isCreate={true}
              fullWidth={true}
            />
          </div>

          <div class="form-section">
            <div class="section-header">
              <h4 class="section-title">{$_("admin_content.workflow_definition")}</h4>
              <p class="section-description">
                Define the workflow states and transitions.
              </p>
            </div>
            <WorkflowForm bind:content={workflowContent} />
          </div>
        </div>

        <div class="modal-footer">
          <button
            type="button"
            onclick={() => (showCreateWorkflowModal = false)}
            class="btn btn-secondary"
            disabled={isCreatingWorkflow}
          >
            {$_("admin_content.modal.cancel")}
          </button>
          <button
            type="submit"
            class="btn btn-primary"
            disabled={isCreatingWorkflow}
          >
            {#if isCreatingWorkflow}
              <div class="spinner"></div>
              {$_("admin_content.modal.creating")}
            {:else}
              {$_("admin_content.actions.create_workflow")}
            {/if}
          </button>
        </div>
      </form>
    </div>
  </div>
{/if}

{#if showCreateItemModal}
  <div class="modal-overlay">
    <div class="modal-container">
      <div class="modal-header">
        <div class="modal-header-content">
          <h3 class="modal-title">
            {$_("admin_content.actions.create_new_item")}
          </h3>
          <p class="modal-subtitle">
            {$_("admin_content.modal.create.subtitle")}
          </p>
        </div>
        <button
          onclick={() => (showCreateItemModal = false)}
          class="modal-close-btn"
          aria-label={$_("admin_content.modal.close")}
        >
          <svg
            class="w-6 h-6"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M6 18L18 6M6 6l12 12"
            ></path>
          </svg>
        </button>
      </div>

      <form onsubmit={handleSaveItem}>
        <div class="modal-content">
          <div class="form-section">
            <div class="section-header">
              <h4 class="section-title">
                {$_("admin_content.modal.basic_info.title")}
              </h4>
              <p class="section-description">
                {$_("admin_content.modal.basic_info.description")}
              </p>
            </div>
            <MetaForm
              bind:formData={createItemMeta}
              bind:validateFn={validateCreateItemForm}
              isCreate={true}
              fullWidth={true}
            />
          </div>

          {#if createItemResourceType === ResourceType.user}
            <div class="form-section">
              <MetaUserForm
                bind:formData={createItemMeta}
                bind:validateFn={validateCreateRTForm}
                isCreate={true}
                fullWidth={true}
              />
            </div>
          {:else if createItemResourceType === ResourceType.role}
            <div class="form-section">
              <MetaRoleForm
                bind:formData={createItemMeta}
                bind:validateFn={validateCreateRTForm}
                fullWidth={true}
              />
            </div>
          {:else if createItemResourceType === ResourceType.permission}
            <div class="form-section">
              <MetaPermissionForm
                bind:formData={createItemMeta}
                bind:validateFn={validateCreateRTForm}
              />
            </div>
          {:else if createItemSchemaShortnames.length > 0}
            <div class="form-section">
              <div class="section-header">
                <h4 class="section-title">
                  {createItemSchemaShortnames.length > 1
                    ? $_("create_entry.schema.selection_title")
                    : $_("create_entry.schema.entry_data_title")}
                </h4>
              </div>

              {#if createItemSchemaShortnames.length > 1}
                <label
                  for="create-item-schema-select"
                  class="block text-sm font-semibold text-text mb-2"
                >
                  {$_("create_entry.schema.select_label")}
                </label>
                <select
                  id="create-item-schema-select"
                  class="w-full px-4 py-3 border-2 border-border rounded-lg text-sm bg-surface-2 text-text focus:outline-none focus:border-info mb-2"
                  value={selectedCreateSchemaShortname}
                  onchange={handleCreateSchemaChange}
                >
                  <option value=""
                    >{$_("create_entry.schema.choose_option")}</option
                  >
                  {#each createItemSchemaShortnames as schemaShortname (schemaShortname)}
                    <option value={schemaShortname}>{schemaShortname}</option>
                  {/each}
                </select>
              {/if}

              {#if loadingCreateSchema}
                <p class="text-sm text-text-muted py-2">
                  {$_("create_entry.schema.loading")}
                </p>
              {:else if createSchema}
                <DynamicSchemaBasedForms
                  bind:content={createSchemaFormData}
                  schema={createSchema}
                  space={spaceName}
                  subpath={$actualSubpath}
                  resourceType={ResourceType.content}
                />
              {/if}
            </div>
          {/if}
        </div>

        <div class="modal-footer">
          <button
            type="button"
            onclick={() => (showCreateItemModal = false)}
            class="btn btn-secondary"
            disabled={isCreatingItem}
          >
            {$_("admin_content.modal.cancel")}
          </button>
          <button
            type="submit"
            class="btn btn-primary"
            disabled={isCreatingItem}
          >
            {#if isCreatingItem}
              <div class="spinner"></div>
              {$_("admin_content.modal.creating")}
            {:else}
              {$_("admin_content.actions.create_new_item")}
            {/if}
          </button>
        </div>
      </form>
    </div>
  </div>
{/if}

<!-- Column Settings Modal -->
{#if showColumnSettingsModal}
  <div
    class="fixed inset-0 z-[60] flex items-center justify-center p-4 bg-[var(--surface-overlay)] backdrop-blur-sm"
  >
    <div
      class="bg-surface-2 rounded-3xl shadow-2xl w-full max-w-lg overflow-hidden border border-border modal-container"
    >
      <div
        class="p-6 border-b border-border flex items-center justify-between bg-surface-2 modal-header"
      >
        <div class="flex items-center gap-3">
          <div
            class="w-10 h-10 bg-primary-soft rounded-xl flex items-center justify-center text-primary"
          >
            <svg
              class="w-6 h-6"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z"
              ></path>
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
              ></path>
            </svg>
          </div>
          <h2 class="text-xl font-bold text-text">
            {$_("admin_content.settings_modal.title")}
          </h2>
        </div>
        <button
          onclick={() => (showColumnSettingsModal = false)}
          aria-label={$_("common.close")}
          class="p-2 text-text-faint hover:text-text-muted hover:bg-surface rounded-lg transition-colors modal-close-btn"
        >
          <svg
            class="w-6 h-6"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M6 18L18 6M6 6l12 12"
            ></path>
          </svg>
        </button>
      </div>

      <div class="p-6 max-h-[60vh] overflow-y-auto bg-surface/30 modal-content">
        <!-- Meta Info -->
        <div class="mb-6">
          <h3 class="text-sm font-semibold text-text mb-3 px-1">
            {$_("admin_content.settings_modal.meta_info")}
          </h3>
          <div
            class="p-4 bg-surface-2 rounded-2xl border border-border shadow-sm space-y-4"
          >
            <div class="space-y-2">
              <p class="text-xs font-semibold text-text-muted px-1 m-0">{$_("fields.displayname")}</p>
              <div class="grid grid-cols-3 gap-2">
                <div class="space-y-1">
                  <label
                    for="settings-displayname-en"
                    class="text-[10px] font-medium text-text-muted px-1"
                    >{$_("languages.english")}</label
                  >
                  <input
                    id="settings-displayname-en"
                    type="text"
                    bind:value={editingMeta.displayname.en}
                    class="w-full px-4 py-2 bg-surface border-none rounded-xl text-sm focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div class="space-y-1">
                  <label
                    for="settings-displayname-ar"
                    class="text-[10px] font-medium text-text-muted px-1"
                    >{$_("languages.arabic")}</label
                  >
                  <input
                    id="settings-displayname-ar"
                    type="text"
                    bind:value={editingMeta.displayname.ar}
                    dir="rtl"
                    class="w-full px-4 py-2 bg-surface border-none rounded-xl text-sm focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div class="space-y-1">
                  <label
                    for="settings-displayname-ku"
                    class="text-[10px] font-medium text-text-muted px-1"
                    >{$_("languages.kurdish")}</label
                  >
                  <input
                    id="settings-displayname-ku"
                    type="text"
                    bind:value={editingMeta.displayname.ku}
                    dir="rtl"
                    class="w-full px-4 py-2 bg-surface border-none rounded-xl text-sm focus:ring-2 focus:ring-primary"
                  />
                </div>
              </div>
            </div>
            <div class="space-y-2">
              <p class="text-xs font-semibold text-text-muted px-1 m-0">{$_("fields.description")}</p>
              <div class="grid grid-cols-3 gap-2">
                <div class="space-y-1">
                  <label
                    for="settings-description-en"
                    class="text-[10px] font-medium text-text-muted px-1"
                    >{$_("languages.english")}</label
                  >
                  <textarea
                    id="settings-description-en"
                    bind:value={editingMeta.description.en}
                    rows="3"
                    class="w-full px-4 py-2 bg-surface border-none rounded-xl text-sm focus:ring-2 focus:ring-primary resize-none"
                  ></textarea>
                </div>
                <div class="space-y-1">
                  <label
                    for="settings-description-ar"
                    class="text-[10px] font-medium text-text-muted px-1"
                    >{$_("languages.arabic")}</label
                  >
                  <textarea
                    id="settings-description-ar"
                    bind:value={editingMeta.description.ar}
                    rows="3"
                    dir="rtl"
                    class="w-full px-4 py-2 bg-surface border-none rounded-xl text-sm focus:ring-2 focus:ring-primary resize-none"
                  ></textarea>
                </div>
                <div class="space-y-1">
                  <label
                    for="settings-description-ku"
                    class="text-[10px] font-medium text-text-muted px-1"
                    >{$_("languages.kurdish")}</label
                  >
                  <textarea
                    id="settings-description-ku"
                    bind:value={editingMeta.description.ku}
                    rows="3"
                    dir="rtl"
                    class="w-full px-4 py-2 bg-surface border-none rounded-xl text-sm focus:ring-2 focus:ring-primary resize-none"
                  ></textarea>
                </div>
              </div>
            </div>
            <label
              for="settings-is-active"
              class="flex items-center gap-3 cursor-pointer select-none px-1"
            >
              <input
                id="settings-is-active"
                type="checkbox"
                bind:checked={editingMeta.is_active}
                class="w-4 h-4 rounded border-border-strong text-primary focus:ring-primary"
              />
              <span class="text-sm font-medium text-text"
                >{$_("fields.active")}</span
              >
            </label>
          </div>
        </div>

        <!-- Column Settings -->
        <h3 class="text-sm font-semibold text-text mb-3 px-1">
          {$_("admin_content.settings_modal.column_settings")}
        </h3>
        <div class="space-y-4">
          {#each editingIndexAttributes as attr, i (i)}
            <div
              class="flex items-center gap-3 p-4 bg-surface-2 rounded-2xl border border-border shadow-sm"
            >
              <div class="flex-1 grid grid-cols-2 gap-4">
                <div class="space-y-1.5">
                  <label
                    for="col-name-{i}"
                    class="text-xs font-semibold text-text-muted px-1"
                    >{$_("users_page.column_label")}</label
                  >
                  <input
                    id="col-name-{i}"
                    type="text"
                    bind:value={attr.name}
                    placeholder={$_("users_page.column_label_placeholder")}
                    class="w-full px-4 py-2 bg-surface border-none rounded-xl text-sm focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div class="space-y-1.5">
                  <label
                    for="col-key-{i}"
                    class="text-xs font-semibold text-text-muted px-1"
                    >{$_("users_page.column_key")}</label
                  >
                  <input
                    id="col-key-{i}"
                    type="text"
                    bind:value={attr.key}
                    placeholder={$_("users_page.column_key_placeholder")}
                    class="w-full px-4 py-2 bg-surface border-none rounded-xl text-sm focus:ring-2 focus:ring-primary font-mono"
                  />
                </div>
              </div>
              <button
                onclick={() => removeColumnSetting(i)}
                class="mt-6 p-2 text-danger hover:text-danger hover:bg-danger-soft rounded-lg transition-colors"
                title={$_("users_page.remove_column")}
                aria-label={$_("users_page.remove_column")}
              >
                <svg
                  class="w-5 h-5"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                  ></path>
                </svg>
              </button>
            </div>
          {/each}
        </div>

        <button
          onclick={addColumnSetting}
          class="w-full mt-6 py-3 border-2 border-dashed border-border rounded-2xl text-sm font-medium text-text-muted hover:border-primary hover:text-primary hover:bg-primary-soft transition-all flex items-center justify-center gap-2"
        >
          <svg
            class="w-5 h-5"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M12 4v16m8-8H4"
            ></path>
          </svg>
          Add New Column
        </button>
      </div>

      <div
        class="p-6 border-t border-border flex items-center justify-end gap-3 bg-surface-2 modal-footer"
      >
        <button
          onclick={() => (showColumnSettingsModal = false)}
          class="px-6 py-2.5 text-sm font-medium text-text-muted hover:text-text transition-colors"
        >
          {$_("common.cancel")}
        </button>
        <button
          onclick={handleUpdateColumns}
          disabled={isSavingColumns}
          class="px-8 py-2.5 bg-primary text-text-on-primary rounded-xl text-sm font-semibold hover:bg-primary-hover shadow-md shadow-card disabled:opacity-50 disabled:cursor-not-allowed transition-all flex items-center gap-2"
        >
          {#if isSavingColumns}
            <div
              class="w-4 h-4 border-2 border-text-on-primary/30 border-t-white rounded-full animate-spin"
            ></div>
            {$_("common.saving") || "Saving..."}
          {:else}
            {$_("common.save_changes") || "Save Changes"}
          {/if}
        </button>
      </div>
    </div>
  </div>
{/if}

<ConfirmDialog
  bind:open={showDeleteDialog}
  title={$_("delete_confirmation.title", { values: { type: itemToDelete?.resource_type || $_("delete_confirmation.item_label") } })}
  body={itemToDelete ? `${getDisplayName(itemToDelete)}\n${$_("delete_confirmation.warning")}` : ""}
  variant="danger"
  action={performDelete}
  onConfirm={afterDelete}
  onCancel={closeDeleteDialog}
>
  <label class="flex items-start gap-2 text-sm text-text cursor-pointer">
    <input type="checkbox" class="mt-0.5 accent-primary" bind:checked={forceDelete} />
    <span>
      {$_("force_delete")}
      <span class="block text-xs text-text-muted">{$_("force_delete_help")}</span>
    </span>
  </label>
</ConfirmDialog>

<!-- CSV Import/Export Modals -->
<ModalCSVUpload
  space_name={spaceName}
  subpath={$actualSubpath || "/"}
  bind:isOpen={isCSVUploadModalOpen}
  onUploadSuccess={() => loadContents(true)}
/>

<ModalCSVDownload
  space_name={spaceName}
  subpath={$actualSubpath || "/"}
  bind:isOpen={isCSVDownloadModalOpen}
  {folderMetadata}
  {indexAttributes}
  onUpdateFolder={() => loadContents(true)}
/>

{#if showCopyModal}
  <ModalCopy
    bind:open={showCopyModal}
    records={copyRecords}
    action={copyAction}
    sourceSpace={spaceName}
    defaultSubpath={`/${$actualSubpath || ""}`.replace(/\/+$/, "") || "/"}
    onClose={closeCopyModal}
    onDone={handleCopyOrMoveDone}
  />
{/if}

<style>


  .section-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--color-text);
  }



  .search-input:focus {
    outline: none;
    border-color: var(--color-primary-600);
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.1);
  }


  .clear-search-button:hover {
    color: var(--color-text-muted);
    background-color: rgba(107, 114, 128, 0.1);
  }





  .sort-select {
    flex: 1;
  }

  /* Modal Styles */
  .modal-overlay {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.6);
    backdrop-filter: blur(4px);
    z-index: 50;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 1rem;
    animation: fadeIn var(--duration-normal) var(--ease-out);
  }

  .modal-container {
    overflow: scroll;
    background: var(--color-surface-2);
    border-radius: var(--radius-card);
    box-shadow: var(--shadow-modal);
    width: 100%;
    max-width: 80rem;
    max-height: 95vh;
    display: flex;
    flex-direction: column;
    animation: scaleIn var(--duration-slow) var(--ease-out);
    border: 1px solid var(--color-border);
  }

  .modal-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 1.5rem 2rem;
    border-bottom: 1px solid var(--color-surface-3);
    background: linear-gradient(
      135deg,
      var(--color-surface) 0%,
      var(--color-surface-2) 100%
    );
    border-radius: var(--radius-card) var(--radius-card) 0 0;
    flex-shrink: 0;
  }


  .modal-header-content {
    flex: 1;
  }

  .modal-title {
    font-size: 1.5rem;
    font-weight: 600;
    color: var(--color-text);
    margin: 0 0 0.25rem 0;
  }

  .modal-subtitle {
    font-size: 0.875rem;
    color: var(--color-text-muted);
    margin: 0;
  }

  .modal-close-btn {
    background: none;
    border: none;
    color: var(--color-text-muted);
    cursor: pointer;
    padding: 0.5rem;
    border-radius: var(--radius-control);
    transition: all var(--duration-normal) var(--ease-out);
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .modal-close-btn:hover {
    background: var(--color-surface-3);
    color: var(--color-text);
    transform: scale(1.05);
  }

  .modal-content {
    flex: 1;
    overflow-y: auto;
    padding: 2rem;
    display: flex;
    flex-direction: column;
    gap: 2rem;
    min-height: 0;
  }

  .form-section {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .section-header {
    border-bottom: 1px solid var(--color-border);
    padding-bottom: 0.75rem;
  }

  .section-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--color-text);
    margin: 0 0 0.25rem 0;
  }

  .section-description {
    font-size: 0.875rem;
    color: var(--color-text-muted);
    margin: 0;
  }

  .modal-footer {
    display: flex;
    justify-content: flex-end;
    gap: 0.75rem;
    padding: 1.5rem 2rem;
    border-top: 1px solid var(--color-surface-3);
    background: var(--color-surface);
    border-radius: 0 0 var(--radius-card) var(--radius-card);
    flex-shrink: 0;
  }

  .btn {
    padding: 0.75rem 1.5rem;
    font-size: 0.875rem;
    font-weight: 600;
    border-radius: var(--radius-card);
    border: none;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-width: 120px;
    justify-content: center;
  }

  .btn:disabled {
    cursor: not-allowed;
    opacity: 0.6;
  }

  .btn-secondary {
    background: var(--color-surface);
    color: var(--color-text-muted);
    border: 2px solid var(--color-border);
  }

  .btn-secondary:hover:not(:disabled) {
    background: var(--color-surface-3);
    border-color: var(--color-border-strong);
    transform: translateY(-1px);
  }

  .btn-primary {
    background: var(--color-primary);
    color: white;
    box-shadow: var(--shadow-card);
  }

  .btn-primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
    transform: translateY(-2px);
    box-shadow: var(--shadow-modal);
  }

  @media (min-width: 640px) {
    .search-filter-controls {
      flex-direction: column;
      gap: 1.5rem;
    }

    .search-input-group {
      flex: 2;
    }

    .filter-controls {
      flex: 1;
      justify-content: flex-start;
    }


    .results-summary {
      flex-direction: row;
    }
  }
  @media (max-width: 1024px) {
    .modal-container {
      max-width: 95vw;
      margin: 0.5rem;
    }

    .modal-header {
      padding: 1rem 1.5rem;
    }

    .modal-content {
      padding: 1.5rem;
    }

    .modal-footer {
      padding: 1rem 1.5rem;
    }
  }

  @media (max-width: 768px) {
    .container {
      padding-inline-start: 1rem;
      padding-inline-end: 1rem;
    }

    .search-filter-controls {
      gap: 1rem;
    }

    .filter-controls {
      flex-direction: column;
      align-items: stretch;
    }


    .filter-group {
      min-width: auto;
    }

    .results-summary {
      flex-direction: column;
      gap: 0.5rem;
      align-items: flex-start;
    }


    .admin-content-card {
      padding: 1rem;
      gap: 0.75rem;
    }

    .card-header {
      flex-direction: column;
      align-items: flex-start;
      gap: 0.5rem;
    }


    .card-actions {
      align-items: stretch;
    }

    .action-buttons {
      justify-content: center;
    }

    .load-more-button {
      padding: 0.75rem 1.5rem;
      font-size: 0.875rem;
    }

    .modal-container {
      max-width: 95vw;
      margin: 0.5rem;
    }

    .modal-header {
      padding: 1rem 1.5rem;
    }

    .modal-content {
      padding: 1.5rem;
    }

    .modal-footer {
      padding: 1rem 1.5rem;
    }
  }

  @media (max-width: 640px) {
    .modal-container {
      max-width: 100vw;
      max-height: 100vh;
      margin: 0;
      border-radius: 0;
    }

    .modal-header {
      padding: 1rem;
      flex-direction: column;
      align-items: flex-start;
      gap: 1rem;
    }


    .modal-header-content {
      flex: none;
      width: 100%;
    }

    .modal-close-btn {
      position: absolute;
      top: 1rem;
      inset-inline-end: 1rem;
    }


    .modal-content {
      padding: 1rem;
    }

    .modal-footer {
      padding: 1rem;
      flex-direction: column-reverse;
    }


    .btn {
      width: 100%;
    }
  }

  .modal-content::-webkit-scrollbar {
    width: 8px;
  }

  .modal-content::-webkit-scrollbar-track {
    background: var(--color-surface-3);
    border-radius: 4px;
  }

  .modal-content::-webkit-scrollbar-thumb {
    background: var(--color-border-strong);
    border-radius: 4px;
  }

  .modal-content::-webkit-scrollbar-thumb:hover {
    background: var(--color-text-faint);
  }

  /* Bulk Actions Bar */
  .bulk-actions-bar {
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-card);
    padding: 0.875rem 1.25rem;
    margin: 1rem 0;
    box-shadow: var(--shadow-card);
  }

  .bulk-actions-bar.rtl {
    direction: rtl;
  }

  .bulk-actions-content {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    flex-wrap: wrap;
  }

  .bulk-actions-info {
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .bulk-actions-count {
    color: var(--color-text);
    font-weight: 600;
    font-size: 0.9375rem;
  }

  .bulk-actions-buttons {
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .bulk-btn {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 1rem;
    border-radius: var(--radius-control);
    font-size: 0.875rem;
    font-weight: 500;
    transition: all var(--duration-normal) var(--ease-out);
    cursor: pointer;
    border: none;
  }

  .bulk-btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }

  .bulk-btn-secondary {
    background-color: var(--color-surface-3);
    color: var(--color-text);
  }

  .bulk-btn-secondary:hover:not(:disabled) {
    background-color: var(--color-border);
  }

  .bulk-btn-warning {
    background: linear-gradient(135deg, var(--color-warning) 0%, var(--color-warning) 100%);
    color: white;
    box-shadow: 0 2px 4px rgba(217, 119, 6, 0.2);
  }

  .bulk-btn-warning:hover:not(:disabled) {
    background: linear-gradient(135deg, var(--color-warning) 0%, var(--color-warning) 100%);
    transform: translateY(-1px);
    box-shadow: 0 4px 8px rgba(217, 119, 6, 0.3);
  }

  .bulk-btn-danger {
    background: linear-gradient(135deg, var(--color-danger) 0%, var(--color-danger) 100%);
    color: white;
    box-shadow: 0 2px 4px rgba(220, 38, 38, 0.2);
  }

  .bulk-btn-danger:hover:not(:disabled) {
    background: linear-gradient(135deg, var(--color-danger) 0%, var(--color-danger) 100%);
    transform: translateY(-1px);
    box-shadow: 0 4px 8px rgba(220, 38, 38, 0.3);
  }

  /* Button styles for modal */
  .btn-warning {
    background: linear-gradient(135deg, var(--color-warning) 0%, var(--color-warning) 100%);
    color: white;
    box-shadow: 0 2px 4px rgba(217, 119, 6, 0.2);
  }

  .btn-warning:hover:not(:disabled) {
    background: linear-gradient(135deg, var(--color-warning) 0%, var(--color-warning) 100%);
    transform: translateY(-1px);
    box-shadow: 0 4px 8px rgba(217, 119, 6, 0.3);
  }

  @media (max-width: 640px) {
    .bulk-actions-content {
      flex-direction: column;
      align-items: stretch;
    }

    .bulk-actions-buttons {
      justify-content: stretch;
    }

    .bulk-btn {
      flex: 1;
      justify-content: center;
    }
  }

  /* --- Tag Filters --- */
  .tags-section {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  .tags-label {
    display: flex;
    align-items: center;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    font-weight: 600;
    color: var(--color-text-muted);
  }

  .tag-pills {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .tag-pill {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.375rem 0.75rem;
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-full);
    font-size: 0.875rem;
    color: var(--color-text-muted);
    font-weight: 500;
    cursor: pointer;
    transition: all var(--duration-fast) var(--ease-out);
    box-shadow: var(--shadow-card);
  }

  .tag-pill:hover {
    background: var(--color-surface);
    border-color: var(--color-border-strong);
  }

  .tag-pill-active {
    background: var(--color-primary-50);
    border-color: var(--color-primary-200);
    color: var(--color-primary-700);
  }

  .tag-pill-active:hover {
    background: var(--color-primary-100);
    border-color: var(--color-primary-300);
  }

  .tag-pill-more {
    color: var(--color-primary-600);
    background: var(--color-primary-50);
    border-color: var(--color-primary-200);
  }

  .tag-pill-more:hover {
    background: var(--color-primary-100);
    border-color: var(--color-primary-300);
  }

  .bullet {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--color-border-strong);
  }

  .bullet-active {
    background: var(--color-primary-600);
  }

  .tag-count {
    color: var(--color-text-faint);
    margin-inline-start: 0.25rem;
    font-size: 0.75rem;
  }

  .tag-pill-active .tag-count {
    color: var(--color-primary-500);
  }




  /* Bulk Edit Modal Styles */
  .bulk-edit-overlay {
    z-index: 60;
  }

  .bulk-edit-container {
    max-width: 90vw;
    width: 100%;
    max-height: 90vh;
  }

  .bulk-edit-content {
    padding: 0;
    overflow: hidden;
  }

  .bulk-edit-table-wrapper {
    overflow-x: auto;
    overflow-y: auto;
    max-height: 60vh;
  }

  .bulk-edit-table {
    width: 100%;
    border-collapse: separate;
    border-spacing: 0;
    font-size: 0.875rem;
  }

  .bulk-edit-th {
    position: sticky;
    top: 0;
    background: var(--color-surface);
    padding: 0.875rem 1rem;
    text-align: start;
    font-weight: 600;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--color-text-muted);
    border-bottom: 1px solid var(--color-border);
    white-space: nowrap;
    z-index: 10;
  }


  .bulk-edit-row {
    border-bottom: 1px solid var(--color-surface-3);
    transition: background-color var(--duration-fast);
  }

  .bulk-edit-row:hover {
    background-color: var(--color-primary-50);
  }

  .bulk-edit-row:last-child {
    border-bottom: none;
  }

  .bulk-edit-td {
    padding: 1rem;
    vertical-align: top;
    border-bottom: 1px solid var(--color-surface-3);
  }

  .localized-inputs {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    min-width: 200px;
  }

  .localized-inputs.compact {
    gap: 0.25rem;
    min-width: 150px;
  }

  .localized-input-row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .locale-badge {
    flex-shrink: 0;
    width: 1.5rem;
    height: 1.5rem;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--color-border);
    color: var(--color-text-muted);
    font-size: 0.625rem;
    font-weight: 700;
    text-transform: uppercase;
    border-radius: 0.25rem;
  }

  .compact .locale-badge {
    width: 1.25rem;
    height: 1.25rem;
    font-size: 0.5625rem;
  }

  .bulk-edit-input {
    flex: 1;
    padding: 0.5rem 0.75rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-control);
    font-size: 0.875rem;
    color: var(--color-text);
    background: var(--color-surface-2);
    transition: all var(--duration-normal) var(--ease-out);
    min-width: 0;
  }

  .bulk-edit-input:focus {
    outline: none;
    border-color: var(--color-primary-500);
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.1);
  }

  .bulk-edit-input::placeholder {
    color: var(--color-text-faint);
  }

  .bulk-edit-input[type="datetime-local"] {
    padding: 0.375rem 0.5rem;
    font-size: 0.8125rem;
  }

  textarea.bulk-edit-input {
    resize: vertical;
    min-height: 60px;
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas,
      monospace;
    font-size: 0.75rem;
    line-height: 1.4;
  }

  .compact .bulk-edit-input {
    padding: 0.375rem 0.5rem;
    font-size: 0.8125rem;
  }

  /* Status Toggle */
  .status-toggle {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    cursor: pointer;
  }

  .status-toggle-slider {
    position: relative;
    width: 2.75rem;
    height: 1.5rem;
    background: var(--color-border);
    border-radius: var(--radius-full);
    transition: background-color var(--duration-normal);
    flex-shrink: 0;
  }

  .status-toggle-slider::after {
    content: "";
    position: absolute;
    top: 0.125rem;
    inset-inline-start: 0.125rem;
    width: 1.25rem;
    height: 1.25rem;
    background: var(--color-surface);
    border-radius: 50%;
    transition: transform 0.2s;
    box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
  }

  .status-toggle-slider.active {
    background: var(--color-success);
  }

  .status-toggle-slider.active::after {
    transform: translateX(1.25rem);
  }

  .status-toggle-label {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
  }

  /* Tags Editor */
  .tags-editor {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    min-width: 180px;
  }

  .tags-list {
    display: flex;
    flex-wrap: wrap;
    gap: 0.375rem;
  }

  .tags-list.compact {
    gap: 0.25rem;
    margin-bottom: 0.25rem;
  }

  .edit-tag {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0.25rem 0.5rem;
    background: var(--color-primary-50);
    color: var(--color-primary-700);
    font-size: 0.75rem;
    font-weight: 500;
    border-radius: var(--radius-control);
  }

  .edit-tag-remove {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0.125rem;
    margin-inline-start: 0.25rem;
    color: var(--color-primary-500);
    background: none;
    border: none;
    cursor: pointer;
    border-radius: 0.125rem;
    transition: color var(--duration-fast);
  }

  .edit-tag-remove:hover {
    color: var(--color-danger);
  }

  .tag-input-wrapper {
    display: flex;
  }

  .tag-input {
    width: 100%;
  }

  /* Bulk Edit Button */
  .bulk-btn-primary {
    background: var(--color-primary);
    color: white;
    box-shadow: var(--shadow-card);
  }

  .bulk-btn-primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
    transform: translateY(-1px);
    box-shadow: var(--shadow-modal);
  }

  /* Responsive Bulk Edit */
  @media (max-width: 1024px) {
    .bulk-edit-container {
      max-width: 95vw;
      max-height: 95vh;
    }

    .bulk-edit-table-wrapper {
      max-height: 50vh;
    }

    .localized-inputs {
      min-width: 150px;
    }

    .localized-inputs.compact {
      min-width: 120px;
    }
  }

  @media (max-width: 768px) {
    .bulk-edit-container {
      max-width: 100vw;
      max-height: 100vh;
      margin: 0;
      border-radius: 0;
    }

    .bulk-edit-table-wrapper {
      max-height: calc(100vh - 200px);
    }

    .bulk-edit-th {
      padding: 0.75rem 0.5rem;
      font-size: 0.6875rem;
    }

    .bulk-edit-td {
      padding: 0.75rem 0.5rem;
    }

    .localized-inputs {
      min-width: 120px;
    }

    .bulk-edit-input {
      padding: 0.375rem 0.5rem;
      font-size: 0.8125rem;
    }
  }



  /* Admin header classes */
  .admin-page-title {
    font-family: var(--font-sans);
    font-weight: 700;
    font-size: clamp(1.5rem, 3vw, 1.75rem);
    line-height: 1.2;
    letter-spacing: -0.02em;
    color: var(--color-text);
  }

  .admin-breadcrumb-link {
    font-size: 0.875rem;
    color: var(--color-text-faint);
    cursor: pointer;
    transition: color var(--duration-fast) var(--ease-out);
  }

  .admin-breadcrumb-link:hover {
    color: var(--color-primary-600);
  }

  .admin-breadcrumb-current {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
  }

  .admin-stat-card {
    display: flex;
    align-items: center;
    padding: 0.5rem 0.75rem;
    gap: 0.5rem;
    border-radius: var(--radius-card);
    border: 1px solid var(--color-surface-3);
    background: var(--color-surface-2);
    box-shadow: var(--shadow-card);
  }

  .admin-stat-label {
    font-size: 0.75rem;
    color: var(--color-text-faint);
    margin-inline-end: 0.25rem;
  }

  .admin-stat-value {
    font-size: 0.8125rem;
    font-weight: 700;
    color: var(--color-text);
  }
</style>
