import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, page, setPermissions, type MockClient } from "@/test/crm";
import HomePage from "./home";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const COUNTS: Record<string, number> = { new: 4, contacted: 3, qualified: 2 };

describe("HomePage sales cards", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /leads": ({ params }) => page([], { totalCount: COUNTS[String(params?.status)] ?? 0 }),
      "GET /deals/board": () => ({
        pipelineId: "p1",
        stages: [
          {
            id: "s1",
            name: "A",
            kind: "open",
            probability: 10,
            count: 3,
            totalAmount: 1000,
            deals: [],
          },
          {
            id: "s2",
            name: "B",
            kind: "open",
            probability: 50,
            count: 2,
            totalAmount: 500,
            deals: [],
          },
          {
            id: "s3",
            name: "C",
            kind: "won",
            probability: 100,
            count: 9,
            totalAmount: 99999,
            deals: [],
          },
        ],
      }),
    });
  });
  afterEach(clearSession);

  it("sums open leads from the list totals and open deals from the board", async () => {
    setPermissions(["crm.leads.read", "crm.deals.read"]);
    renderWithProviders(<HomePage />);

    const leadsCard = (await screen.findByText("Açık potansiyeller")).closest("a") as HTMLElement;
    await waitFor(() => expect(within(leadsCard).getByTestId("stat-value")).toHaveTextContent("9"));
    const dealsCard = screen.getByText("Açık fırsatlar").closest("a") as HTMLElement;
    await waitFor(() => expect(within(dealsCard).getByTestId("stat-value")).toHaveTextContent("5"));
    // Won deals are not part of the open pipeline amount.
    const amountCard = screen.getByText("Açık huni tutarı").closest("a") as HTMLElement;
    expect(within(amountCard).getByTestId("stat-value")).toHaveTextContent(/1\.500/);

    // The lead statistics only ask for one row: they use totalCount, not the items.
    const leadCalls = client.get.mock.calls.filter(([u]) => u === "/leads");
    expect(leadCalls.map(([, c]) => c.params.status).sort()).toEqual([
      "contacted",
      "new",
      "qualified",
    ]);
    expect(leadCalls.every(([, c]) => c.params.pageSize === 1)).toBe(true);
  });

  it("does not request or show what the user may not read", async () => {
    setPermissions(["crm.deals.read"]);
    renderWithProviders(<HomePage />);

    await screen.findByText("Açık fırsatlar");
    expect(screen.queryByText("Açık potansiyeller")).not.toBeInTheDocument();
    expect(client.get.mock.calls.some(([u]) => u === "/leads")).toBe(false);
  });
});
