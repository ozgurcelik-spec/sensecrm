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
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { caseDetail } from "@/test/service";
import { CaseFormDialog } from "./case-form-dialog";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const ACCOUNTS = [
  { id: "a1", name: "Acme A.Ş." },
  { id: "a2", name: "Globex" },
];
const CONTACTS = [
  { id: "c1", fullName: "Ayşe Yılmaz", accountId: "a1", accountName: "Acme A.Ş." },
  { id: "c2", fullName: "Bora Kaya" },
];

const onClose = vi.fn();
const onSaved = vi.fn();

function open(props: Partial<Parameters<typeof CaseFormDialog>[0]> = {}) {
  return renderWithProviders(
    <>
      <Routes>
        <Route
          path="/"
          element={<CaseFormDialog onClose={onClose} onSaved={onSaved} {...props} />}
        />
        <Route path="/app/cases/:id" element={<div>detail page</div>} />
      </Routes>
      <LocationDisplay />
    </>
  );
}

function lastPost(url: string) {
  return client.post.mock.calls.filter(([u]) => u === url).at(-1)?.[1];
}

async function pick(label: string, option: string) {
  await userEvent.click(screen.getByRole("combobox", { name: label }));
  await userEvent.click(await screen.findByRole("option", { name: option }));
}

describe("CaseFormDialog", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions([
      "crm.cases.read",
      "crm.cases.write",
      "crm.accounts.read",
      "crm.contacts.read",
    ]);
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page(ACCOUNTS),
      "GET /contacts": () => page(CONTACTS),
      "POST /cases": () => ({ id: "new-id" }),
      "PUT /cases/9": () => undefined,
    });
  });
  afterEach(clearSession);

  it("requires the subject and does not call the API without it", async () => {
    open();
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("creates an unassigned normal-priority case by default and opens its detail page", async () => {
    open();
    await userEvent.type(screen.getByRole("textbox", { name: /Konu/ }), "Fatura hatalı");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(lastPost("/cases")).toEqual({
      subject: "Fatura hatalı",
      priority: "normal",
      channel: "other",
    });
    expect(onSaved).toHaveBeenCalledWith("new-id");
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("/app/cases/new-id")
    );
  });

  it("sends the chosen account, contact, priority, channel and assignee", async () => {
    open();
    await userEvent.type(screen.getByRole("textbox", { name: /Konu/ }), "Kargo gecikti");
    await userEvent.type(screen.getByRole("textbox", { name: "Açıklama" }), "3 gündür yok");
    await pick("Müşteri", "Acme A.Ş.");
    await pick("Kişi", "Ayşe Yılmaz");
    await pick("Öncelik", "Acil");
    await pick("Kanal", "Telefon");
    await pick("Atanan", "Grace Hopper");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(lastPost("/cases")).toEqual({
      subject: "Kargo gecikti",
      description: "3 gündür yok",
      accountId: "a1",
      contactId: "c1",
      priority: "urgent",
      channel: "phone",
      assignedUserId: "user-2",
    });
  });

  it("fills an empty account from the chosen contact", async () => {
    open();
    await userEvent.type(screen.getByRole("textbox", { name: /Konu/ }), "X");
    await pick("Kişi", "Ayşe Yılmaz");

    await waitFor(() => expect(screen.getByRole("combobox", { name: "Müşteri" })).toHaveValue("Acme A.Ş."));
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(lastPost("/cases")).toMatchObject({ accountId: "a1", contactId: "c1" });
  });

  it("narrows the contacts to the chosen account (server-side accountId filter)", async () => {
    open();
    await pick("Müşteri", "Globex");
    await userEvent.click(screen.getByRole("combobox", { name: "Kişi" }));

    await waitFor(() =>
      expect(
        client.get.mock.calls.some(
          ([url, config]) => url === "/contacts" && config?.params?.accountId === "a2"
        )
      ).toBe(true)
    );
  });

  it("puts the server validation errors on their fields", async () => {
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page(ACCOUNTS),
      "GET /contacts": () => page(CONTACTS),
      "POST /cases": () =>
        problem(400, { code: "validation", errors: { Subject: ["Konu çok uzun"] } }),
    });
    open();
    await userEvent.type(screen.getByRole("textbox", { name: /Konu/ }), "X");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Konu çok uzun")).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("maps case.contact_account_mismatch onto the contact field", async () => {
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page(ACCOUNTS),
      "GET /contacts": () => page(CONTACTS),
      "POST /cases": () => problem(400, { code: "case.contact_account_mismatch" }),
    });
    open();
    await userEvent.type(screen.getByRole("textbox", { name: /Konu/ }), "X");
    await pick("Kişi", "Bora Kaya");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(
      await screen.findByText("Seçilen kişi, seçilen müşteriye ait değil")
    ).toBeInTheDocument();
  });

  it("maps case.account_not_found and owner.not_member onto their fields", async () => {
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => page(ACCOUNTS),
      "GET /contacts": () => page(CONTACTS),
      "POST /cases": () => problem(404, { code: "case.account_not_found" }),
    });
    open();
    await userEvent.type(screen.getByRole("textbox", { name: /Konu/ }), "X");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));
    expect(await screen.findByText("Seçilen müşteri bulunamadı")).toBeInTheDocument();
  });

  it("locks a preset account (Talep aç from an account) and still creates the case", async () => {
    open({ fixedAccount: { id: "a1", name: "Acme A.Ş." } });

    expect(screen.getByDisplayValue("Acme A.Ş.")).toBeDisabled();
    await userEvent.type(screen.getByRole("textbox", { name: /Konu/ }), "Destek");
    await userEvent.click(screen.getByRole("combobox", { name: "Kişi" }));
    await waitFor(() =>
      expect(
        client.get.mock.calls.some(
          ([url, config]) => url === "/contacts" && config?.params?.accountId === "a1"
        )
      ).toBe(true)
    );
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(lastPost("/cases")).toMatchObject({ subject: "Destek", accountId: "a1" });
  });

  it("locks a preset contact together with its account (Talep aç from a contact)", async () => {
    open({
      fixedContact: { id: "c1", name: "Ayşe Yılmaz", accountId: "a1", accountName: "Acme A.Ş." },
    });

    expect(screen.getByDisplayValue("Ayşe Yılmaz")).toBeDisabled();
    expect(screen.getByDisplayValue("Acme A.Ş.")).toBeDisabled();
    await userEvent.type(screen.getByRole("textbox", { name: /Konu/ }), "Destek");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(lastPost("/cases")).toMatchObject({ accountId: "a1", contactId: "c1" });
  });

  it("edits with a full replacement PUT and no priority or assignee", async () => {
    open({
      item: caseDetail("9", {
        subject: "Eski konu",
        description: "eski",
        accountId: "a1",
        accountName: "Acme A.Ş.",
        channel: "web",
      }),
    });

    expect(screen.queryByRole("combobox", { name: "Atanan" })).toBeNull();
    expect(screen.queryByRole("combobox", { name: "Öncelik" })).toBeNull();
    const subject = screen.getByRole("textbox", { name: /Konu/ });
    await userEvent.clear(subject);
    await userEvent.type(subject, "Yeni konu");
    await userEvent.clear(screen.getByRole("textbox", { name: "Açıklama" }));
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalled());
    expect(client.put.mock.calls[0]).toEqual([
      "/cases/9",
      { subject: "Yeni konu", accountId: "a1", channel: "web" },
    ]);
  });
});
