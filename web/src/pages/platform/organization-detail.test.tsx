import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { LocationDisplay, clearSession, installApi, page, problem, type MockClient } from "@/test/crm";
import { PLANS, orgDetail } from "@/test/platform";
import { toast, toastApiError } from "@/hooks/use-toast";
import type { PlatformDeletion, PlatformOrganizationDetail } from "@/types";
import OrganizationDetailPage from "./organization-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@mantine/charts", async () => (await import("@/test/charts")).chartMocks);
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const ID = "t1";

const DELETION: PlatformDeletion = {
  requestId: "d1",
  status: "scheduled",
  requestedAt: "2026-09-19T10:00:00Z",
  requestedByEmail: "ops@sense.com",
  reason: "Müşteri KVKK talebi",
  retentionDays: 30,
  scheduledFor: "2099-10-19T10:00:00Z",
  attempts: 0,
};

let org: PlatformOrganizationDetail;

function renderDetail(extra: Record<string, () => unknown> = {}, route = `/app/platform/organizations/${ID}`) {
  installApi(client, {
    [`GET /platform/organizations/${ID}`]: () => org,
    "GET /platform/plans": () => PLANS,
    [`GET /platform/organizations/${ID}/usage`]: () => ({ items: [] }),
    "GET /platform/audit": () => page([]),
    ...extra,
  });
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/platform/organizations/:tenantId" element={<OrganizationDetailPage />} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const button = (name: string | RegExp) => screen.queryByRole("button", { name });
const callsTo = (method: "get" | "post" | "put", url: string) =>
  client[method].mock.calls.filter(([u]) => u === url);

describe("Platform organization detail", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    org = orgDetail(ID, { name: "Acme A.Ş.", slug: "acme", status: "active", trialEndsOn: undefined });
  });
  afterEach(clearSession);

  describe("actions follow the state", () => {
    it("an active organization offers plan, suspend and deletion, not reactivate or cancel", async () => {
      renderDetail();
      expect(await screen.findByRole("heading", { name: "Acme A.Ş." })).toBeInTheDocument();
      expect(button("Planı / denemeyi düzenle")).toBeEnabled();
      expect(button("Askıya al")).toBeEnabled();
      expect(button("Silme talebi")).toBeEnabled();
      expect(button("Yeniden aç")).not.toBeInTheDocument();
      expect(button("Silme talebini iptal et")).not.toBeInTheDocument();
    });

    it("a suspended organization offers reactivate instead of suspend", async () => {
      org = orgDetail(ID, {
        name: "Acme A.Ş.",
        status: "suspended",
        accessLevel: "readOnly",
        suspension: { mode: "readOnly", reason: "Ödeme sorunu", at: "2026-09-10T08:00:00Z" },
      });
      renderDetail();
      await screen.findByRole("heading", { name: "Acme A.Ş." });
      expect(button("Yeniden aç")).toBeEnabled();
      expect(button("Askıya al")).not.toBeInTheDocument();
      expect(button("Silme talebi")).toBeEnabled();
      // The reason and mode are shown on the summary.
      expect(screen.getByText("Ödeme sorunu")).toBeInTheDocument();
      expect(screen.getByText(/Salt okunur askı/)).toBeInTheDocument();
    });

    it("a pending deletion offers only 'cancel deletion' while the request is scheduled", async () => {
      org = orgDetail(ID, {
        name: "Acme A.Ş.",
        status: "pending_deletion",
        accessLevel: "none",
        deletion: DELETION,
      });
      renderDetail();
      await screen.findByRole("heading", { name: "Acme A.Ş." });
      // (the same label is also on the Deletion tab, hence the header button is the first one)
      expect(button("Silme talebini iptal et")).toBeInTheDocument();
      expect(button("Askıya al")).not.toBeInTheDocument();
      expect(button("Yeniden aç")).not.toBeInTheDocument();
      expect(button("Silme talebi")).not.toBeInTheDocument();
      expect(button("Planı / denemeyi düzenle")).not.toBeInTheDocument();
    });

    it("a running or failed deletion cannot be cancelled and shows the error with the attempt count", async () => {
      org = orgDetail(ID, {
        name: "Acme A.Ş.",
        status: "pending_deletion",
        accessLevel: "none",
        deletion: { ...DELETION, status: "failed", attempts: 3, lastError: "Conductor yanıt vermedi" },
      });
      renderDetail({}, `/app/platform/organizations/${ID}?tab=deletion`);
      await screen.findByRole("heading", { name: "Acme A.Ş." });
      expect(button("Silme talebini iptal et")).not.toBeInTheDocument();
      const alert = await screen.findByRole("alert");
      expect(alert).toHaveTextContent("İmha tamamlanamadı");
      expect(alert).toHaveTextContent("Deneme sayısı: 3");
      expect(alert).toHaveTextContent("Conductor yanıt vermedi");
    });

    it("the system organization has every action disabled, with an explanation", async () => {
      org = orgDetail(ID, { name: "Sense Ops", status: "active", isSystem: true });
      renderDetail();
      await screen.findByRole("heading", { name: "Sense Ops" });
      const group = screen.getByTestId("system-actions");
      for (const name of ["Planı / denemeyi düzenle", "Askıya al", "Silme talebi"]) {
        expect(within(group).getByRole("button", { name })).toBeDisabled();
      }
      await userEvent.hover(group);
      expect(await screen.findByText(/işletim organizasyonudur/)).toBeInTheDocument();
    });

    it("shows the technical last error in monospace and offers 'retry erasure' only for a failed request", async () => {
      org = orgDetail(ID, {
        name: "Acme A.Ş.",
        status: "pending_deletion",
        accessLevel: "none",
        deletion: {
          ...DELETION,
          status: "failed",
          attempts: 2,
          lastError: "platform-tombstone: erasure.verification_failed: 3 rows left",
        },
      });
      renderDetail({}, `/app/platform/organizations/${ID}?tab=deletion`);
      await screen.findByRole("heading", { name: "Acme A.Ş." });
      const error = await screen.findByTestId("deletion-last-error");
      expect(error).toHaveTextContent("platform-tombstone: erasure.verification_failed: 3 rows left");
      expect(error.tagName).toBe("PRE");
      expect(button("İmhayı yeniden dene")).toBeInTheDocument();
    });

    it("has no retry button while the request is scheduled, running or completed", async () => {
      for (const status of ["scheduled", "running", "completed", "cancelled"] as const) {
        org = orgDetail(ID, {
          name: "Acme A.Ş.",
          status: "pending_deletion",
          accessLevel: "none",
          deletion: { ...DELETION, status },
        });
        const { unmount } = renderDetail({}, `/app/platform/organizations/${ID}?tab=deletion`);
        await screen.findByTestId("deletion-panel");
        expect(button("İmhayı yeniden dene"), status).not.toBeInTheDocument();
        unmount();
      }
    });

    describe("retry erasure", () => {
      beforeEach(() => {
        org = orgDetail(ID, {
          name: "Acme A.Ş.",
          status: "pending_deletion",
          accessLevel: "none",
          deletion: { ...DELETION, status: "failed", attempts: 1, lastError: "platform-tombstone: x" },
        });
      });

      async function openRetry(extra: Record<string, () => unknown>) {
        renderDetail(extra, `/app/platform/organizations/${ID}?tab=deletion`);
        await userEvent.click(await screen.findByRole("button", { name: "İmhayı yeniden dene" }));
        return screen.findByRole("dialog");
      }

      it("needs the caller's own password and posts it to /deletion-request/retry", async () => {
        const dialog = await openRetry({
          [`POST /platform/organizations/${ID}/deletion-request/retry`]: () => undefined,
        });
        const confirm = within(dialog).getByRole("button", { name: "İmhayı yeniden dene" });
        expect(confirm).toBeDisabled();
        expect(within(dialog).getByLabelText(/Parolanız/)).toHaveAttribute("autocomplete", "current-password");
        await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "Op3rator!pw");
        await userEvent.click(confirm);

        await waitFor(() =>
          expect(client.post).toHaveBeenCalledWith(`/platform/organizations/${ID}/deletion-request/retry`, {
            currentPassword: "Op3rator!pw",
          })
        );
        await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
        expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));
      });

      it("shows a wrong password inline and toasts 'not retryable'", async () => {
        let code = "platform.step_up_failed";
        const dialog = await openRetry({
          [`POST /platform/organizations/${ID}/deletion-request/retry`]: () =>
            problem(code === "platform.step_up_failed" ? 422 : 409, { code }),
        });
        await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "yanlis");
        const confirm = within(dialog).getByRole("button", { name: "İmhayı yeniden dene" });
        await userEvent.click(confirm);
        expect(await within(dialog).findByText("Parola hatalı")).toBeInTheDocument();
        expect(toastApiError).not.toHaveBeenCalled();

        code = "platform.deletion_not_retryable";
        await userEvent.click(confirm);
        await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });
    });

    it("shows a not-found page for an unknown organization", async () => {
      renderDetail({ [`GET /platform/organizations/${ID}`]: () => problem(404, { code: "not_found" }) });
      expect(await screen.findByText("Kayıt bulunamadı")).toBeInTheDocument();
    });
  });

  describe("summary", () => {
    it("shows effective limits and modules with overrides highlighted", async () => {
      org = orgDetail(ID, {
        name: "Acme A.Ş.",
        planCode: "starter",
        limits: {
          maxUsers: 10,
          maxRecords: { sales: 200000 },
          modules: { workflows: true, commerce: false, service: false, marketing: false },
        },
        overrides: { maxUsers: 10, maxRecords: { sales: 200000 }, modules: { workflows: true } },
      });
      renderDetail();
      await screen.findByRole("heading", { name: "Acme A.Ş." });

      const users = screen.getByTestId("limit-users");
      expect(users).toHaveTextContent("10");
      expect(users).toHaveTextContent("İstisna");
      expect(screen.getByTestId("limit-sales")).toHaveTextContent("200.000");
      expect(screen.getByTestId("limit-sales")).toHaveTextContent("İstisna");
      // No finite limit = unlimited, and no override badge.
      expect(screen.getByTestId("limit-service")).toHaveTextContent("Sınırsız");
      expect(screen.getByTestId("limit-service")).not.toHaveTextContent("İstisna");
      expect(screen.getByTestId("module-workflows")).toHaveTextContent("İş akışları: Dahil");
      expect(screen.getByTestId("module-commerce")).toHaveTextContent("Ticaret: Plana dahil değil");
    });
  });

  describe("suspend", () => {
    it("requires a reason, defaults to read-only and posts reason and mode", async () => {
      renderDetail({ [`POST /platform/organizations/${ID}/suspend`]: () => undefined });
      await userEvent.click(await screen.findByRole("button", { name: "Askıya al" }));
      const dialog = await screen.findByRole("dialog");

      expect(within(dialog).getByRole("radio", { name: /Salt okunur/ })).toBeChecked();
      await userEvent.click(within(dialog).getByRole("button", { name: "Askıya al" }));
      expect(await within(dialog).findByText("Gerekçe zorunludur")).toBeInTheDocument();
      expect(client.post).not.toHaveBeenCalled();

      await userEvent.type(within(dialog).getByLabelText(/Gerekçe/), "  Güvenlik olayı ");
      await userEvent.click(within(dialog).getByRole("radio", { name: /Tam engel/ }));
      // A full block asks for the calling admin's own password; the button stays off without it.
      const confirm = within(dialog).getByRole("button", { name: "Askıya al" });
      expect(confirm).toBeDisabled();
      await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "Op3rator!pw");
      await userEvent.click(confirm);

      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      expect(client.post).toHaveBeenCalledWith(`/platform/organizations/${ID}/suspend`, {
        reason: "Güvenlik olayı",
        mode: "blocked",
        currentPassword: "Op3rator!pw",
      });
      await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
      expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));
    });

    it("does not ask for a password for a read-only suspension and sends none", async () => {
      renderDetail({ [`POST /platform/organizations/${ID}/suspend`]: () => undefined });
      await userEvent.click(await screen.findByRole("button", { name: "Askıya al" }));
      const dialog = await screen.findByRole("dialog");

      expect(within(dialog).queryByLabelText(/Parolanız/)).not.toBeInTheDocument();
      await userEvent.type(within(dialog).getByLabelText(/Gerekçe/), "Ödeme gecikmesi");
      await userEvent.click(within(dialog).getByRole("button", { name: "Askıya al" }));

      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      expect(client.post).toHaveBeenCalledWith(`/platform/organizations/${ID}/suspend`, {
        reason: "Ödeme gecikmesi",
        mode: "readOnly",
      });
    });

    it("warns not to put personal data in the reason", async () => {
      renderDetail();
      await userEvent.click(await screen.findByRole("button", { name: "Askıya al" }));
      const dialog = await screen.findByRole("dialog");
      expect(within(dialog).getByText(/Gerekçeye kişisel veri \(ad, e-posta, telefon vb\.\) yazmayın/)).toBeInTheDocument();
    });

    it("shows a wrong step-up password inline on the password field, not as a toast", async () => {
      renderDetail({
        [`POST /platform/organizations/${ID}/suspend`]: () =>
          problem(422, { code: "platform.step_up_failed" }),
      });
      await userEvent.click(await screen.findByRole("button", { name: "Askıya al" }));
      const dialog = await screen.findByRole("dialog");
      await userEvent.type(within(dialog).getByLabelText(/Gerekçe/), "x");
      await userEvent.click(within(dialog).getByRole("radio", { name: /Tam engel/ }));
      await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "yanlis");
      await userEvent.click(within(dialog).getByRole("button", { name: "Askıya al" }));

      expect(await within(dialog).findByText("Parola hatalı")).toBeInTheDocument();
      expect(toastApiError).not.toHaveBeenCalled();
      expect(screen.getByRole("dialog")).toBeInTheDocument();
    });

    it("toasts a step-up rate limit (429) and keeps the dialog", async () => {
      renderDetail({
        [`POST /platform/organizations/${ID}/suspend`]: () =>
          problem(429, { code: "platform.step_up_rate_limited" }),
      });
      await userEvent.click(await screen.findByRole("button", { name: "Askıya al" }));
      const dialog = await screen.findByRole("dialog");
      await userEvent.type(within(dialog).getByLabelText(/Gerekçe/), "x");
      await userEvent.click(within(dialog).getByRole("radio", { name: /Tam engel/ }));
      await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "yanlis");
      await userEvent.click(within(dialog).getByRole("button", { name: "Askıya al" }));

      await waitFor(() => expect(toastApiError).toHaveBeenCalled());
      expect(screen.getByRole("dialog")).toBeInTheDocument();
    });

    it("toasts a 409 invalid transition and keeps the dialog", async () => {
      renderDetail({
        [`POST /platform/organizations/${ID}/suspend`]: () =>
          problem(409, { code: "platform.invalid_transition", args: { from: "suspended", to: "suspended" } }),
      });
      await userEvent.click(await screen.findByRole("button", { name: "Askıya al" }));
      const dialog = await screen.findByRole("dialog");
      await userEvent.type(within(dialog).getByLabelText(/Gerekçe/), "x");
      await userEvent.click(within(dialog).getByRole("button", { name: "Askıya al" }));

      await waitFor(() => expect(toastApiError).toHaveBeenCalled());
      expect(screen.getByRole("dialog")).toBeInTheDocument();
    });
  });

  describe("reactivate", () => {
    it("asks for confirmation and posts to /reactivate", async () => {
      org = orgDetail(ID, { name: "Acme A.Ş.", status: "suspended", accessLevel: "readOnly" });
      renderDetail({ [`POST /platform/organizations/${ID}/reactivate`]: () => undefined });
      await userEvent.click(await screen.findByRole("button", { name: "Yeniden aç" }));
      const dialog = await screen.findByRole("dialog");
      expect(client.post).not.toHaveBeenCalled();
      await userEvent.click(within(dialog).getByRole("button", { name: "Yeniden aç" }));

      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith(`/platform/organizations/${ID}/reactivate`)
      );
    });
  });

  describe("deletion request", () => {
    async function openDialog() {
      await userEvent.click(await screen.findByRole("button", { name: "Silme talebi" }));
      return screen.findByRole("dialog");
    }

    it("warns about permanent destruction and only enables the button once the name is typed exactly", async () => {
      renderDetail({
        [`POST /platform/organizations/${ID}/deletion-request`]: () => ({
          requestId: "d9",
          scheduledFor: "2026-10-20T10:00:00Z",
        }),
      });
      const dialog = await openDialog();
      expect(within(dialog).getByText(/KALICI İMHA/)).toBeInTheDocument();
      const confirm = within(dialog).getByRole("button", { name: "Silme talebini başlat" });
      expect(confirm).toBeDisabled();

      await userEvent.type(within(dialog).getByLabelText(/Gerekçe/), "KVKK silme talebi");
      expect(confirm).toBeDisabled();
      const nameField = within(dialog).getByLabelText(/Onaylamak için organizasyon adını yazın/);
      await userEvent.type(nameField, "acme a.ş.");
      expect(confirm).toBeDisabled();
      await userEvent.clear(nameField);
      await userEvent.type(nameField, "Acme A.Ş.");
      // The password is required too.
      expect(confirm).toBeDisabled();
      await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "Op3rator!pw");
      expect(confirm).toBeEnabled();

      await userEvent.click(confirm);
      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      expect(client.post).toHaveBeenCalledWith(`/platform/organizations/${ID}/deletion-request`, {
        reason: "KVKK silme talebi",
        retentionDays: 30,
        confirmTenantName: "Acme A.Ş.",
        currentPassword: "Op3rator!pw",
      });
    });

    it("warns not to put personal data in the reason", async () => {
      renderDetail();
      const dialog = await openDialog();
      expect(within(dialog).getByText(/Gerekçeye kişisel veri \(ad, e-posta, telefon vb\.\) yazmayın/)).toBeInTheDocument();
    });

    it("maps step-up and confirmation errors onto their fields and toasts the rest", async () => {
      let code = "platform.step_up_failed";
      renderDetail({
        [`POST /platform/organizations/${ID}/deletion-request`]: () => problem(422, { code }),
      });
      const dialog = await openDialog();
      await userEvent.type(within(dialog).getByLabelText(/Gerekçe/), "neden");
      await userEvent.type(within(dialog).getByLabelText(/Onaylamak için/), "Acme A.Ş.");
      await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "yanlis");
      const confirm = within(dialog).getByRole("button", { name: "Silme talebini başlat" });

      await userEvent.click(confirm);
      expect(await within(dialog).findByText("Parola hatalı")).toBeInTheDocument();

      code = "platform.confirmation_mismatch";
      await userEvent.click(confirm);
      expect(await within(dialog).findByText("Yazılan organizasyon adı eşleşmiyor")).toBeInTheDocument();
      expect(within(dialog).queryByText("Parola hatalı")).not.toBeInTheDocument();

      expect(toastApiError).not.toHaveBeenCalled();
      code = "platform.step_up_rate_limited";
      await userEvent.click(confirm);
      await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
    });

    it("keeps the retention period within 7 and 90 days", async () => {
      renderDetail({
        [`POST /platform/organizations/${ID}/deletion-request`]: () => ({
          requestId: "d9",
          scheduledFor: "2026-10-20T10:00:00Z",
        }),
      });
      const dialog = await openDialog();
      await userEvent.type(within(dialog).getByLabelText(/Gerekçe/), "neden");
      await userEvent.type(within(dialog).getByLabelText(/Onaylamak için/), "Acme A.Ş.");
      await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "pw");
      const days = within(dialog).getByLabelText(/Bekleme süresi/);
      const confirm = within(dialog).getByRole("button", { name: "Silme talebini başlat" });

      for (const bad of ["6", "91", "0"]) {
        await userEvent.clear(days);
        await userEvent.type(days, bad);
        expect(confirm).toBeDisabled();
        expect(within(dialog).getByText("7 ile 90 arasında bir tam sayı girin")).toBeInTheDocument();
      }
      for (const good of ["7", "90"]) {
        await userEvent.clear(days);
        await userEvent.type(days, good);
        expect(confirm).toBeEnabled();
      }
      await userEvent.click(confirm);
      await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
      expect(client.post.mock.calls[0]?.[1]).toEqual({
        reason: "neden",
        retentionDays: 90,
        confirmTenantName: "Acme A.Ş.",
        currentPassword: "pw",
      });
    });

    it("puts a server retentionDays error on the field", async () => {
      renderDetail({
        [`POST /platform/organizations/${ID}/deletion-request`]: () =>
          problem(400, { code: "validation", errors: { RetentionDays: ["Saklama süresi 7-90 olmalı"] } }),
      });
      const dialog = await openDialog();
      await userEvent.type(within(dialog).getByLabelText(/Gerekçe/), "neden");
      await userEvent.type(within(dialog).getByLabelText(/Onaylamak için/), "Acme A.Ş.");
      await userEvent.type(within(dialog).getByLabelText(/Parolanız/), "pw");
      await userEvent.click(within(dialog).getByRole("button", { name: "Silme talebini başlat" }));
      expect(await within(dialog).findByText("Saklama süresi 7-90 olmalı")).toBeInTheDocument();
    });

    it("shows the retention window on the Deletion tab and cancels a scheduled request after confirming", async () => {
      org = orgDetail(ID, {
        name: "Acme A.Ş.",
        status: "pending_deletion",
        accessLevel: "none",
        deletion: DELETION,
      });
      renderDetail(
        { [`POST /platform/organizations/${ID}/deletion-request/cancel`]: () => undefined },
        `/app/platform/organizations/${ID}?tab=deletion`
      );
      const panel = await screen.findByTestId("deletion-panel");
      expect(panel).toHaveTextContent("Planlandı");
      expect(panel).toHaveTextContent("Saklama süresi 30 gün");
      expect(screen.getByText("Müşteri KVKK talebi")).toBeInTheDocument();
      expect(screen.getByText("ops@sense.com")).toBeInTheDocument();

      await userEvent.click(within(panel).getByRole("button", { name: "Silme talebini iptal et" }));
      const dialog = await screen.findByRole("dialog");
      expect(client.post).not.toHaveBeenCalled();
      await userEvent.click(within(dialog).getByRole("button", { name: "Silme talebini iptal et" }));
      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith(`/platform/organizations/${ID}/deletion-request/cancel`)
      );
    });
  });

  describe("plan / trial editor", () => {
    async function openEditor() {
      await userEvent.click(await screen.findByRole("button", { name: "Planı / denemeyi düzenle" }));
      return screen.findByRole("dialog");
    }

    it("PUTs the full replacement: plan, trial day and only the overridden fields", async () => {
      renderDetail({ [`PUT /platform/organizations/${ID}/subscription`]: () => ({ overLimit: [] }) });
      const dialog = await openEditor();

      await userEvent.click(within(dialog).getByRole("combobox", { name: "Plan" }));
      await userEvent.click(await screen.findByRole("option", { name: "Business" }));
      await userEvent.type(within(dialog).getByLabelText("Deneme bitiş günü"), "2026-12-31");

      // maxUsers: custom 10; sales records: unlimited; marketing module on.
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Kullanıcı limiti" }));
      await userEvent.click(await screen.findByRole("option", { name: "Özel" }));
      await userEvent.type(within(dialog).getByRole("textbox", { name: "Kullanıcı limiti değeri" }), "10");
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Satış kayıt limiti" }));
      await userEvent.click(await screen.findByRole("option", { name: "Sınırsız" }));
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Pazarlama modülü" }));
      await userEvent.click(await screen.findByRole("option", { name: "Açık" }));

      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
      await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
      expect(client.put).toHaveBeenCalledWith(`/platform/organizations/${ID}/subscription`, {
        planCode: "business",
        trialEndsOn: "2026-12-31",
        overrides: { maxUsers: 10, maxRecords: { sales: null }, modules: { marketing: true } },
      });
      await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    });

    it("starts from the current state and 'remove trial' clears the trial day; no overrides = none sent", async () => {
      org = orgDetail(ID, {
        name: "Acme A.Ş.",
        status: "trial",
        trialEndsOn: "2026-10-04",
        overrides: { maxUsers: null },
      });
      renderDetail({ [`PUT /platform/organizations/${ID}/subscription`]: () => ({ overLimit: [] }) });
      const dialog = await openEditor();

      expect(within(dialog).getByRole("combobox", { name: "Plan" })).toHaveValue("Starter");
      expect(within(dialog).getByLabelText("Deneme bitiş günü")).toHaveValue("2026-10-04");
      expect(within(dialog).getByRole("combobox", { name: "Kullanıcı limiti" })).toHaveValue("Sınırsız");
      // The plan's own value is shown as a hint next to fields that follow the plan.
      expect(within(dialog).getAllByText("Plan: 10000").length).toBeGreaterThan(0);

      await userEvent.click(within(dialog).getByRole("button", { name: "Denemeyi kaldır" }));
      expect(within(dialog).getByLabelText("Deneme bitiş günü")).toHaveValue("");
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Kullanıcı limiti" }));
      await userEvent.click(await screen.findByRole("option", { name: "Plan değeri" }));
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

      await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
      expect(client.put.mock.calls[0]?.[1]).toEqual({ planCode: "starter" });
    });

    it("does not offer inactive plans other than the current one", async () => {
      renderDetail();
      const dialog = await openEditor();
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Plan" }));
      const options = (await screen.findAllByRole("option")).map((o) => o.textContent);
      expect(options).toEqual(["Ic kullanim", "Starter", "Business"]);
    });

    it("reports over-limit usage after a save and keeps the plan saved", async () => {
      renderDetail({
        [`PUT /platform/organizations/${ID}/subscription`]: () => ({
          overLimit: [
            { limit: "users", max: 5, used: 8 },
            { limit: "records", module: "sales", max: 5000, used: 6200 },
          ],
        }),
      });
      const dialog = await openEditor();
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

      const strip = await screen.findByTestId("over-limit");
      expect(strip).toHaveTextContent("Kullanım yeni limitin üstünde");
      expect(strip).toHaveTextContent("Kullanıcı: 8 / 5");
      expect(strip).toHaveTextContent("Satış kayıtları: 6.200 / 5.000");
      await userEvent.click(within(strip).getByRole("button", { name: "Kapat" }));
      expect(screen.queryByTestId("over-limit")).not.toBeInTheDocument();
    });

    it("maps platform.plan_not_found onto the plan field", async () => {
      renderDetail({
        [`PUT /platform/organizations/${ID}/subscription`]: () =>
          problem(404, { code: "platform.plan_not_found" }),
      });
      const dialog = await openEditor();
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

      expect(await within(dialog).findByText("Plan bulunamadı veya pasif")).toBeInTheDocument();
      expect(screen.getByRole("dialog")).toBeInTheDocument();
      expect(toastApiError).not.toHaveBeenCalled();
    });

    it("maps validation errors onto trial day, overrides and modules", async () => {
      renderDetail({
        [`PUT /platform/organizations/${ID}/subscription`]: () =>
          problem(400, {
            code: "validation",
            errors: {
              TrialEndsOn: ["Deneme bitişi geçmişte olamaz"],
              "Overrides.MaxUsers": ["Kullanıcı limiti negatif olamaz"],
              "Overrides.MaxRecords.sales": ["Kayıt limiti geçersiz"],
              "Overrides.Modules.marketing": ["Modül anahtarı geçersiz"],
            },
          }),
      });
      const dialog = await openEditor();
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

      expect(await within(dialog).findByText("Deneme bitişi geçmişte olamaz")).toBeInTheDocument();
      expect(within(dialog).getByText("Kullanıcı limiti negatif olamaz")).toBeInTheDocument();
      expect(within(dialog).getByText("Kayıt limiti geçersiz")).toBeInTheDocument();
      expect(within(dialog).getByText("Modül anahtarı geçersiz")).toBeInTheDocument();
      expect(toastApiError).not.toHaveBeenCalled();
    });

    it("toasts anything else (system tenant protected) and keeps the dialog open", async () => {
      renderDetail({
        [`PUT /platform/organizations/${ID}/subscription`]: () =>
          problem(422, { code: "platform.system_tenant_protected" }),
      });
      const dialog = await openEditor();
      await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
      await waitFor(() => expect(toastApiError).toHaveBeenCalled());
      expect(screen.getByRole("dialog")).toBeInTheDocument();
    });

    it("blocks saving while a custom limit is empty or negative", async () => {
      renderDetail();
      const dialog = await openEditor();
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Kullanıcı limiti" }));
      await userEvent.click(await screen.findByRole("option", { name: "Özel" }));
      expect(within(dialog).getByRole("button", { name: "Kaydet" })).toBeDisabled();
      await userEvent.type(within(dialog).getByRole("textbox", { name: "Kullanıcı limiti değeri" }), "0");
      expect(within(dialog).getByRole("button", { name: "Kaydet" })).toBeEnabled();
    });
  });

  describe("usage tab", () => {
    const DAYS = [
      { day: "2026-09-18", usersActive: 2, usersPending: 0, metrics: { "sales.records": 300, "sales.accounts": 100 } },
      { day: "2026-09-19", usersActive: 3, usersPending: 1, metrics: { "sales.records": 340, "sales.accounts": 120 } },
    ];
    const usageRoute = `GET /platform/organizations/${ID}/usage`;
    const usageCalls = () => callsTo("get", `/platform/organizations/${ID}/usage`);
    const ROUTE = `/app/platform/organizations/${ID}?tab=usage`;

    it("requests the last 90 days, plots users and the chosen metric, and re-requests for another range", async () => {
      renderDetail({ [usageRoute]: () => ({ items: DAYS }) }, ROUTE);
      const chart = await screen.findByTestId("chart-line");
      expect(JSON.parse(chart.getAttribute("data-points") ?? "[]")).toEqual([
        { day: expect.stringContaining("2026"), users: 2, metric: 300 },
        { day: expect.stringContaining("2026"), users: 3, metric: 340 },
      ]);
      const first = usageCalls().at(-1)?.[1].params as { from: string };
      expect(first.from).toMatch(/^\d{4}-\d{2}-\d{2}$/);

      await userEvent.click(screen.getByRole("radio", { name: "30 gün" }));
      await waitFor(() => expect(usageCalls().length).toBeGreaterThan(1));
      const second = usageCalls().at(-1)?.[1].params as { from: string };
      expect(second.from > first.from).toBe(true);

      await userEvent.click(screen.getByRole("radio", { name: "365 gün" }));
      await waitFor(() => {
        const third = usageCalls().at(-1)?.[1].params as { from: string };
        expect(third.from < first.from).toBe(true);
      });
    });

    it("switches the metric and shows a table view", async () => {
      renderDetail({ [usageRoute]: () => ({ items: DAYS }) }, ROUTE);
      await screen.findByTestId("chart-line");

      await userEvent.click(screen.getByRole("combobox", { name: "Metrik" }));
      await userEvent.click(await screen.findByRole("option", { name: "Satış: Müşteri" }));
      const chart = screen.getByTestId("chart-line");
      expect(JSON.parse(chart.getAttribute("data-points") ?? "[]").map((p: { metric: number }) => p.metric)).toEqual([100, 120]);

      await userEvent.click(screen.getByRole("radio", { name: "Tablo" }));
      const rows = screen.getAllByRole("row");
      // header + 2 days, newest first
      expect(rows).toHaveLength(3);
      expect(within(rows[1] as HTMLElement).getByText("120")).toBeInTheDocument();
    });

    it("shows an empty state when there are no snapshots", async () => {
      renderDetail({ [usageRoute]: () => ({ items: [] }) }, ROUTE);
      expect(await screen.findByTestId("usage-empty")).toHaveTextContent("anlık görüntüsü yok");
      expect(screen.queryByTestId("chart-line")).not.toBeInTheDocument();
    });

    it("'Şimdi yenile' posts to usage/refresh and reloads the series", async () => {
      renderDetail(
        {
          [usageRoute]: () => ({ items: DAYS }),
          [`POST /platform/organizations/${ID}/usage/refresh`]: () => ({ items: [DAYS[1]] }),
        },
        ROUTE
      );
      await screen.findByTestId("chart-line");
      const before = usageCalls().length;
      await userEvent.click(screen.getByRole("button", { name: "Şimdi yenile" }));

      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith(`/platform/organizations/${ID}/usage/refresh`)
      );
      await waitFor(() => expect(usageCalls().length).toBeGreaterThan(before));
      expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));
    });

    it("toasts a failed refresh (409 for an organization in deletion)", async () => {
      renderDetail(
        {
          [usageRoute]: () => ({ items: DAYS }),
          [`POST /platform/organizations/${ID}/usage/refresh`]: () =>
            problem(409, { code: "platform.invalid_transition" }),
        },
        ROUTE
      );
      await screen.findByTestId("chart-line");
      await userEvent.click(screen.getByRole("button", { name: "Şimdi yenile" }));
      await waitFor(() => expect(toastApiError).toHaveBeenCalled());
    });
  });

  it("the Audit tab lists the organization's platform audit entries", async () => {
    renderDetail(
      {
        "GET /platform/audit": () =>
          page([
            {
              id: "a1",
              occurredAt: "2026-09-19T10:00:00Z",
              action: "subscription.changed",
              actorEmail: "ops@sense.com",
              targetTenantId: ID,
              targetTenantName: "Acme A.Ş.",
              details: { planCode: { old: "starter", new: "business" } },
            },
          ]),
      },
      `/app/platform/organizations/${ID}?tab=audit`
    );
    expect(await screen.findByText("Abonelik değişti")).toBeInTheDocument();
    expect(screen.getByText("ops@sense.com")).toBeInTheDocument();
    expect(callsTo("get", "/platform/audit").at(-1)?.[1].params).toMatchObject({ tenantId: ID, page: 1 });

    await userEvent.click(screen.getByRole("button", { name: "Göster" }));
    expect(screen.getByTestId("audit-details")).toHaveTextContent('"planCode"');
  });
});

describe("Platform organization storage limit (M8C)", () => {
  const putRoute = `PUT /platform/organizations/${ID}/subscription`;

  beforeEach(() => {
    vi.clearAllMocks();
    org = orgDetail(ID, { name: "Acme A.Ş.", slug: "acme", status: "active", trialEndsOn: undefined });
  });
  afterEach(clearSession);

  async function openEditor() {
    await userEvent.click(await screen.findByRole("button", { name: "Planı / denemeyi düzenle" }));
    return screen.findByRole("dialog");
  }

  it("the summary shows the effective storage limit in readable units, unlimited without one, and marks an override", async () => {
    org = orgDetail(ID, {
      name: "Acme A.Ş.",
      limits: {
        maxUsers: 5,
        maxStorageMb: 25600,
        maxRecords: {},
        modules: { workflows: false, commerce: false, service: false, marketing: false },
      },
      overrides: { maxStorageMb: 25600 },
    });
    const view = renderDetail();
    await screen.findByRole("heading", { name: "Acme A.Ş." });
    expect(screen.getByTestId("limit-storage")).toHaveTextContent("25 GB");
    expect(screen.getByTestId("limit-storage")).toHaveTextContent("İstisna");
    view.unmount();

    org = orgDetail(ID, { name: "Acme A.Ş." });
    renderDetail();
    await screen.findByRole("heading", { name: "Acme A.Ş." });
    expect(screen.getByTestId("limit-storage")).toHaveTextContent("Sınırsız");
    expect(screen.getByTestId("limit-storage")).not.toHaveTextContent("İstisna");
  });

  it("the editor PUTs overrides.maxStorageMb (custom number, or null for unlimited) and nothing when it follows the plan", async () => {
    renderDetail({ [putRoute]: () => ({ overLimit: [] }) });
    let dialog = await openEditor();

    await userEvent.click(within(dialog).getByRole("combobox", { name: "Depolama limiti (MB)" }));
    await userEvent.click(await screen.findByRole("option", { name: "Özel" }));
    await userEvent.type(within(dialog).getByRole("textbox", { name: "Depolama limiti (MB) değeri" }), "2048");
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put).toHaveBeenLastCalledWith(`/platform/organizations/${ID}/subscription`, {
      planCode: org.planCode,
      trialEndsOn: undefined,
      overrides: { maxStorageMb: 2048 },
    });
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    dialog = await openEditor();
    await userEvent.click(within(dialog).getByRole("combobox", { name: "Depolama limiti (MB)" }));
    await userEvent.click(await screen.findByRole("option", { name: "Sınırsız" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(2));
    expect(client.put.mock.calls[1]?.[1]).toMatchObject({ overrides: { maxStorageMb: null } });
  });

  it("starts from a stored override (the server keeps the JSON as sent)", async () => {
    org = orgDetail(ID, { name: "Acme A.Ş.", overrides: { maxStorageMb: 512 } });
    renderDetail({ [putRoute]: () => ({ overLimit: [] }) });
    const dialog = await openEditor();

    expect(within(dialog).getByRole("combobox", { name: "Depolama limiti (MB)" })).toHaveValue("Özel");
    expect(within(dialog).getByRole("textbox", { name: "Depolama limiti (MB) değeri" })).toHaveValue("512");
  });

  it("puts a server error of overrides.maxStorageMb on its field", async () => {
    renderDetail({
      [putRoute]: () =>
        problem(400, { code: "validation", errors: { "Overrides.MaxStorageMb": ["Üst sınırı aşıyor"] } }),
    });
    const dialog = await openEditor();
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
    expect(await within(dialog).findByText("Üst sınırı aşıyor")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("an over-limit report after a save words storage in bytes", async () => {
    renderDetail({
      [putRoute]: () => ({
        overLimit: [{ limit: "storage", module: "files", max: 1024 * 1024 * 1024, used: 3 * 1024 * 1024 * 1024 }],
      }),
    });
    const dialog = await openEditor();
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
    expect(await screen.findByTestId("over-limit")).toHaveTextContent("Depolama: 3 GB / 1 GB");
  });

  it("the usage tab formats files.storage_bytes as sizes in the table and plots MB in the chart", async () => {
    const days = [
      { day: "2026-09-18", usersActive: 2, usersPending: 0, metrics: { "files.storage_bytes": 1048576, "files.files": 3 } },
      { day: "2026-09-19", usersActive: 3, usersPending: 0, metrics: { "files.storage_bytes": 3145728, "files.files": 5 } },
    ];
    renderDetail(
      { [`GET /platform/organizations/${ID}/usage`]: () => ({ items: days }) },
      `/app/platform/organizations/${ID}?tab=usage`
    );
    const chart = await screen.findByTestId("chart-line");
    // The first metric alphabetically is files.files; choose the storage one.
    await userEvent.click(screen.getByRole("combobox", { name: "Metrik" }));
    await userEvent.click(await screen.findByRole("option", { name: "Dosyalar: Depolama" }));
    expect(JSON.parse(screen.getByTestId("chart-line").getAttribute("data-points") ?? "[]").map((p: { metric: number }) => p.metric)).toEqual([1, 3]);
    expect(chart).toBeDefined();

    await userEvent.click(screen.getByRole("radio", { name: "Tablo" }));
    const rows = screen.getAllByRole("row");
    expect(within(rows[1] as HTMLElement).getByText("3 MB")).toBeInTheDocument();
    expect(within(rows[2] as HTMLElement).getByText("1 MB")).toBeInTheDocument();
  });
});
