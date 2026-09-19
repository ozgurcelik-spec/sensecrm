import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { toastApiError } from "@/hooks/use-toast";
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
import type { Lead } from "@/types";
import { LeadConvertDialog } from "./lead-convert-dialog";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const lead: Lead = {
  id: "l1",
  firstName: "Ayşe",
  lastName: "Yılmaz",
  fullName: "Ayşe Yılmaz",
  company: "Acme Ltd",
  source: "web",
  status: "qualified",
  ownerUserId: "user-1",
  createdAt: "2026-05-01T10:00:00Z",
};

const existingAccount = {
  id: "acc-existing",
  name: "Acme Ltd",
  ownerUserId: "user-1",
  createdAt: "2026-01-01T00:00:00Z",
};

const PIPELINES = [
  {
    id: "p1",
    name: "Satış",
    isDefault: true,
    stages: [{ id: "s1", name: "Nitelendirme", order: 1, probability: 10, kind: "open" }],
  },
];

function baseRoutes(accounts: unknown[] = []) {
  return {
    "GET /accounts": () => page(accounts),
    "GET /pipelines": () => PIPELINES,
    "GET /organization/members": () => MEMBERS,
  };
}

function open(canCreateDeal = true) {
  return renderWithProviders(
    <>
      <Routes>
        <Route
          path="/app/leads"
          element={
            <LeadConvertDialog lead={lead} canCreateDeal={canCreateDeal} onClose={vi.fn()} />
          }
        />
        <Route path="/app/accounts/:id" element={<div>account page</div>} />
        <Route path="/app/deals/:id" element={<div>deal page</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: "/app/leads" }
  );
}

describe("LeadConvertDialog", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions([
      "crm.leads.read",
      "crm.leads.write",
      "crm.accounts.write",
      "crm.contacts.write",
      "crm.deals.write",
    ]);
    installApi(client, {
      ...baseRoutes(),
      "POST /leads/l1/convert": () => ({ accountId: "acc-1", contactId: "con-1" }),
    });
  });
  afterEach(clearSession);

  it("converts into a new account without a deal and opens the account", async () => {
    open();

    await userEvent.click(screen.getByRole("button", { name: "Dönüştür" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    const [url, body] = client.post.mock.calls[0] as [string, Record<string, unknown>];
    expect(url).toBe("/leads/l1/convert");
    expect(body).toMatchObject({ createDeal: false });
    expect(body.accountId).toBeUndefined();
    expect(body.dealName).toBeUndefined();
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("/app/accounts/acc-1")
    );
  });

  it("creates a deal too and navigates to the created deal", async () => {
    installApi(client, {
      ...baseRoutes(),
      "POST /leads/l1/convert": () => ({
        accountId: "acc-1",
        contactId: "con-1",
        dealId: "deal-1",
      }),
    });
    open();

    await userEvent.click(screen.getByRole("switch", { name: "Fırsat da oluştur" }));
    // The deal name defaults to the lead's company.
    const dealName = screen.getByLabelText(/Fırsat adı/);
    expect(dealName).toHaveValue("Acme Ltd");
    await userEvent.clear(dealName);
    await userEvent.type(dealName, "Acme yıllık lisans");
    await userEvent.type(screen.getByLabelText("Tutar"), "1500");
    await userEvent.type(screen.getByLabelText("Kapanış tarihi"), "2026-12-31");
    await userEvent.click(screen.getByRole("button", { name: "Dönüştür" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post.mock.calls[0]?.[1]).toMatchObject({
      createDeal: true,
      dealName: "Acme yıllık lisans",
      amount: 1500,
      closingDate: "2026-12-31",
    });
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("/app/deals/deal-1")
    );
  });

  it("requires a deal name once the deal option is on", async () => {
    open();

    await userEvent.click(screen.getByRole("switch", { name: "Fırsat da oluştur" }));
    await userEvent.clear(screen.getByLabelText(/Fırsat adı/));
    await userEvent.click(screen.getByRole("button", { name: "Dönüştür" }));

    expect(await screen.findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("offers a same-named account and converts into it when chosen", async () => {
    installApi(client, {
      ...baseRoutes([existingAccount]),
      "POST /leads/l1/convert": () => ({ accountId: "acc-existing", contactId: "con-1" }),
    });
    open();

    expect(await screen.findByText("Aynı adda müşteri var")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Mevcut müşteriyi kullan" }));
    await userEvent.click(screen.getByRole("button", { name: "Dönüştür" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post.mock.calls[0]?.[1]).toMatchObject({
      accountId: "acc-existing",
      createDeal: false,
    });
  });

  it("requires an account when converting into an existing one", async () => {
    open();

    await userEvent.click(screen.getByRole("radio", { name: "Mevcut müşteri" }));
    await userEvent.click(screen.getByRole("button", { name: "Dönüştür" }));

    expect(await screen.findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("lets the user pick an existing account through the search picker", async () => {
    installApi(client, {
      ...baseRoutes([{ ...existingAccount, id: "acc-9", name: "Başka Firma" }]),
      "POST /leads/l1/convert": () => ({ accountId: "acc-9", contactId: "con-1" }),
    });
    open();

    await userEvent.click(screen.getByRole("radio", { name: "Mevcut müşteri" }));
    await userEvent.click(screen.getByRole("combobox", { name: /Müşteri/ }));
    await userEvent.click(await screen.findByRole("option", { name: "Başka Firma" }));
    await userEvent.click(screen.getByRole("button", { name: "Dönüştür" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post.mock.calls[0]?.[1]).toMatchObject({ accountId: "acc-9" });
  });

  it("reports lead.already_converted and stays on the dialog", async () => {
    const conflict = problem(409, {
      status: 409,
      title: "Conflict",
      code: "lead.already_converted",
    });
    installApi(client, { ...baseRoutes(), "POST /leads/l1/convert": () => conflict });
    open();

    await userEvent.click(screen.getByRole("button", { name: "Dönüştür" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(conflict));
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/leads$/);
  });

  it("hides the deal option without deal write permission", () => {
    open(false);

    expect(screen.queryByRole("switch", { name: "Fırsat da oluştur" })).not.toBeInTheDocument();
  });
});
