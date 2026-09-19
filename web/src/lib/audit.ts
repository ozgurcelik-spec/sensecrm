export interface FieldChange {
  field: string;
  before: unknown;
  after: unknown;
}

function lowerFirst(value: string): string {
  return value.charAt(0).toLowerCase() + value.slice(1);
}

/**
 * Turns an audit entry's `changes` (`{ field: { old, new } }`) into rows. Tolerates `oldValue`/`newValue`
 * and `before`/`after` pair names, and plain values (treated as "new"). Field names are lower-camel-cased
 * so `OwnerUserId` and `ownerUserId` translate the same way.
 */
export function parseChanges(changes: Record<string, unknown> | null | undefined): FieldChange[] {
  return Object.entries(changes ?? {}).map(([field, value]) => {
    const name = lowerFirst(field);
    if (value && typeof value === "object" && !Array.isArray(value)) {
      const v = value as Record<string, unknown>;
      if ("old" in v || "new" in v) return { field: name, before: v.old, after: v.new };
      if ("oldValue" in v || "newValue" in v) {
        return { field: name, before: v.oldValue, after: v.newValue };
      }
      if ("before" in v || "after" in v) return { field: name, before: v.before, after: v.after };
    }
    return { field: name, before: undefined, after: value };
  });
}

export function displayValue(value: unknown): string {
  if (value === null || value === undefined || value === "") return "-";
  if (typeof value === "string") return value;
  return JSON.stringify(value);
}
