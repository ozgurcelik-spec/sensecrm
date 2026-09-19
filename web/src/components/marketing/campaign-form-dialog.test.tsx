import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  ME_ID,
  MEMBERS,
  clearSession,
  installApi,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { campaign } from "@/test/campaigns";
import { toastApiError } from "@/hooks/use-toast";
import { CampaignFormDialog } from "./campaign-form-dialog";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const setValue = (label: string | RegExp, value: string) =>
  fireEvent.change(screen.getByLabelText(label), { target: { value } });

describe("CampaignFormDialog", () => {
  const onClose = vi.fn();
  const onSaved = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["crm.campaigns.read", "crm.campaigns.write"]);
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "POST /campaigns": () => campaign("new-id"),
      "PUT /campaigns/c1": () => undefined,
    });
  });
  afterEach(clearSession);

  const open = (props: Partial<Parameters<typeof CampaignFormDialog>[0]> = {}) =>
    renderWithProviders(<CampaignFormDialog onClose={onClose} onSaved={onSaved} {...props} />);

  it("requires the name and does not call the API when it is empty", async () => {
    open();

    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("blocks an end date before the start date on the client", async () => {
    open();

    setValue(/Kampanya adı/, "Sonbahar");
    setValue("Başlangıç tarihi", "2026-10-10");
    setValue("Bitiş tarihi", "2026-10-01");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(
      await screen.findByText("Bitiş tarihi, başlangıç tarihinden önce olamaz")
    ).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();

    // An equal end date is valid.
    setValue("Bitiş tarihi", "2026-10-10");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));
    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
  });

  it("rejects a negative amount", async () => {
    open();

    setValue(/Kampanya adı/, "Sonbahar");
    setValue("Bütçe", "-5");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Tutar sıfırdan küçük olamaz")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("creates the campaign with trimmed values, the current user as owner and a chosen initial status", async () => {
    open();

    setValue(/Kampanya adı/, "  Sonbahar E-posta ");
    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    // Only planned and active are offered on create.
    expect(screen.queryByRole("option", { name: "Tamamlandı" })).not.toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "İptal edildi" })).not.toBeInTheDocument();
    await userEvent.click(await screen.findByRole("option", { name: "Aktif" }));
    setValue("Bütçe", "50000");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post).toHaveBeenCalledWith("/campaigns", {
      name: "Sonbahar E-posta",
      type: "email",
      status: "active",
      currency: "TRY",
      budget: 50000,
      ownerUserId: ME_ID,
    });
    expect(onSaved).toHaveBeenCalledWith("new-id");
    expect(onClose).toHaveBeenCalled();
  });

  it("edits without a status field and does not send the status", async () => {
    open({
      campaign: campaign("c1", {
        name: "Eski ad",
        status: "completed",
        startDate: "2026-01-01",
        actualCost: 1200,
      }),
    });

    expect(screen.queryByRole("combobox", { name: "Durum" })).not.toBeInTheDocument();
    setValue(/Kampanya adı/, "Yeni ad");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    const [url, body] = client.put.mock.calls[0] as [string, Record<string, unknown>];
    expect(url).toBe("/campaigns/c1");
    expect(body).toMatchObject({ name: "Yeni ad", startDate: "2026-01-01", actualCost: 1200 });
    // `undefined` is dropped from the JSON body.
    expect(JSON.parse(JSON.stringify(body))).not.toHaveProperty("status");
  });

  it("puts campaign.invalid_date_range on the end date field", async () => {
    client.post.mockRejectedValueOnce(
      problem(400, { code: "campaign.invalid_date_range", errors: { endDate: ["server text"] } })
    );
    open();

    setValue(/Kampanya adı/, "Sonbahar");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(
      await screen.findByText("Bitiş tarihi, başlangıç tarihinden önce olamaz")
    ).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("puts owner.not_member on the owner field", async () => {
    client.post.mockRejectedValueOnce(problem(400, { code: "owner.not_member" }));
    open();

    setValue(/Kampanya adı/, "Sonbahar");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(
      await screen.findByText("Seçilen sahip bu organizasyonun aktif üyesi değil")
    ).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("maps server validation errors onto their fields", async () => {
    client.post.mockRejectedValueOnce(
      problem(400, { code: "validation", errors: { Name: ["Ad çok uzun"] } })
    );
    open();

    setValue(/Kampanya adı/, "Sonbahar");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Ad çok uzun")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("shows any other error as a toast", async () => {
    client.post.mockRejectedValueOnce(problem(500, { code: "unexpected" }));
    open();

    setValue(/Kampanya adı/, "Sonbahar");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
  });
});
