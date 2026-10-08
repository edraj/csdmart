/** One column as a folder's rendering payload names it (`index_attributes`, `search_columns`, `csv_columns`). */
export interface FolderColumn {
    key: string;
    name: string;
}

export interface ListColumn {
    path: string;
    title: string;
    type: string;
    width: string;
}

const TIMESTAMP_KEYS = new Set(["created_at", "updated_at"]);

/**
 * True when a column path names a timestamp field, whatever prefix reaches it:
 * `created_at`, `attributes.updated_at` and `Attributes.Created_At` all count.
 * The list renders such columns through `formatDate` instead of raw ISO text.
 */
export function isTimestampKey(key: string | null | undefined): boolean {
    if (!key) return false;
    const last = key.split(".").pop() ?? key;
    return TIMESTAMP_KEYS.has(last.toLowerCase());
}

/**
 * The folder's columns as the list wants them, keyed by column key. A folder
 * payload carries them as an array; the bundled column files as an object.
 */
export function folderRenderingColsToListCols(originalArray: Record<string, FolderColumn> | FolderColumn[]): Record<string, ListColumn> {
    const transformedObject: Record<string, ListColumn> = {};
    const columns: FolderColumn[] = Array.isArray(originalArray) ? originalArray : Object.values(originalArray);
    const columnWidth = `${(100 / (columns.length || 1)).toString()}%`;

    columns.forEach(column => {
        transformedObject[column.key] = {
            path: column.key,
            title: column.name,
            type: "string",
            width: columnWidth,
        };
    });

    return transformedObject;
}
