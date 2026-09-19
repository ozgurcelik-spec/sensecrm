import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  LocationDisplay,
  clearSession,
  installApi,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import ReportsPage from "./reports";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@mantine/charts", async () => (await import("@/test/charts")).chartMocks);
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const FUNNEL = {
  pipelineId: "p1",
  stages: [
    { id: "s1", name: "Aday", kind: "open", order: 1, probability: 10, count: 5, totalAmount: 500 },
    {
      id: "s2",
      name: "Kazanıldı",
      kind: "won",
      order: 2,
      probability: 100,
      count: 1,
      totalAmount: 300,
    },
  ],
};
const PIPELINES = [
  { id: "p1", name: "Ana huni", isDefault: true, stages: [] },
  { id: "p2", name: "Kurumsal huni", isDefault: false, stages: [] },
];
const WON_LOST = [
  { period: "2026-08", wonCount: 2, wonAmount: 12500.5, lostCount: 1, lostAmount: 400 },
  { period: "2026-09", wonCount: 0, wonAmount: 0, lostCount: 0, lostAmount: 0 },
];
const SOURCES = [
  { source: "web", count: 8, convertedCount: 2 },
  { source: "referral", count: 2, convertedCount: 0 },
];
const OWNERS = [
  {
    ownerUserId: "user-1",
    ownerName: 'Ada "Kaptan" Lovelace',
    openDealCount: 3,
    openDealAmount: 1500,
    wonCount: 1,
    wonAmount: 700,
    leadCount: 6,
  },
];
const USERS = [
  { userId: "user-1", userName: "Ada Lovelace", completedCount: 5, openCount: 2, overdueCount: 1 },
];

const paramsOf = (path: string) =>
  client.get.mock.calls.filter(([url]) => url === path).at(-1)?.[1].params;
const callsTo = (prefix: string) => client.get.mock.calls.filter(([url]) => url.startsWith(prefix));

function renderPage(route = "/app/reports") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/reports" element={<ReportsPage />} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("ReportsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // Today is 19 Sep 2026 (afternoon, so the calendar day is the same in every zone).
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-09-19T12:00:00Z"));
    setPermissions(["crm.reports.read", "crm.deals.read"]);
    installApi(client, {
      "GET /pipelines": () => PIPELINES,
      "GET /reports/sales/funnel": () => FUNNEL,
      "GET /reports/sales/won-lost": () => WON_LOST,
      "GET /reports/sales/leads-by-source": () => SOURCES,
      "GET /reports/sales/by-owner": () => OWNERS,
      "GET /reports/activities/by-user": () => USERS,
    });
  });
  afterEach(() => {
    vi.useRealTimers();
    clearSession();
  });

  it("starts on the funnel with the default pipeline and no date params (the funnel is a snapshot)", async () => {
    renderPage();

    expect(await screen.findByTestId("chart-funnel")).toBeInTheDocument();
    expect(paramsOf("/reports/sales/funnel")).toEqual({});
    // Only the active tab is requested.
    expect(callsTo("/reports/sales/won-lost")).toHaveLength(0);
    expect(screen.getByText("Aday")).toBeInTheDocument();
  });

  it("sends the chosen pipeline and keeps it in the URL", async () => {
    renderPage();
    await screen.findByTestId("chart-funnel");

    await userEvent.click(await screen.findByRole("combobox", { name: "Satış hunisi" }));
    await userEvent.click(await screen.findByRole("option", { name: "Kurumsal huni" }));

    await waitFor(() => expect(paramsOf("/reports/sales/funnel")).toEqual({ pipelineId: "p2" }));
    expect(screen.getByTestId("location")).toHaveTextContent("pipelineId=p2");
  });

  it("turns the range presets into from/to request params", async () => {
    renderPage("/app/reports?tab=wonLost");
    await screen.findByTestId("chart-bar");

    // Default: last 12 months = 1 Oct 2025 .. today.
    expect(paramsOf("/reports/sales/won-lost")).toEqual({
      from: "2025-10-01",
      to: "2026-09-19",
      groupBy: "month",
    });

    await userEvent.click(screen.getByText("Bu ay"));
    await waitFor(() =>
      expect(paramsOf("/reports/sales/won-lost")).toEqual({
        from: "2026-09-01",
        to: "2026-09-19",
        groupBy: "month",
      })
    );
    expect(screen.getByTestId("location")).toHaveTextContent("range=thisMonth");

    await userEvent.click(screen.getByText("Son 3 ay"));
    await waitFor(() => expect(paramsOf("/reports/sales/won-lost")?.from).toBe("2026-07-01"));

    // Back to the default preset: not written to the URL, and served from the cache of the first request.
    await userEvent.click(screen.getByText("Son 12 ay"));
    await waitFor(() => expect(screen.getByTestId("location")).not.toHaveTextContent("range="));
    expect(client.get.mock.calls.filter(([url]) => url === "/reports/sales/won-lost")).toHaveLength(
      3
    );
  });

  it("uses a custom range from the date inputs and waits until it is valid", async () => {
    renderPage("/app/reports?tab=leadSources&range=custom");
    await screen.findByLabelText("Başlangıç");

    // Nothing is requested for an incomplete custom range.
    expect(screen.getByRole("status")).toHaveTextContent(
      "Geçerli bir başlangıç ve bitiş tarihi seçin"
    );
    expect(callsTo("/reports/sales/leads-by-source")).toHaveLength(0);

    fireEvent.change(screen.getByLabelText("Başlangıç"), { target: { value: "2026-01-05" } });
    fireEvent.change(screen.getByLabelText("Bitiş"), { target: { value: "2026-02-10" } });

    await waitFor(() =>
      expect(paramsOf("/reports/sales/leads-by-source")).toEqual({
        from: "2026-01-05",
        to: "2026-02-10",
      })
    );
    expect(screen.getByTestId("location")).toHaveTextContent("from=2026-01-05");
    expect(screen.queryByRole("status")).not.toBeInTheDocument();

    // A reversed range is rejected again instead of being sent.
    const before = callsTo("/reports/sales/leads-by-source").length;
    fireEvent.change(screen.getByLabelText("Bitiş"), { target: { value: "2026-01-01" } });
    expect(await screen.findByRole("status")).toBeInTheDocument();
    expect(callsTo("/reports/sales/leads-by-source")).toHaveLength(before);
  });

  it("groups won/lost by week when asked", async () => {
    renderPage("/app/reports?tab=wonLost");
    await screen.findByTestId("chart-bar");

    await userEvent.click(screen.getByText("Haftalık"));

    await waitFor(() => expect(paramsOf("/reports/sales/won-lost")?.groupBy).toBe("week"));
    expect(screen.getByTestId("location")).toHaveTextContent("groupBy=week");
  });

  it("requests each remaining report with the range and shows its table", async () => {
    renderPage("/app/reports?range=thisMonth");
    await screen.findByTestId("chart-funnel");
    const range = { from: "2026-09-01", to: "2026-09-19" };

    await userEvent.click(screen.getByRole("tab", { name: "Potansiyel kaynakları" }));
    expect(await screen.findByText("Tavsiye")).toBeInTheDocument();
    expect(paramsOf("/reports/sales/leads-by-source")).toEqual(range);
    expect(screen.getByText("%25")).toBeInTheDocument(); // 2 of 8 converted

    await userEvent.click(screen.getByRole("tab", { name: "Satış temsilcisi" }));
    expect(await screen.findByText('Ada "Kaptan" Lovelace')).toBeInTheDocument();
    expect(paramsOf("/reports/sales/by-owner")).toEqual(range);

    await userEvent.click(screen.getByRole("tab", { name: "Aktivite" }));
    expect(await screen.findByText("Ada Lovelace")).toBeInTheDocument();
    expect(paramsOf("/reports/activities/by-user")).toEqual(range);
  });

  it("shows an empty state and disables the CSV button when there is no data", async () => {
    installApi(client, {
      "GET /pipelines": () => PIPELINES,
      "GET /reports/sales/won-lost": () => [],
    });
    renderPage("/app/reports?tab=wonLost");

    expect(await screen.findByText("Bu dönem için veri yok")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "CSV indir" })).toBeDisabled();
  });

  describe("CSV download", () => {
    let blobs: Blob[];
    let names: string[];

    beforeEach(() => {
      blobs = [];
      names = [];
      Object.assign(URL, {
        createObjectURL: vi.fn((blob: Blob) => {
          blobs.push(blob);
          return "blob:report";
        }),
        revokeObjectURL: vi.fn(),
      });
      vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (
        this: HTMLAnchorElement
      ) {
        names.push(this.download);
      });
    });
    afterEach(() => vi.restoreAllMocks());

    async function text(blob: Blob) {
      const bytes = new Uint8Array(await blob.arrayBuffer());
      return {
        hasBom: bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf,
        content: new TextDecoder("utf-8", { ignoreBOM: false }).decode(bytes),
      };
    }

    it("saves the won/lost table with a BOM, Turkish separators and the range in the name", async () => {
      renderPage("/app/reports?tab=wonLost");
      await screen.findByTestId("chart-bar");

      await userEvent.click(screen.getByRole("button", { name: "CSV indir" }));

      expect(blobs).toHaveLength(1);
      expect(names).toEqual(["kazanilan-kaybedilen_2025-10-01_2026-09-19.csv"]);
      const { hasBom, content } = await text(blobs[0] as Blob);
      expect(hasBom).toBe(true);
      expect(content).toBe(
        [
          "Dönem;Kazanılan adet;Kazanılan tutar;Kaybedilen adet;Kaybedilen tutar",
          "2026-08;2;12500,5;1;400",
          "2026-09;0;0;0;0",
        ].join("\r\n")
      );
    });

    it("quotes and escapes text cells (quotes, the delimiter) in the sales rep report", async () => {
      installApi(client, {
        "GET /reports/sales/by-owner": () => [
          { ...(OWNERS[0] as object), ownerName: 'Ada; "Kaptan" Lovelace' },
        ],
      });
      renderPage("/app/reports?tab=byOwner");
      await screen.findByText('Ada; "Kaptan" Lovelace');

      await userEvent.click(screen.getByRole("button", { name: "CSV indir" }));

      const { content } = await text(blobs[0] as Blob);
      expect(content.split("\r\n")[1]).toBe('"Ada; ""Kaptan"" Lovelace";3;1500;1;700;6');
    });

    it("writes the lead source conversion rate as a percentage number", async () => {
      renderPage("/app/reports?tab=leadSources");
      await screen.findByText("Tavsiye");

      await userEvent.click(screen.getByRole("button", { name: "CSV indir" }));

      const { content } = await text(blobs[0] as Blob);
      expect(content.split("\r\n")).toEqual([
        "Kaynak;Adet;Dönüşen;Dönüşüm oranı (%)",
        "Web;8;2;25",
        "Tavsiye;2;0;0",
      ]);
    });
  });
});
