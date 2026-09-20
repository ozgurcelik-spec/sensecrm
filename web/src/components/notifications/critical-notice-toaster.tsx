import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { Anchor, Stack, Text } from "@mantine/core";
import { useMarkNotificationRead } from "@/hooks/use-notifications";
import { toast } from "@/hooks/use-toast";
import { navigateApp } from "@/lib/app-navigator";
import { safeNotificationLink } from "@/lib/notifications";
import { listNotifications } from "@/services/notifications.service";
import type { AppNotification, NotificationUnreadCount } from "@/types";

const STORAGE_KEY = "notifications.toasted";
const MAX_REMEMBERED = 100;

/** Toasted ids of this browser session; storage that throws (private window, blocked) leaves it in memory. */
function loadSeen(): Set<string> {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    if (Array.isArray(parsed)) return new Set(parsed.filter((v): v is string => typeof v === "string"));
  } catch {
    // Memory only.
  }
  return new Set();
}

function saveSeen(seen: Set<string>): void {
  try {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify([...seen].slice(-MAX_REMEMBERED)));
  } catch {
    // Memory only.
  }
}

const CRITICAL_QUERY = { status: "unread", severity: "critical", page: 1, pageSize: 10 } as const;

interface CriticalNoticeToasterProps {
  /** The polled unread numbers (owned by the bell so the interval runs once). */
  counts: NotificationUnreadCount | undefined;
}

/**
 * Shows one toast for a new critical notice (`newestCriticalId` of the poll). The first poll of a
 * page load only remembers what is already unread (no toast storm on open); later polls toast what
 * appeared since. Several in one round become one summary toast. Renders nothing itself.
 */
export function CriticalNoticeToaster({ counts }: CriticalNoticeToasterProps) {
  const { t } = useTranslation(["notifications"]);
  const markRead = useMarkNotificationRead();
  const seen = useRef<Set<string> | null>(null);
  const started = useRef(false);
  // Ids being looked up: a re-run of the effect (StrictMode) must not look up or toast twice.
  const inFlight = useRef(new Set<string>());
  const newestId = counts?.newestCriticalId;
  const loaded = counts !== undefined;

  useEffect(() => {
    if (!loaded) return;
    seen.current ??= loadSeen();
    const known = seen.current;
    const first = !started.current;
    started.current = true;
    if (!newestId || inFlight.current.has(newestId) || (!first && known.has(newestId))) return;
    inFlight.current.add(newestId);

    void (async () => {
      let items: AppNotification[] = [];
      try {
        items = (await listNotifications({ ...CRITICAL_QUERY })).items;
      } catch {
        // A failed lookup is not worth a toast; the badge still shows the count.
      }
      const fresh = items.filter((item) => !known.has(item.id));
      known.add(newestId);
      for (const item of items) known.add(item.id);
      saveSeen(known);
      if (first || fresh.length === 0) return;

      const only = fresh.length === 1 ? fresh[0] : undefined;
      const link = only ? safeNotificationLink(only.link) : "/app/notifications?status=unread&severity=critical";
      const handle = toast({
        variant: "destructive",
        title: only ? only.title : t("notifications:toast.many", { count: fresh.length }),
        description: (
          <Stack gap={4}>
            {only && <Text size="sm">{only.body}</Text>}
            {link && (
              <Anchor
                component="button"
                type="button"
                size="sm"
                onClick={() => {
                  if (only && !only.isRead) markRead.mutate(only.id);
                  handle.dismiss();
                  navigateApp(link);
                }}
              >
                {t("notifications:toast.view")}
              </Anchor>
            )}
          </Stack>
        ),
        duration: 12_000,
      });
    })();
    // `markRead` and `t` are stable enough; re-running on them would re-toast.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loaded, newestId]);

  return null;
}
