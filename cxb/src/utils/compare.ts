/**
 * A plain JSON object: not null, not an array. The narrowing every helper
 * that walks server data or editor content needs before indexing into it.
 */
export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function isDeepEqual(x: unknown, y: unknown): boolean {
  if (x === y) {
    return true;
  } else if (
    typeof x === "object" &&
    x != null &&
    typeof y === "object" &&
    y != null
  ) {
    if (Object.keys(x).length !== Object.keys(y).length) {
      return false;
    }

    for (const prop in x) {
      if (Object.prototype.hasOwnProperty.call(y, prop)) {
        if (!isDeepEqual((x as Record<string, unknown>)[prop], (y as Record<string, unknown>)[prop])) {
          return false;
        }
      } else {
        return false;
      }
    }

    return true;
  } else {
    return false;
  }
}

/** One array member or nested value: objects are cleaned, arrays walked, primitives kept as they are. */
function cleanValue(item: unknown): unknown {
  if (Array.isArray(item)) {
    return item.map(cleanValue);
  }
  return typeof item === "object" && item !== null ? removeEmpty(item as Record<string, unknown>) : item;
}

export function removeEmpty(obj: unknown[]): unknown[];
export function removeEmpty(obj: Record<string, unknown>): Record<string, unknown>;
export function removeEmpty(obj: Record<string, unknown> | unknown[]): Record<string, unknown> | unknown[] {
  // Handle arrays specifically
  if (Array.isArray(obj)) {
    return obj.map(cleanValue);
  }

  const newObj: Record<string, unknown> = {};
  Object.keys(obj).forEach((key) => {
    const value = obj[key];
    if (Array.isArray(value)) {
      // Preserve arrays (including empty arrays)
      newObj[key] = value.map(cleanValue);
    } else if (typeof value === "object" && value !== null) {
      // Handle nested objects
      newObj[key] = removeEmpty(value as Record<string, unknown>);
    } else if (typeof value === "string") {
      if (value.trim().length !== 0) {
        newObj[key] = value;
      }
    } else if (value !== undefined && value !== null) {
      newObj[key] = value;
    }
  });
  return newObj;
}
