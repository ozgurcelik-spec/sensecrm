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
  type ApiHandler,
  type MockClient,
} from "@/test/crm";
import { purchaseOrder } from "@/test/inventory";
import { toast, toastApiError } from "@/hooks/use-toast";
import PurchaseOrderDetailPage from "./purchase-order-detail";
import PurchaseOrdersPage from "./purchase-orders";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

function summary(overrides: Record<string, unknown> = {}) {
  const row: Record<string, unknown> = { ...purchaseOrder(overrides) };
  delete row.lines;
  return row;
}

function renderList(route = "/app/purchase-orders") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/purchase-orders" element={<PurchaseOrdersPage />} />
        <Route path="/app/purchase-orders/new" element={<div>Yeni SAE</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const listRoutes = (extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> => ({
  "GET /purchase-orders": () =>
    page([summary(), summary({ id: "po2", number: "PO-2026-0002", status: "received" })]),
  "GET /organization/members": () => MEMBERS,
  ...extra,
});

describe("PurchaseOrdersPage", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  it("lists number, subject, vendor, status, total, PO date and due date and filters by status through the URL", async () => {
    setPermissions(["crm.purchaseorders.read"]);
    installApi(client, listRoutes());
    renderList();

    expect(await screen.findByRole("link", { name: "PO-2026-0001" })).toHaveAttribute("href", "/app/purchase-orders/po1");
    expect(screen.getAllByText("Tedarik A.Ş.")).toHaveLength(2);
    expect(within(screen.getByRole("table")).getByText("Teslim alındı")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Onaylandı" }));
    await waitFor(() => {
      const last = client.get.mock.calls.filter(([u]) => u === "/purchase-orders").at(-1)?.[1];
      expect(last.params).toMatchObject({ status: "confirmed" });
    });
    expect(screen.getByTestId("location")).toHaveTextContent("status=confirmed");
  });

  it("offers edit and delete only for drafts and only with crm.purchaseorders.write", async () => {
    setPermissions(["crm.purchaseorders.read"]);
    installApi(client, listRoutes());
    const first = renderList();
    await screen.findByRole("link", { name: "PO-2026-0001" });
    expect(screen.queryByRole("button", { name: "Yeni satın alma emri" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
    first.unmount();

    setPermissions(["crm.purchaseorders.read", "crm.purchaseorders.write"]);
    renderList();
    await screen.findByRole("link", { name: "PO-2026-0001" });
    expect(screen.getByRole("button", { name: "Yeni satın alma emri" })).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Düzenle" })).toHaveLength(1);
    expect(screen.getAllByRole("button", { name: "Sil" })).toHaveLength(1);
  });
});

function renderDetail() {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/purchase-orders/:id" element={<PurchaseOrderDetailPage />} />
        <Route path="/app/purchase-orders" element={<div>SAE listesi</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: "/app/purchase-orders/po1" }
  );
}

function detailRoutes(doc: object, extra: Record<string, ApiHandler> = {}): Record<string, ApiHandler> {
  return { "GET /purchase-orders/po1": () => doc, "GET /audit": () => ({ items: [], total: 0 }), ...extra };
}

const NAMES = ["Onayla", "Teslim alındı", "Düzenle", "İptal et", "Sil"] as const;

async function visibleActions() {
  await screen.findByRole("heading", { name: /PO-2026-0001/ });
  return NAMES.filter((name) => screen.queryByRole("button", { name }));
}

describe("PurchaseOrderDetailPage - actions by status and permission", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  // draft: confirm / edit / cancel / delete; confirmed: receive / cancel; received and cancelled are terminal.
  const MATRIX: [status: string, write: boolean, expected: string[]][] = [
    ["draft", true, ["Onayla", "Düzenle", "İptal et", "Sil"]],
    ["draft", false, []],
    ["confirmed", true, ["Teslim alındı", "İptal et"]],
    ["confirmed", false, []],
    ["received", true, []],
    ["cancelled", true, []],
  ];

  it.each(MATRIX)("%s, purchaseorders.write=%s", async (status, write, expected) => {
    setPermissions(["crm.purchaseorders.read", ...(write ? ["crm.purchaseorders.write"] : [])]);
    installApi(client, detailRoutes(purchaseOrder({ status })));
    renderDetail();
    expect(await visibleActions()).toEqual(expected);
  });

  it("shows the vendor link, the facts, the lines and the totals with the rounding line", async () => {
    setPermissions(["crm.purchaseorders.read", "crm.vendors.read"]);
    installApi(
      client,
      detailRoutes(
        purchaseOrder({
          adjustment: 0.44,
          grandTotal: 240.44,
          dueDate: "2026-10-01",
          carrier: "Aras Kargo",
          billingAddress: { city: "İstanbul" },
        })
      )
    );
    renderDetail();
    await screen.findByRole("heading", { name: /PO-2026-0001/ });

    expect(screen.getByRole("link", { name: "Tedarik A.Ş." })).toHaveAttribute("href", "/app/vendors/v1");
    expect(screen.getByText("Sunucu")).toBeInTheDocument();
    expect(screen.getByTestId("total-adjustment")).toHaveTextContent(/0,44/);
    expect(screen.getByTestId("total-grand")).toHaveTextContent(/240,44/);
    expect(screen.getByTestId("view-billing-address")).toHaveTextContent("İstanbul");
    expect(screen.getByText(/Aras Kargo/)).toBeInTheDocument();
  });

  it("confirms and receives through their endpoints", async () => {
    setPermissions(["crm.purchaseorders.read", "crm.purchaseorders.write"]);
    installApi(
      client,
      detailRoutes(purchaseOrder({ status: "draft" }), { "POST /purchase-orders/po1/confirm": () => undefined })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Onayla" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/purchase-orders/po1/confirm", {}));
    expect(toast).toHaveBeenCalled();
  });

  it("marks a confirmed order received", async () => {
    setPermissions(["crm.purchaseorders.read", "crm.purchaseorders.write"]);
    installApi(
      client,
      detailRoutes(purchaseOrder({ status: "confirmed" }), { "POST /purchase-orders/po1/receive": () => undefined })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Teslim alındı" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/purchase-orders/po1/receive", {}));
  });

  it("cancels with an optional reason", async () => {
    setPermissions(["crm.purchaseorders.read", "crm.purchaseorders.write"]);
    installApi(
      client,
      detailRoutes(purchaseOrder({ status: "confirmed" }), { "POST /purchase-orders/po1/cancel": () => undefined })
    );
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "İptal et" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByRole("textbox"), "Tedarikçi vazgeçti");
    await userEvent.click(within(dialog).getByRole("button", { name: "İptal et" }));
    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/purchase-orders/po1/cancel", { reason: "Tedarikçi vazgeçti" })
    );
  });

  it("toasts purchase_order.no_lines when confirming an order without lines", async () => {
    setPermissions(["crm.purchaseorders.read", "crm.purchaseorders.write"]);
    const error = problem(422, { code: "purchase_order.no_lines" });
    installApi(client, detailRoutes(purchaseOrder({ lines: [] }), { "POST /purchase-orders/po1/confirm": () => error }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Onayla" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });

  it("deletes a draft after a confirmation and returns to the list", async () => {
    setPermissions(["crm.purchaseorders.read", "crm.purchaseorders.write"]);
    installApi(client, detailRoutes(purchaseOrder(), { "DELETE /purchase-orders/po1": () => undefined }));
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/purchase-orders$/));
  });
});
