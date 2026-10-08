interface FolderColumn {
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

export function folderRenderingColsToListCols(originalArray: Record<string, FolderColumn>): Record<string, ListColumn> {
    const transformedObject: Record<string, ListColumn> = {};
    const keys = Object.keys(originalArray);
    const columnWidth = `${(100 / (keys.length || 1)).toString()}%`;

    keys.forEach(item => {
        transformedObject[originalArray[item].key] = {
            path: originalArray[item].key,
            title: originalArray[item].name,
            type: "string",
            width: columnWidth,
        };
    });

    return transformedObject;
}