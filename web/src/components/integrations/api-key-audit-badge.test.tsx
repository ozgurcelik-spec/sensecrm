import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { RecordAuditTab } from "@/components/crm/record-audit-tab";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, setPermissions, type MockClient } from "@/test/crm";
import AuditLogPage from "@/pages/audit-log";
import { ApiKeyAuditBadge } from "./badges";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const ENTRIES = [
  {
    id: "e2",
    entityType: "Lead",
    entityId: "l1",
    action: "created",
    userDisplayName: "Ada Lovelace (API: Raporlama)",
    apiKeyId: "k1",
    occurredAt: "2026-09-20T10:00:00Z",
    changes: { name: { old: null, new: "Acme" } },
  },
  {
    id: "e1",
    entityType: "Lead",
    entityId: "l1",
    action: "updated",
    userDisplayName: "Ada Lovelace",
    apiKeyId: null,
    occurredAt: "2026-09-19T10:00:00Z",
    changes: { name: { old: "Acme", new: "Acme A.Ş." } },
  },
];

describe("API key badge on audit rows", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["crm.leads.read", "org.audit.read"]);
  });
  afterEach(clearSession);

  it("renders only when the change was made with an API key", () => {
    const { rerender } = renderWithProviders(<ApiKeyAuditBadge apiKeyId="k1" />);
    expect(screen.getByTestId("audit-api-key-badge")).toHaveTextContent("API anahtarıyla");
    rerender(<ApiKeyAuditBadge apiKeyId={null} />);
    expect(screen.queryByTestId("audit-api-key-badge")).not.toBeInTheDocument();
    rerender(<ApiKeyAuditBadge apiKeyId={undefined} />);
    expect(screen.queryByTestId("audit-api-key-badge")).not.toBeInTheDocument();
  });

  it("marks the rows of a record's audit tab", async () => {
    installApi(client, { "GET /audit": () => ({ total: 2, items: ENTRIES }) });
    renderWithProviders(<RecordAuditTab entityType="Lead" entityId="l1" />);
    const entries = await screen.findAllByTestId("audit-entry");
    expect(within(entries[0] as HTMLElement).getByTestId("audit-api-key-badge")).toBeInTheDocument();
    expect(within(entries[1] as HTMLElement).queryByTestId("audit-api-key-badge")).not.toBeInTheDocument();
  });

  it("marks the rows of the organization audit log", async () => {
    installApi(client, { "GET /organization/audit": () => ({ total: 2, items: ENTRIES }) });
    renderWithProviders(<AuditLogPage />);
    const badges = await screen.findAllByTestId("audit-api-key-badge");
    expect(badges).toHaveLength(1);
    expect(badges[0]?.closest("tr")).toHaveTextContent("Ada Lovelace (API: Raporlama)");
  });
});
