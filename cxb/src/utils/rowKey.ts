/** The parts of a list record that identify it. */
export type KeyedRecord = {
    uuid?: string | null;
    resource_type?: string | null;
    subpath?: string | null;
    shortname?: string | null;
    attributes?: { timestamp?: string | null } | null;
};

/**
 * A stable key for an `{#each}` over list records.
 *
 * Entries carry a uuid. Rows that do not (events, history) are identified by
 * their locator plus the event timestamp, which is what makes two events on
 * the same entry distinct. Never the position in the list: a row keyed by
 * position is re-used for a different record after a sort or a page change.
 */
export function rowKey(row: KeyedRecord | null | undefined): string {
    if (!row) return "";
    if (row.uuid) return row.uuid;
    return [
        row.resource_type ?? "",
        row.subpath ?? "",
        row.shortname ?? "",
        row.attributes?.timestamp ?? "",
    ].join("|");
}
