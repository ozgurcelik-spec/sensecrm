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
import AccountsPage from "./accounts";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const account = (n: number) => ({
  id: `a${n}`,
  name: `Firma ${n}`,
  industry: "Perakende",
  phone: "0212 000 00 00",
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  createdAt: "2026-05-01T10:00:00Z",
});

function listCalls() {
  return client.get.mock.calls
    .filter(([url]) => url === "/accounts")
    .map(([, config]) => config.params);
}

function renderPage(route = "/app/accounts") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/accounts" element={<AccountsPage />} />
        <Route path="/app/accounts/:id" element={<div>detail</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

describe("AccountsPage list", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["crm.accounts.read", "crm.accounts.write"]);
    installApi(client, {
      "GET /accounts": ({ params }) =>
        page([account(1), account(2)], {
          page: Number(params?.page ?? 1),
          pageSize: Number(params?.pageSize ?? 25),
          totalCount: 60,
        }),
      "GET /organization/members": () => MEMBERS,
    });
  });
  afterEach(clearSession);

  it("loads the first page with the default paging and shows the rows", async () => {
    renderPage();

    expect(await screen.findByText("Firma 1")).toBeInTheDocument();
    expect(screen.getByText("Firma 2")).toBeInTheDocument();
    expect(listCalls()[0]).toEqual({ page: 1, pageSize: 25 });
    expect(screen.getByText("Toplam 60 kayıt")).toBeInTheDocument();
  });

  it("requests the next page from the server and reflects it in the URL", async () => {
    renderPage();
    await screen.findByText("Firma 1");

    await userEvent.click(screen.getByRole("button", { name: "2" }));

    await waitFor(() => expect(listCalls().at(-1)).toEqual({ page: 2, pageSize: 25 }));
    expect(screen.getByTestId("location")).toHaveTextContent("/app/accounts?page=2");
  });

  it("debounces the search box, sends q and goes back to page 1", async () => {
    renderPage("/app/accounts?page=2");
    await screen.findByText("Firma 1");
    const before = listCalls().length;

    await userEvent.type(screen.getByRole("searchbox", { name: "Müşteri ara..." }), "acme");

    // Not one request per keystroke: the debounce fires a single search.
    await waitFor(() => expect(listCalls().at(-1)).toEqual({ page: 1, pageSize: 25, q: "acme" }));
    expect(listCalls().length - before).toBe(1);
    expect(screen.getByTestId("location")).toHaveTextContent("/app/accounts?q=acme");
  });

  it("filters by owner and resets paging", async () => {
    renderPage("/app/accounts?page=3");
    await screen.findByText("Firma 1");

    await userEvent.click(screen.getByRole("combobox", { name: "Sahip" }));
    await userEvent.click(await screen.findByRole("option", { name: "Grace Hopper" }));

    await waitFor(() =>
      expect(listCalls().at(-1)).toEqual({ page: 1, pageSize: 25, ownerUserId: "user-2" })
    );
    expect(screen.getByTestId("location")).toHaveTextContent("ownerUserId=user-2");
    expect(screen.getByTestId("location")).not.toHaveTextContent("page=");
  });

  it("cycles the sort ascending, descending, then back to the default order", async () => {
    renderPage();
    await screen.findByText("Firma 1");
    const sortName = () => screen.getByRole("button", { name: "Müşteri adı sütununa göre sırala" });

    await userEvent.click(sortName());
    await waitFor(() => expect(listCalls().at(-1)).toMatchObject({ sort: "name" }));

    await userEvent.click(sortName());
    await waitFor(() => expect(listCalls().at(-1)).toMatchObject({ sort: "-name" }));
    expect(screen.getByRole("columnheader", { name: /Müşteri adı/ })).toHaveAttribute(
      "aria-sort",
      "descending"
    );

    await userEvent.click(sortName());
    await waitFor(() => expect(listCalls().at(-1)).not.toHaveProperty("sort"));
  });

  it("restores paging, search, sort and filters from the URL", async () => {
    renderPage("/app/accounts?page=2&pageSize=50&q=foo&sort=-createdAt&industry=Perakende");

    await screen.findByText("Firma 1");
    expect(listCalls()[0]).toEqual({
      page: 2,
      pageSize: 50,
      q: "foo",
      sort: "-createdAt",
      industry: "Perakende",
    });
    expect(screen.getByRole("searchbox", { name: "Müşteri ara..." })).toHaveValue("foo");
  });

  it("changes the page size and clears filters back to the defaults", async () => {
    renderPage("/app/accounts?q=foo");
    await screen.findByText("Firma 1");

    await userEvent.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    await waitFor(() => expect(listCalls().at(-1)).toEqual({ page: 1, pageSize: 25 }));
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/accounts$/);

    await userEvent.click(screen.getByRole("combobox", { name: "Sayfa başına kayıt" }));
    await userEvent.click(await screen.findByRole("option", { name: "50" }));
    await waitFor(() => expect(listCalls().at(-1)).toEqual({ page: 1, pageSize: 50 }));
  });

  it("shows the empty state", async () => {
    client.get.mockImplementation(async (url: string) => ({
      data: url === "/accounts" ? page([]) : MEMBERS,
    }));
    renderPage();

    expect(await screen.findByText("Kayıt bulunamadı")).toBeInTheDocument();
  });

  it("shows a loading skeleton first", () => {
    client.get.mockImplementation(() => new Promise(() => undefined));
    renderPage();

    expect(screen.getAllByTestId("row-skeleton").length).toBeGreaterThan(0);
  });

  it("shows an error with retry when the request fails, and recovers on retry", async () => {
    let fail = true;
    client.get.mockImplementation(async (url: string) => {
      if (url === "/organization/members") return { data: MEMBERS };
      if (fail) throw problem(500, { status: 500, title: "Server error" });
      return { data: page([account(1)]) };
    });
    renderPage();

    expect(await screen.findByText("Veriler yüklenemedi")).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole("button", { name: "Tekrar dene" }));

    expect(await screen.findByText("Firma 1")).toBeInTheDocument();
  });
});

describe("AccountsPage permission gating", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /accounts": () => page([account(1)]),
      "GET /organization/members": () => MEMBERS,
    });
  });
  afterEach(clearSession);

  it("hides New, edit and delete for a read-only user", async () => {
    setPermissions(["crm.accounts.read"]);
    renderPage();

    await screen.findByText("Firma 1");
    expect(screen.queryByRole("button", { name: "Yeni müşteri" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sil" })).not.toBeInTheDocument();
  });

  it("shows New, edit and delete when the user may write", async () => {
    setPermissions(["crm.accounts.read", "crm.accounts.write"]);
    renderPage();

    await screen.findByText("Firma 1");
    expect(screen.getByRole("button", { name: "Yeni müşteri" })).toBeInTheDocument();
    const row = screen.getByText("Firma 1").closest("tr") as HTMLElement;
    expect(within(row).getByRole("button", { name: "Düzenle" })).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: "Sil" })).toBeInTheDocument();
  });

  it("reports the account.has_dependents error from the server when deleting", async () => {
    const { toastApiError } = await import("@/hooks/use-toast");
    setPermissions(["crm.accounts.read", "crm.accounts.write"]);
    const conflict = problem(409, {
      status: 409,
      title: "Conflict",
      code: "account.has_dependents",
    });
    installApi(client, {
      "GET /accounts": () => page([account(1)]),
      "GET /organization/members": () => MEMBERS,
      "DELETE /accounts/a1": () => conflict,
    });
    renderPage();

    await screen.findByText("Firma 1");
    await userEvent.click(screen.getByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(conflict));
  });
});
