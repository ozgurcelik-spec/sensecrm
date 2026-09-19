import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  LocationDisplay,
  MEMBERS,
  clearSession,
  installApi,
  page,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import DealsPage from "./deals";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const PIPELINES = [
  {
    id: "p1",
    name: "Satış",
    isDefault: true,
    stages: [
      { id: "s1", name: "Nitelendirme", order: 1, probability: 10, kind: "open" },
      { id: "s2", name: "Kazanıldı", order: 2, probability: 100, kind: "won" },
    ],
  },
  {
    id: "p2",
    name: "Bayi",
    isDefault: false,
    stages: [{ id: "s9", name: "Başvuru", order: 1, probability: 10, kind: "open" }],
  },
];

const BOARD = {
  pipelineId: "p1",
  stages: [
    {
      id: "s1",
      name: "Nitelendirme",
      kind: "open",
      probability: 10,
      count: 1,
      totalAmount: 1000,
      deals: [{ id: "d1", name: "Alfa", accountName: "Acme", amount: 1000, currency: "TRY" }],
    },
    {
      id: "s2",
      name: "Kazanıldı",
      kind: "won",
      probability: 100,
      count: 0,
      totalAmount: 0,
      deals: [],
    },
  ],
};

const DEAL = {
  id: "d1",
  name: "Alfa",
  accountId: "a1",
  accountName: "Acme",
  pipelineId: "p1",
  pipelineName: "Satış",
  stageId: "s1",
  stageName: "Nitelendirme",
  stageKind: "open",
  probability: 10,
  amount: 1000,
  currency: "TRY",
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  createdAt: "2026-05-01T10:00:00Z",
};

function renderPage(route = "/app/deals") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/deals" element={<DealsPage />} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const calls = (url: string) => client.get.mock.calls.filter(([u]) => u === url);

describe("DealsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /pipelines": () => PIPELINES,
      "GET /deals/board": () => BOARD,
      "GET /deals": ({ params }) =>
        page([DEAL], { totalCount: 40, page: Number(params?.page ?? 1) }),
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page([]),
    });
  });
  afterEach(clearSession);

  it("opens on the kanban board of the default pipeline", async () => {
    setPermissions(["crm.deals.read", "crm.deals.write"]);
    renderPage();

    expect(await screen.findByText("Alfa")).toBeInTheDocument();
    expect(calls("/deals/board").at(0)?.[1].params).toEqual({ pipelineId: "p1" });
    expect(calls("/deals")).toHaveLength(0);
    expect(screen.getByRole("group", { name: "Nitelendirme" })).toBeInTheDocument();
  });

  it("switches to the list view (paged, URL-synced) and back", async () => {
    setPermissions(["crm.deals.read"]);
    renderPage();
    await screen.findByText("Alfa");

    await userEvent.click(screen.getByRole("radio", { name: "Liste" }));

    await waitFor(() => expect(calls("/deals").length).toBeGreaterThan(0));
    expect(calls("/deals").at(-1)?.[1].params).toEqual({ page: 1, pageSize: 25, pipelineId: "p1" });
    expect(screen.getByTestId("location")).toHaveTextContent("/app/deals?view=list");
    expect(await screen.findByText("Toplam 40 kayıt")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "2" }));
    await waitFor(() =>
      expect(calls("/deals").at(-1)?.[1].params).toMatchObject({ page: 2, pipelineId: "p1" })
    );

    await userEvent.click(screen.getByRole("radio", { name: "Pano" }));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/deals$/));
    expect(await screen.findByRole("group", { name: "Nitelendirme" })).toBeInTheDocument();
  });

  it("reloads the board for another pipeline and keeps it in the URL", async () => {
    setPermissions(["crm.deals.read"]);
    renderPage();
    await screen.findByText("Alfa");

    await userEvent.click(screen.getByRole("combobox", { name: "Satış hunisi" }));
    await userEvent.click(await screen.findByRole("option", { name: "Bayi" }));

    await waitFor(() =>
      expect(calls("/deals/board").at(-1)?.[1].params).toEqual({ pipelineId: "p2" })
    );
    expect(screen.getByTestId("location")).toHaveTextContent("pipelineId=p2");
  });

  it("filters the list by stage and sends the sort", async () => {
    setPermissions(["crm.deals.read"]);
    renderPage("/app/deals?view=list&sort=-amount");

    await screen.findByText("Alfa");
    expect(calls("/deals")[0]?.[1].params).toEqual({
      page: 1,
      pageSize: 25,
      sort: "-amount",
      pipelineId: "p1",
    });

    await userEvent.click(screen.getByRole("combobox", { name: "Aşama" }));
    await userEvent.click(await screen.findByRole("option", { name: "Kazanıldı" }));
    await waitFor(() =>
      expect(calls("/deals").at(-1)?.[1].params).toMatchObject({ stageId: "s2", sort: "-amount" })
    );
  });

  it("hides New and the board move controls for a read-only user", async () => {
    setPermissions(["crm.deals.read"]);
    renderPage();
    await screen.findByText("Alfa");

    expect(screen.queryByRole("button", { name: "Yeni fırsat" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /fırsatını taşı/ })).not.toBeInTheDocument();
  });

  it("shows New and the move controls when the user may write deals", async () => {
    setPermissions(["crm.deals.read", "crm.deals.write"]);
    renderPage();
    await screen.findByText("Alfa");

    expect(screen.getByRole("button", { name: "Yeni fırsat" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Alfa fırsatını taşı" })).toBeInTheDocument();
  });
});
