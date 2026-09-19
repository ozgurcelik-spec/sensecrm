import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { newLine, type LineDraft, type LineErrors } from "@/lib/commerce-lines";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, page, setPermissions, type MockClient } from "@/test/crm";
import { LineItemsGrid } from "./line-items-grid";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const product = (over: Record<string, unknown>) => ({
  id: "p1",
  name: "CRM Pro",
  code: "CRM",
  unitPrice: 100,
  currency: "TRY",
  taxRate: 20,
  isActive: true,
  createdAt: "2026-05-01T10:00:00Z",
  ...over,
});

const PRODUCTS = [
  product({}),
  product({ id: "p2", name: "Dolar Paketi", code: "USD1", currency: "USD", unitPrice: 5, taxRate: 0 }),
];

interface HarnessProps {
  initial?: LineDraft[];
  currency?: string;
  errors?: LineErrors;
  canPickProducts?: boolean;
}

function Harness({ initial, currency = "TRY", errors, canPickProducts = true }: HarnessProps) {
  const [lines, setLines] = useState<LineDraft[]>(initial ?? [newLine()]);
  return (
    <LineItemsGrid
      lines={lines}
      onChange={setLines}
      currency={currency}
      errors={errors}
      canPickProducts={canPickProducts}
    />
  );
}

const descriptions = () =>
  screen.getAllByLabelText(/^Açıklama \d+$/).map((input) => (input as HTMLInputElement).value);

/** Reference vectors {1, 3} of the plan: subtotal 85.22, discount 6.00, tax 15.34, total 94.56. */
const vectorLines = () => [
  newLine({ description: "Vektör 1", quantity: 3, unitPrice: 19.99, discountPercent: 10, taxRate: 20 }),
  newLine({ description: "Vektör 3", quantity: 2.5, unitPrice: 10.1, discountPercent: 0, taxRate: 18 }),
];

describe("LineItemsGrid", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, { "GET /products": () => page(PRODUCTS) });
    setPermissions(["crm.products.read"]);
  });
  afterEach(clearSession);

  it("computes the live totals card and the row totals from the reference vectors", () => {
    renderWithProviders(<Harness initial={vectorLines()} />);

    expect(screen.getAllByTestId("line-total").map((n) => n.textContent)).toEqual([
      expect.stringContaining("64,76"),
      expect.stringContaining("29,80"),
    ]);
    expect(screen.getByTestId("total-subtotal")).toHaveTextContent("85,22");
    expect(screen.getByTestId("total-discount")).toHaveTextContent("6,00");
    expect(screen.getByTestId("total-tax")).toHaveTextContent("15,34");
    expect(screen.getByTestId("total-grand")).toHaveTextContent("94,56");
  });

  it("updates the totals card as a value changes", async () => {
    renderWithProviders(
      <Harness initial={[newLine({ description: "Hizmet", quantity: 1, unitPrice: 100, taxRate: 20 })]} />
    );
    expect(screen.getByTestId("total-grand")).toHaveTextContent("120,00");

    const quantity = screen.getByLabelText("Adet 1");
    await userEvent.clear(quantity);
    await userEvent.type(quantity, "3");

    expect(screen.getByTestId("total-subtotal")).toHaveTextContent("300,00");
    expect(screen.getByTestId("total-tax")).toHaveTextContent("60,00");
    expect(screen.getByTestId("total-grand")).toHaveTextContent("360,00");

    // A decimal typed character by character ("2." on the way to "2.5") must not be lost.
    await userEvent.clear(quantity);
    await userEvent.type(quantity, "2.5");
    expect(quantity).toHaveValue("2.5");
    expect(screen.getByTestId("total-subtotal")).toHaveTextContent("250,00");
    expect(screen.getByTestId("total-grand")).toHaveTextContent("300,00");
  });

  it("adds, removes and moves rows", async () => {
    renderWithProviders(
      <Harness
        initial={[newLine({ description: "Birinci" }), newLine({ description: "İkinci" })]}
        canPickProducts={false}
      />
    );
    expect(descriptions().filter(Boolean)).toEqual(["Birinci", "İkinci"]);

    await userEvent.click(screen.getByRole("button", { name: "Aşağı taşı 1" }));
    expect(descriptions().filter(Boolean)).toEqual(["İkinci", "Birinci"]);

    await userEvent.click(screen.getByRole("button", { name: "Yukarı taşı 2" }));
    expect(descriptions().filter(Boolean)).toEqual(["Birinci", "İkinci"]);

    // The first row cannot move up, the last cannot move down.
    expect(screen.getByRole("button", { name: "Yukarı taşı 1" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Aşağı taşı 2" })).toBeDisabled();

    await userEvent.click(screen.getByRole("button", { name: "Satır ekle" }));
    expect(screen.getAllByTestId("line-row")).toHaveLength(3);

    await userEvent.click(screen.getByRole("button", { name: "Satırı sil 1" }));
    expect(screen.getAllByTestId("line-row")).toHaveLength(2);
    expect(descriptions().filter(Boolean)).toEqual(["İkinci"]);
  });

  it("fills description, unit price and tax rate from the chosen product and lets the user change them", async () => {
    renderWithProviders(<Harness initial={[newLine({ quantity: 2, unitPrice: 0 })]} />);

    await userEvent.click(screen.getByRole("combobox", { name: "Ürün 1" }));
    await userEvent.click(await screen.findByRole("option", { name: "CRM Pro (CRM)" }));

    expect(screen.getByLabelText("Açıklama 1")).toHaveValue("CRM Pro");
    expect(screen.getByLabelText("Birim fiyat 1")).toHaveValue("100");
    expect(screen.getByLabelText("KDV % 1")).toHaveValue("20");
    expect(screen.getByTestId("total-grand")).toHaveTextContent("240,00");

    const price = screen.getByLabelText("Birim fiyat 1");
    await userEvent.clear(price);
    await userEvent.type(price, "50");
    expect(screen.getByTestId("total-grand")).toHaveTextContent("120,00");
  });

  it("disables products whose currency differs from the document currency", async () => {
    renderWithProviders(<Harness currency="TRY" />);
    await userEvent.click(screen.getByRole("combobox", { name: "Ürün 1" }));

    const usd = await screen.findByRole("option", { name: /Dolar Paketi/ });
    expect(usd).toHaveAttribute("data-combobox-disabled", "true");
    const ok = screen.getByRole("option", { name: "CRM Pro (CRM)" });
    expect(ok).not.toHaveAttribute("data-combobox-disabled");
    // Only active products are requested, server-side searched.
    expect(client.get).toHaveBeenCalledWith(
      "/products",
      expect.objectContaining({ params: expect.objectContaining({ isActive: true }) })
    );
  });

  it("has no product column (and never asks for products) without crm.products.read", () => {
    setPermissions([]);
    renderWithProviders(<Harness canPickProducts={false} />);
    expect(screen.queryByRole("columnheader", { name: "Ürün" })).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Ürün 1" })).not.toBeInTheDocument();
    expect(client.get).not.toHaveBeenCalledWith("/products", expect.anything());
    expect(screen.getByLabelText("Açıklama 1")).toBeInTheDocument();
  });

  it("puts a server cell error (lines[1].quantity) on the right cell only", () => {
    renderWithProviders(
      <Harness
        initial={[newLine({ description: "A" }), newLine({ description: "B" })]}
        errors={{ 1: { quantity: "Adet geçersiz (sunucu)" } }}
        canPickProducts={false}
      />
    );
    const rows = screen.getAllByTestId("line-row");
    expect(within(rows[1] as HTMLElement).getByText("Adet geçersiz (sunucu)")).toBeInTheDocument();
    expect(within(rows[0] as HTMLElement).queryByText("Adet geçersiz (sunucu)")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Adet 2")).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText("Adet 1")).not.toHaveAttribute("aria-invalid", "true");
  });

  it("translates client error keys", () => {
    renderWithProviders(
      <Harness
        initial={[newLine({ description: "" })]}
        errors={{ 0: { description: "auth:validation.required" } }}
        canPickProducts={false}
      />
    );
    expect(screen.getByText("Bu alan zorunludur")).toBeInTheDocument();
  });
});
