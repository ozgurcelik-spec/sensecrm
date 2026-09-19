import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
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
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import QuotesPage from "./quotes";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const quote = (id: string, status: string, over: Record<string, unknown> = {}) => ({
  id,
  number: `Q-2026-000${id}`,
  subject: `Konu ${id}`,
  status,
  accountId: "a1",
  accountName: "Acme Ltd",
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  currency: "TRY",
  grandTotal: 1200,
  validUntil: "2026-10-19",
  createdAt: "2026-05-01T10:00:00Z",
  ...over,
});

const lastQuoteParams = () =>
  client.get.mock.calls.filter(([url]) => url === "/quotes").at(-1)?.[1].params;

function renderPage(route = "/app/quotes") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/quotes" element={<QuotesPage />} />
        <Route path="/app/quotes/new" element={<div>Yeni teklif sayfası</div>} />
        <Route path="/app/quotes/:id/edit" element={<div>Düzenleme sayfası</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("QuotesPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /quotes": () =>
        page([
          quote("1", "draft"),
          quote("2", "sent"),
          quote("3", "expired"),
          quote("4", "accepted", { validUntil: undefined }),
        ]),
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page([{ id: "a1", name: "Acme Ltd" }]),
      "GET /accounts/a1": () => ({ id: "a1", name: "Acme Ltd" }),
      "DELETE /quotes/1": () => undefined,
    });
  });
  afterEach(clearSession);

  it("lists quotes with status badges (expired has its own label), totals and validity", async () => {
    setPermissions(["crm.quotes.read"]);
    renderPage();

    expect(await screen.findByRole("link", { name: "Q-2026-0001" })).toHaveAttribute(
      "href",
      "/app/quotes/1"
    );
    const table = screen.getByRole("table");
    for (const label of ["Taslak", "Gönderildi", "Süresi doldu", "Kabul edildi"]) {
      expect(within(table).getByText(label)).toBeInTheDocument();
    }
    expect(within(table).getAllByText(/1\.200,00/)).toHaveLength(4);
    expect(lastQuoteParams()).toEqual({ page: 1, pageSize: 25 });
  });

  it("syncs the status filter with the URL and the request", async () => {
    setPermissions(["crm.quotes.read"]);
    renderPage("/app/quotes?page=3");
    await screen.findByText("Konu 1");

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Süresi doldu" }));

    await waitFor(() => expect(lastQuoteParams()).toEqual({ page: 1, pageSize: 25, status: "expired" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/app/quotes?status=expired");
  });

  it("'accepted, not yet converted' sets status and converted together, and clears them together", async () => {
    setPermissions(["crm.quotes.read"]);
    renderPage();
    await screen.findByText("Konu 1");

    const box = screen.getByRole("checkbox", { name: "Dönüştürülmemiş kabul edilenler" });
    await userEvent.click(box);
    await waitFor(() =>
      expect(lastQuoteParams()).toEqual({ page: 1, pageSize: 25, status: "accepted", converted: "false" })
    );
    expect(box).toBeChecked();
    expect(screen.getByTestId("location").textContent).toContain("status=accepted");
    expect(screen.getByTestId("location").textContent).toContain("converted=false");

    await userEvent.click(box);
    await waitFor(() => expect(lastQuoteParams()).toEqual({ page: 1, pageSize: 25 }));
    expect(box).not.toBeChecked();
  });

  it("restores filters from the URL (deep link) including the account name", async () => {
    setPermissions(["crm.quotes.read", "crm.accounts.read"]);
    renderPage("/app/quotes?status=sent&accountId=a1&ownerUserId=user-2&q=lisans");

    await screen.findByText("Konu 1");
    expect(lastQuoteParams()).toEqual({
      page: 1,
      pageSize: 25,
      q: "lisans",
      status: "sent",
      accountId: "a1",
      ownerUserId: "user-2",
    });
    expect(await screen.findByDisplayValue("Acme Ltd")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Filtreleri temizle" })).toBeInTheDocument();
  });

  it("sorts by a column through the URL (ascending, descending, back to default)", async () => {
    setPermissions(["crm.quotes.read"]);
    renderPage();
    await screen.findByText("Konu 1");

    const header = () => screen.getByRole("button", { name: "Geçerlilik tarihi sütununa göre sırala" });
    await userEvent.click(header());
    await waitFor(() => expect(lastQuoteParams()).toMatchObject({ sort: "validUntil" }));
    await userEvent.click(header());
    await waitFor(() => expect(lastQuoteParams()).toMatchObject({ sort: "-validUntil" }));
    await userEvent.click(header());
    await waitFor(() => expect(lastQuoteParams()).not.toHaveProperty("sort"));
  });

  it("shows the empty state, the loading skeleton and the error with a retry", async () => {
    setPermissions(["crm.quotes.read"]);
    installApi(client, {
      "GET /quotes": () => page([]),
      "GET /organization/members": () => MEMBERS,
    });
    const first = renderPage();
    expect(screen.getAllByTestId("row-skeleton").length).toBeGreaterThan(0);
    expect(await screen.findByText("Kayıt bulunamadı")).toBeInTheDocument();
    first.unmount();

    installApi(client, {
      "GET /quotes": () => problem(500, { code: "unknown" }),
      "GET /organization/members": () => MEMBERS,
    });
    renderPage();
    expect(await screen.findByText("Veriler yüklenemedi")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tekrar dene" })).toBeInTheDocument();
  });

  it("hides write actions without crm.quotes.write", async () => {
    setPermissions(["crm.quotes.read"]);
    renderPage();
    await screen.findByText("Konu 1");
    expect(screen.queryByRole("button", { name: "Yeni teklif" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sil" })).not.toBeInTheDocument();
  });

  it("offers edit and delete on drafts only when the user can write", async () => {
    setPermissions(["crm.quotes.read", "crm.quotes.write"]);
    renderPage();
    await screen.findByText("Konu 1");

    // One draft in the list: one edit and one delete button.
    expect(screen.getAllByRole("button", { name: "Düzenle" })).toHaveLength(1);
    expect(screen.getAllByRole("button", { name: "Sil" })).toHaveLength(1);
    expect(screen.getByRole("button", { name: "Yeni teklif" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Q-2026-0001 numaralı taslak teklif silinecek/)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/quotes/1"));
  });

  it("navigates to the editor for new and edit", async () => {
    setPermissions(["crm.quotes.read", "crm.quotes.write"]);
    renderPage();
    await screen.findByText("Konu 1");
    await userEvent.click(screen.getByRole("button", { name: "Düzenle" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/app/quotes/1/edit");
  });
});
