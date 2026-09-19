import { Anchor, Text } from "@mantine/core";
import { safeHttpHref } from "@/lib/url";

/**
 * A website value as a link only when it is an absolute http(s) URL; anything else (legacy bare
 * hosts, `javascript:` and other schemes) is shown as inert text, never as an href.
 */
export function WebsiteValue({ website }: { website?: string | null }) {
  if (!website) return <>-</>;
  const href = safeHttpHref(website);
  if (!href) {
    return (
      <Text size="sm" component="span">
        {website}
      </Text>
    );
  }
  return (
    <Anchor href={href} target="_blank" rel="noopener noreferrer" size="sm">
      {website}
    </Anchor>
  );
}
