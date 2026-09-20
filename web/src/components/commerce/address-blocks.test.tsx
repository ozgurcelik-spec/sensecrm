import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import {
  EMPTY_DOCUMENT_ADDRESS,
  fromAccountAddress,
  toDocumentAddressPayload,
  toDocumentAddressValues,
  type DocumentAddressValues,
} from "@/lib/document-address";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, setPermissions, type MockClient } from "@/test/crm";
import { AddressBlocks } from "./address-blocks";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const ISTANBUL: DocumentAddressValues = {
  street: "Atatürk Cd. 12",
  building: "Kat 3",
  city: "İstanbul",
  state: "İstanbul",
  postalCode: "34000",
  country: "Türkiye",
};

function Harness({
  initialBilling = EMPTY_DOCUMENT_ADDRESS,
  initialShipping = EMPTY_DOCUMENT_ADDRESS,
  accountId,
  canReadAccounts = false,
  onState,
}: {
  initialBilling?: DocumentAddressValues;
  initialShipping?: DocumentAddressValues;
  accountId?: string;
  canReadAccounts?: boolean;
  onState?: (billing: DocumentAddressValues, shipping: DocumentAddressValues) => void;
}) {
  const [billing, setBilling] = useState(initialBilling);
  const [shipping, setShipping] = useState(initialShipping);
  onState?.(billing, shipping);
  return (
    <AddressBlocks
      billing={billing}
      shipping={shipping}
      onBillingChange={setBilling}
      onShippingChange={setShipping}
      accountId={accountId}
      canReadAccounts={canReadAccounts}
      billingErrors={{ city: "Şehir sunucuda reddedildi" }}
    />
  );
}

const field = (block: "Faturalama Adresi" | "İletişim (Teslimat) Adresi", label: string) =>
  screen.getByLabelText(`${block} - ${label}`) as HTMLInputElement;

describe("AddressBlocks", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions([]);
  });
  afterEach(clearSession);

  it("renders the two blocks with country, building, street, city, state and postal code", () => {
    renderWithProviders(<Harness />);
    for (const block of ["Faturalama Adresi", "İletişim (Teslimat) Adresi"] as const) {
      for (const label of ["Ülke", "Daire / Ev No / Bina / Apartman adı", "Açık adres", "Şehir", "Eyalet / İl", "Posta kodu"]) {
        expect(field(block, label)).toBeInTheDocument();
      }
    }
  });

  it("shows a server error on its field (billingAddress.city -> the billing city input)", () => {
    renderWithProviders(<Harness />);
    expect(screen.getByText("Şehir sunucuda reddedildi")).toBeInTheDocument();
  });

  it("clears one block with 'Tümünü temizle' and leaves the other alone", async () => {
    renderWithProviders(<Harness initialBilling={ISTANBUL} initialShipping={{ ...ISTANBUL, city: "Ankara" }} />);

    await userEvent.click(screen.getByRole("button", { name: "Faturalama Adresi - Tümünü temizle" }));

    expect(field("Faturalama Adresi", "Şehir")).toHaveValue("");
    expect(field("Faturalama Adresi", "Açık adres")).toHaveValue("");
    expect(field("İletişim (Teslimat) Adresi", "Şehir")).toHaveValue("Ankara");
  });

  describe("Adres Kopyala", () => {
    it("copies billing to shipping", async () => {
      renderWithProviders(<Harness initialBilling={ISTANBUL} />);
      await userEvent.click(screen.getByRole("button", { name: "Adres Kopyala" }));
      await userEvent.click(await screen.findByRole("menuitem", { name: "Faturalama adresini teslimata kopyala" }));

      expect(field("İletişim (Teslimat) Adresi", "Şehir")).toHaveValue("İstanbul");
      expect(field("İletişim (Teslimat) Adresi", "Daire / Ev No / Bina / Apartman adı")).toHaveValue("Kat 3");
    });

    it("copies shipping to billing", async () => {
      renderWithProviders(<Harness initialShipping={{ ...ISTANBUL, city: "İzmir" }} />);
      await userEvent.click(screen.getByRole("button", { name: "Adres Kopyala" }));
      await userEvent.click(await screen.findByRole("menuitem", { name: "Teslimat adresini faturalamaya kopyala" }));

      expect(field("Faturalama Adresi", "Şehir")).toHaveValue("İzmir");
      expect(field("Faturalama Adresi", "Posta kodu")).toHaveValue("34000");
    });

    it("fetches the account's address into the billing block ('Müşteriden getir'), the building stays empty", async () => {
      setPermissions(["crm.accounts.read"]);
      installApi(client, {
        "GET /accounts/a1": () => ({
          id: "a1",
          name: "Acme Ltd",
          billingAddress: { street: "Cumhuriyet Cd. 5", city: "Bursa", state: "Bursa", postalCode: "16000", country: "Türkiye" },
        }),
      });
      renderWithProviders(<Harness accountId="a1" canReadAccounts initialBilling={{ ...ISTANBUL, building: "Eski bina" }} />);

      await userEvent.click(screen.getByRole("button", { name: "Adres Kopyala" }));
      const item = await screen.findByRole("menuitem", { name: "Müşteriden getir" });
      await waitFor(() => expect(item).not.toBeDisabled());
      await userEvent.click(item);

      expect(field("Faturalama Adresi", "Şehir")).toHaveValue("Bursa");
      expect(field("Faturalama Adresi", "Açık adres")).toHaveValue("Cumhuriyet Cd. 5");
      expect(field("Faturalama Adresi", "Daire / Ev No / Bina / Apartman adı")).toHaveValue("");
    });

    it("offers no account entry without an account or without crm.accounts.read (and asks for nothing)", async () => {
      installApi(client, {});
      renderWithProviders(<Harness accountId="a1" canReadAccounts={false} />);
      await userEvent.click(screen.getByRole("button", { name: "Adres Kopyala" }));
      await screen.findByRole("menuitem", { name: "Faturalama adresini teslimata kopyala" });
      expect(screen.queryByRole("menuitem", { name: "Müşteriden getir" })).not.toBeInTheDocument();
      expect(client.get).not.toHaveBeenCalled();
    });
  });
});

describe("document address helpers", () => {
  it("sends an empty block as nothing and a filled one trimmed, without blank lines", () => {
    expect(toDocumentAddressPayload(EMPTY_DOCUMENT_ADDRESS)).toBeUndefined();
    expect(toDocumentAddressPayload({ ...EMPTY_DOCUMENT_ADDRESS, city: "   " })).toBeUndefined();
    expect(toDocumentAddressPayload({ ...EMPTY_DOCUMENT_ADDRESS, city: " Ankara ", building: "Kat 2" })).toEqual({
      city: "Ankara",
      building: "Kat 2",
    });
  });

  it("round-trips values and maps the account address without a building", () => {
    expect(toDocumentAddressValues(undefined)).toEqual(EMPTY_DOCUMENT_ADDRESS);
    expect(toDocumentAddressValues({ city: "Ankara" }).city).toBe("Ankara");
    expect(fromAccountAddress({ street: "S", city: "C", state: "T", postalCode: "P", country: "K" })).toEqual({
      street: "S",
      building: "",
      city: "C",
      state: "T",
      postalCode: "P",
      country: "K",
    });
  });
});
