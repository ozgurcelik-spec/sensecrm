import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, setPermissions, type MockClient } from "@/test/crm";
import { RecordAuditTab } from "./record-audit-tab";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

describe("RecordAuditTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["crm.accounts.read"]);
  });
  afterEach(clearSession);

  it("asks the per-record endpoint and renders old and new values per field", async () => {
    installApi(client, {
      "GET /audit": () => ({
        total: 2,
        items: [
          {
            id: "e2",
            entityType: "Account",
            entityId: "a1",
            action: "updated",
            userDisplayName: "Ada Lovelace",
            occurredAt: "2026-05-02T10:00:00Z",
            changes: {
              Name: { old: "Eski Ad", new: "Yeni Ad" },
              industry: { old: null, new: "Gıda" },
            },
          },
          {
            id: "e1",
            entityType: "Account",
            entityId: "a1",
            action: "created",
            occurredAt: "2026-05-01T10:00:00Z",
            changes: { name: { old: null, new: "Eski Ad" } },
          },
        ],
      }),
    });
    renderWithProviders(<RecordAuditTab entityType="Account" entityId="a1" />);

    const entries = await screen.findAllByTestId("audit-entry");
    expect(entries).toHaveLength(2);
    expect(client.get).toHaveBeenCalledWith("/audit", {
      params: { entityType: "Account", entityId: "a1", page: 1, pageSize: 20 },
    });

    const updated = within(entries[0] as HTMLElement);
    expect(updated.getByText("Güncellendi")).toBeInTheDocument();
    expect(updated.getByText("Ada Lovelace")).toBeInTheDocument();
    // PascalCase field names are normalised and translated.
    const nameRow = updated.getByText("Ad").closest("tr") as HTMLElement;
    expect(within(nameRow).getByText("Eski Ad")).toBeInTheDocument();
    expect(within(nameRow).getByText("Yeni Ad")).toBeInTheDocument();
    const industryRow = updated.getByText("Sektör").closest("tr") as HTMLElement;
    expect(within(industryRow).getByText("-")).toBeInTheDocument();
    expect(within(industryRow).getByText("Gıda")).toBeInTheDocument();

    expect(within(entries[1] as HTMLElement).getByText("Sistem")).toBeInTheDocument();
  });

  it("shows an empty message when the record has no history", async () => {
    installApi(client, { "GET /audit": () => ({ items: [], total: 0 }) });
    renderWithProviders(<RecordAuditTab entityType="Deal" entityId="d1" />);

    expect(await screen.findByText("Bu kayıt için denetim kaydı yok")).toBeInTheDocument();
  });

  it("shows an error with retry when loading fails", async () => {
    installApi(client, { "GET /audit": () => problem(500, { status: 500, title: "boom" }) });
    renderWithProviders(<RecordAuditTab entityType="Deal" entityId="d1" />);

    expect(await screen.findByText("Veriler yüklenemedi")).toBeInTheDocument();
  });
});
