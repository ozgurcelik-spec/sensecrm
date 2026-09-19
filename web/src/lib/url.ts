/**
 * Website values are only valid as absolute `http://` or `https://` URLs (C-SEC): anything else
 * (`javascript:`, `data:`, bare hosts) is neither accepted by the form nor rendered as a link.
 */

/** The parsed URL when `value` is an absolute http(s) URL with a host, otherwise undefined. */
function parseHttpUrl(value: string): URL | undefined {
  const trimmed = value.trim();
  if (!/^https?:\/\//i.test(trimmed)) return undefined;
  try {
    const url = new URL(trimmed);
    return (url.protocol === "http:" || url.protocol === "https:") && url.hostname
      ? url
      : undefined;
  } catch {
    return undefined;
  }
}

export function isHttpUrl(value: string): boolean {
  return parseHttpUrl(value) !== undefined;
}

/** href safe to put in an anchor, or undefined when the value must be shown as plain text. */
export function safeHttpHref(value: string | null | undefined): string | undefined {
  return value ? parseHttpUrl(value)?.href : undefined;
}
