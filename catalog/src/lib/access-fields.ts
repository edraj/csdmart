/**
 * Field-level access helpers — apply a permission's `restricted_fields` and
 * `allowed_fields_values` to forms and tables. Pure / unit-testable.
 *
 * Builds on {@link resolvePermission} from `@/lib/access`. The default is ALLOW
 * when no permission entry matches: field restriction *narrows* an already
 * granted action (the action-level `checkAccess` is what lets the user reach
 * the form/table at all), so defaulting to deny here would hide fields from
 * fully-authorized users.
 */
import {
  resolvePermission,
  type PermissionsMap,
} from "@/lib/access";

/** True when `field` must be hidden/disabled for the user at (space, subpath, rt). */
export function isFieldRestricted(
  permissions: PermissionsMap,
  field: string,
  space: string,
  subpath: string,
  resourceType: string,
): boolean {
  const perm = resolvePermission(permissions, space, subpath, resourceType);
  if (!perm) return false; // no entry => allow
  return (perm.restricted_fields ?? []).includes(field);
}

/**
 * The allowed-value whitelist for `field`, or `null` when unconstrained.
 * `allowed_fields_values` maps a field name to its permitted values; a scalar
 * is normalized to a single-element array.
 */
export function allowedValuesForField(
  permissions: PermissionsMap,
  field: string,
  space: string,
  subpath: string,
  resourceType: string,
): any[] | null {
  const perm = resolvePermission(permissions, space, subpath, resourceType);
  const v = perm?.allowed_fields_values?.[field];
  if (v == null) return null;
  return Array.isArray(v) ? v : [v];
}

/**
 * Filter a column-definition array down to the columns the user may see.
 *
 * Permission `restricted_fields` use bare attribute names (e.g. `email`), while
 * table column keys may be dotted (e.g. `attributes.email`) — both directions
 * are matched so either naming hides the column.
 */
export function visibleColumns<T extends { key: string }>(
  columns: T[],
  permissions: PermissionsMap,
  space: string,
  subpath: string,
  resourceType: string,
): T[] {
  const perm = resolvePermission(permissions, space, subpath, resourceType);
  const restricted = new Set(perm?.restricted_fields ?? []);
  if (restricted.size === 0) return columns;
  return columns.filter((c) => {
    const key = c.key;
    const bare = key.includes(".") ? key.slice(key.lastIndexOf(".") + 1) : key;
    return (
      !restricted.has(key) &&
      !restricted.has(bare) &&
      !restricted.has(`attributes.${key}`)
    );
  });
}

/**
 * Narrow a schema-provided list of enum options by the permission whitelist.
 * Returns `options` unchanged when there is no whitelist. Any currently-saved
 * value outside the whitelist is appended so edit forms still round-trip
 * (legacy data is never silently dropped).
 */
export function constrainEnumOptions(
  options: any[],
  permissions: PermissionsMap,
  field: string,
  space: string,
  subpath: string,
  resourceType: string,
  currentValue?: any,
): any[] {
  const allowed = allowedValuesForField(permissions, field, space, subpath, resourceType);
  if (allowed == null) return options;
  const result = options.filter((o) => allowed.includes(o));
  if (
    currentValue != null &&
    !result.includes(currentValue) &&
    options.includes(currentValue)
  ) {
    result.push(currentValue);
  }
  return result;
}
