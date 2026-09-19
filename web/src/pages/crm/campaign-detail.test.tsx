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
import { campaign, member, metrics } from "@/test/campaigns";
import { toast, toastApiError } from "@/hooks/use-toast";
import type { Campaign, CampaignStatus } from "@/types";
import CampaignDetailPage from "./campaign-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const WRITE = ["crm.campaigns.read", "crm.campaigns.write"];

let current: Campaign;
let membersPage: ReturnType<typeof member>[];
let metricsBody: ReturnType<typeof metrics>;

function renderDetail(route = "/app/campaigns/c1") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/campaigns/:id" element={<CampaignDetailPage />} />
        <Route path="/app/campaigns" element={<div>list page</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const callsTo = (method: "get" | "post" | "put" | "delete", url: string) =>
  client[method].mock.calls.filter(([u]) => u === url);

describe("CampaignDetailPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    current = campaign("c1", {
      name: "Sonbahar E-posta",
      status: "active",
      startDate: "2026-09-01",
      budget: 50000,
      expectedRevenue: 200000,
      actualCost: 12000,
      description: "Sonbahar dönemi kampanyası",
      memberCount: 40,
    });
    membersPage = [member("m1", { status: "sent" })];
    metricsBody = metrics("c1");
    installApi(client, {
      "GET /campaigns/c1": () => current,
      "GET /campaigns/c1/metrics": () => metricsBody,
      "GET /campaigns/c1/members": () => page(membersPage),
      "GET /organization/members": () => MEMBERS,
      "GET /audit": () => ({ items: [], total: 0 }),
      "GET /leads": () =>
        page([
          { id: "l1", fullName: "Ayşe Yılmaz" },
          { id: "l2", fullName: "Can Demir" },
        ]),
      "GET /contacts": () => page([{ id: "k1", fullName: "Zeynep Kaya" }]),
      "POST /campaigns/c1/status": () => undefined,
      "POST /campaigns/c1/members": () => ({ addedCount: 1, alreadyMemberCount: 0, skipped: [] }),
      "POST /campaigns/c1/members/status": () => ({ updatedCount: 2, skippedCount: 0 }),
      "POST /campaigns/c1/members/remove": () => ({ removedCount: 1 }),
      "DELETE /campaigns/c1": () => undefined,
    });
  });
  afterEach(clearSession);

  describe("General tab", () => {
    it("shows the info panel and the metric cards", async () => {
      setPermissions(["crm.campaigns.read"]);
      renderDetail();

      expect(await screen.findByRole("heading", { name: "Sonbahar E-posta" })).toBeInTheDocument();
      expect(screen.getByText("Sonbahar dönemi kampanyası")).toBeInTheDocument();

      const members = await screen.findByTestId("metric-members");
      expect(within(members).getByTestId("metric-value")).toHaveTextContent("40");
      expect(members).toHaveTextContent("30 potansiyel, 10 kişi");
      expect(screen.getByTestId("metric-response-rate")).toHaveTextContent("46,67%");
      expect(screen.getByTestId("metric-response-rate")).toHaveTextContent("14 yanıt / 30 ulaşılan");
      expect(screen.getByTestId("metric-converted")).toHaveTextContent("6");
      expect(screen.getByTestId("metric-converted")).toHaveTextContent("Dönüşüm oranı 20%");
      expect(screen.getByTestId("metric-cost-per-lead")).toHaveTextContent(/400/);
      const breakdown = screen.getByTestId("metric-breakdown");
      expect(breakdown).toHaveTextContent("Eklendi: 10");
      expect(breakdown).toHaveTextContent("Gönderildi: 12");
      expect(breakdown).toHaveTextContent("Yanıtladı: 8");
      expect(breakdown).toHaveTextContent("Dönüştü: 6");
      expect(breakdown).toHaveTextContent("Ayrıldı: 4");
    });

    it("shows a dash for the cost per lead when the server omits it", async () => {
      metricsBody = metrics("c1", { costPerLead: undefined });
      setPermissions(["crm.campaigns.read"]);
      renderDetail();

      const card = await screen.findByTestId("metric-cost-per-lead");
      expect(within(card).getByTestId("metric-value")).toHaveTextContent("—");
    });

    it("shows an error with retry when the metrics fail", async () => {
      let failing = true;
      installApi(client, {
        "GET /campaigns/c1": () => current,
        "GET /campaigns/c1/metrics": () => (failing ? problem(500, { code: "unknown" }) : metricsBody),
      });
      setPermissions(["crm.campaigns.read"]);
      renderDetail();

      const retry = await screen.findByRole("button", { name: "Tekrar dene" });
      failing = false;
      await userEvent.click(retry);
      expect(await screen.findByTestId("metric-members")).toBeInTheDocument();
    });

    it("serves fresh metrics on the General tab after a member status change", async () => {
      setPermissions(WRITE);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");
      const before = callsTo("get", "/campaigns/c1/metrics").length;

      await userEvent.click(screen.getByRole("combobox", { name: "Üye m1 üyesinin durumu" }));
      await userEvent.click(await screen.findByRole("option", { name: "Yanıtladı" }));
      await waitFor(() => expect(callsTo("post", "/campaigns/c1/members/status")).toHaveLength(1));
      // The metrics query is not mounted on this tab, but its cache is invalidated: the General tab refetches.
      await userEvent.click(screen.getByRole("tab", { name: "Genel" }));
      await waitFor(() =>
        expect(callsTo("get", "/campaigns/c1/metrics").length).toBeGreaterThan(before)
      );
    });
  });

  describe("status menu", () => {
    const CASES: [CampaignStatus, string[]][] = [
      ["planned", ["Başlat", "İptal et"]],
      ["active", ["Tamamla", "İptal et"]],
      ["completed", ["Yeniden aç"]],
      ["cancelled", ["Yeniden planla"]],
    ];

    it.each(CASES)("offers only the valid targets from %s", async (status, labels) => {
      current = campaign("c1", { name: "Sonbahar E-posta", status });
      setPermissions(WRITE);
      renderDetail();
      await screen.findByRole("heading", { name: "Sonbahar E-posta" });

      await userEvent.click(screen.getByRole("button", { name: "Durumu değiştir" }));
      const items = (await screen.findAllByRole("menuitem")).map((item) => item.textContent);
      expect(items).toEqual(labels);
    });

    it("posts the chosen status and confirms", async () => {
      current = campaign("c1", { name: "Sonbahar E-posta", status: "planned" });
      setPermissions(WRITE);
      renderDetail();
      await screen.findByRole("heading", { name: "Sonbahar E-posta" });

      await userEvent.click(screen.getByRole("button", { name: "Durumu değiştir" }));
      await userEvent.click(await screen.findByRole("menuitem", { name: "Başlat" }));

      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith("/campaigns/c1/status", { status: "active" })
      );
      expect(toast).toHaveBeenCalledWith(
        expect.objectContaining({ description: "Kampanya durumu güncellendi" })
      );
    });

    it("turns campaign.invalid_status_transition (409) into an error toast", async () => {
      setPermissions(WRITE);
      renderDetail();
      await screen.findByRole("heading", { name: "Sonbahar E-posta" });
      client.post.mockRejectedValueOnce(
        problem(409, { code: "campaign.invalid_status_transition" })
      );

      await userEvent.click(screen.getByRole("button", { name: "Durumu değiştir" }));
      await userEvent.click(await screen.findByRole("menuitem", { name: "Tamamla" }));

      await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
      expect(toast).not.toHaveBeenCalled();
    });
  });

  describe("write permission", () => {
    it("hides every write control without crm.campaigns.write", async () => {
      current = campaign("c1", { name: "Sonbahar E-posta", status: "active" });
      membersPage = [member("m1", { status: "sent" })];
      setPermissions(["crm.campaigns.read", "crm.leads.read"]);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      expect(screen.queryByRole("button", { name: "Durumu değiştir" })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Düzenle" })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Sil" })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Üye ekle" })).not.toBeInTheDocument();
      expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
      // The status is a plain badge, not a selector.
      expect(screen.queryByRole("combobox", { name: /üyesinin durumu/ })).not.toBeInTheDocument();
      expect(screen.getAllByText("Gönderildi").length).toBeGreaterThan(0);
    });

    it("edits and deletes the campaign with write access", async () => {
      setPermissions(WRITE);
      renderDetail();
      await screen.findByRole("heading", { name: "Sonbahar E-posta" });

      await userEvent.click(screen.getByRole("button", { name: "Düzenle" }));
      expect(await screen.findByRole("dialog", { name: "Kampanyayı düzenle" })).toBeInTheDocument();
      await userEvent.click(screen.getByRole("button", { name: "Vazgeç" }));

      await userEvent.click(screen.getByRole("button", { name: "Sil" }));
      const dialog = await screen.findByRole("dialog", { name: "Kampanya silinsin mi?" });
      await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
      await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/campaigns/c1"));
      await waitFor(() =>
        expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/campaigns$/)
      );
    });
  });

  describe("Members tab", () => {
    it("lists members with links, the deleted-record marker and a locked converted status", async () => {
      membersPage = [
        member("m1", { memberName: "Ayşe Yılmaz", memberId: "l1", status: "sent" }),
        member("m2", { memberType: "contact", memberName: "Zeynep Kaya", memberId: "k1" }),
        member("m3", { memberName: undefined, memberMissing: true, status: "responded" }),
        member("m4", { memberName: "Can Demir", memberId: "l9", status: "converted" }),
      ];
      setPermissions([...WRITE, "crm.leads.read", "crm.contacts.read"]);
      renderDetail("/app/campaigns/c1?tab=members");

      expect(await screen.findByRole("link", { name: "Ayşe Yılmaz" })).toHaveAttribute(
        "href",
        "/app/leads/l1"
      );
      expect(screen.getByRole("link", { name: "Zeynep Kaya" })).toHaveAttribute(
        "href",
        "/app/contacts/k1"
      );
      // A deleted record is dimmed, not a link; the membership is still listed.
      expect(screen.getAllByText("Kayıt silinmiş").length).toBeGreaterThan(0);
      expect(screen.queryByRole("link", { name: "Kayıt silinmiş" })).not.toBeInTheDocument();
      // The converted member's selector is locked, the others are editable.
      expect(screen.getByRole("combobox", { name: "Can Demir üyesinin durumu" })).toBeDisabled();
      expect(screen.getByRole("combobox", { name: "Ayşe Yılmaz üyesinin durumu" })).toBeEnabled();
    });

    it("never offers converted as a status to set", async () => {
      setPermissions(WRITE);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      await userEvent.click(screen.getByRole("combobox", { name: "Üye m1 üyesinin durumu" }));
      const options = (await screen.findAllByRole("option")).map((o) => o.textContent);
      expect(options).toEqual(["Eklendi", "Gönderildi", "Yanıtladı", "Ayrıldı"]);
    });

    it("changes one member's status in place", async () => {
      setPermissions(WRITE);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      await userEvent.click(screen.getByRole("combobox", { name: "Üye m1 üyesinin durumu" }));
      await userEvent.click(await screen.findByRole("option", { name: "Ayrıldı" }));

      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith("/campaigns/c1/members/status", {
          memberIds: ["m1"],
          status: "unsubscribed",
        })
      );
    });

    it("adds several leads through the dialog and summarizes the result", async () => {
      client.post.mockImplementation(async (url: string, body: unknown) => {
        if (url === "/campaigns/c1/members") {
          expect(body).toEqual({ memberType: "lead", memberIds: ["l1", "l2"] });
          return {
            data: {
              addedCount: 3,
              alreadyMemberCount: 1,
              skipped: [
                { memberId: "x", reason: "lead_converted" },
                { memberId: "y", reason: "not_found" },
              ],
            },
          };
        }
        return { data: undefined };
      });
      setPermissions([...WRITE, "crm.leads.read", "crm.contacts.read"]);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      await userEvent.click(screen.getByRole("button", { name: "Üye ekle" }));
      const dialog = await screen.findByRole("dialog", { name: "Kampanyaya üye ekle" });
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Kayıtlar" }));
      await userEvent.click(await screen.findByRole("option", { name: "Ayşe Yılmaz" }));
      await userEvent.click(await screen.findByRole("option", { name: "Can Demir" }));
      await userEvent.click(within(dialog).getByRole("button", { name: "Ekle" }));

      await waitFor(() =>
        expect(toast).toHaveBeenCalledWith(
          expect.objectContaining({
            description: "3 eklendi, 1 zaten üyeydi, 2 atlandı (1 dönüşmüş, 1 bulunamadı)",
          })
        )
      );
    });

    it("adds contacts after switching the member type", async () => {
      setPermissions([...WRITE, "crm.leads.read", "crm.contacts.read"]);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      await userEvent.click(screen.getByRole("button", { name: "Üye ekle" }));
      const dialog = await screen.findByRole("dialog", { name: "Kampanyaya üye ekle" });
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Üye türü" }));
      await userEvent.click(await screen.findByRole("option", { name: "Kişi" }));
      await userEvent.click(within(dialog).getByRole("combobox", { name: "Kayıtlar" }));
      await userEvent.click(await screen.findByRole("option", { name: "Zeynep Kaya" }));
      await userEvent.click(within(dialog).getByRole("button", { name: "Ekle" }));

      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith("/campaigns/c1/members", {
          memberType: "contact",
          memberIds: ["k1"],
        })
      );
    });

    it("requires at least one record", async () => {
      setPermissions([...WRITE, "crm.leads.read"]);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      await userEvent.click(screen.getByRole("button", { name: "Üye ekle" }));
      const dialog = await screen.findByRole("dialog", { name: "Kampanyaya üye ekle" });
      await userEvent.click(within(dialog).getByRole("button", { name: "Ekle" }));

      expect(await within(dialog).findByText("En az bir kayıt seçin")).toBeInTheDocument();
      expect(callsTo("post", "/campaigns/c1/members")).toHaveLength(0);
    });

    it("changes the status of the selected rows in bulk (without converted) and reports skipped rows", async () => {
      membersPage = [member("m1"), member("m2"), member("m3", { status: "converted" })];
      client.post.mockImplementation(async (url: string) => ({
        data: url.endsWith("/members/status") ? { updatedCount: 2, skippedCount: 1 } : undefined,
      }));
      setPermissions(WRITE);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      await userEvent.click(screen.getByRole("checkbox", { name: "Sayfadaki tümünü seç" }));
      expect(await screen.findByTestId("member-bulk-bar")).toHaveTextContent("3 üye seçildi");
      await userEvent.click(screen.getByRole("button", { name: "Üye durumunu değiştir" }));
      const items = (await screen.findAllByRole("menuitem")).map((i) => i.textContent);
      expect(items).toEqual(["Eklendi", "Gönderildi", "Yanıtladı", "Ayrıldı"]);
      await userEvent.click(screen.getByRole("menuitem", { name: "Gönderildi" }));

      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith("/campaigns/c1/members/status", {
          memberIds: ["m1", "m2", "m3"],
          status: "sent",
        })
      );
      await waitFor(() =>
        expect(toast).toHaveBeenCalledWith(
          expect.objectContaining({
            description: "2 üyenin durumu güncellendi, 1 dönüşmüş üye atlandı",
          })
        )
      );
    });

    it("removes the selected members only after a confirmation", async () => {
      membersPage = [member("m1"), member("m2")];
      setPermissions(WRITE);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      await userEvent.click(screen.getByRole("checkbox", { name: "Üye m1 üyesini seç" }));
      await userEvent.click(screen.getByRole("button", { name: "Kampanyadan çıkar" }));
      const dialog = await screen.findByRole("dialog", { name: "Üyeler kampanyadan çıkarılsın mı?" });
      expect(within(dialog).getByText("Seçilen 1 üye kampanyadan çıkarılacak.")).toBeInTheDocument();
      expect(callsTo("post", "/campaigns/c1/members/remove")).toHaveLength(0);

      await userEvent.click(within(dialog).getByRole("button", { name: "Kampanyadan çıkar" }));
      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith("/campaigns/c1/members/remove", {
          memberIds: ["m1"],
        })
      );
    });

    it("removes a single member from the row action", async () => {
      setPermissions(WRITE);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      await userEvent.click(screen.getByRole("button", { name: "Üye m1 kampanyadan çıkar" }));
      const dialog = await screen.findByRole("dialog", { name: "Üyeler kampanyadan çıkarılsın mı?" });
      await userEvent.click(within(dialog).getByRole("button", { name: "Kampanyadan çıkar" }));
      await waitFor(() =>
        expect(client.post).toHaveBeenCalledWith("/campaigns/c1/members/remove", {
          memberIds: ["m1"],
        })
      );
    });

    it.each<CampaignStatus>(["completed", "cancelled"])(
      "disables adding members and explains why when the campaign is %s",
      async (status) => {
        current = campaign("c1", { name: "Sonbahar E-posta", status });
        setPermissions(WRITE);
        renderDetail("/app/campaigns/c1?tab=members");
        await screen.findByText("Üye m1");

        expect(screen.getByRole("button", { name: "Üye ekle" })).toBeDisabled();
        expect(
          screen.getByText("Tamamlanan veya iptal edilen kampanyaya üye eklenemez.")
        ).toBeInTheDocument();
        // Status changes and removal stay available on a closed campaign.
        expect(screen.getByRole("combobox", { name: "Üye m1 üyesinin durumu" })).toBeEnabled();
        expect(screen.getByRole("button", { name: "Üye m1 kampanyadan çıkar" })).toBeEnabled();
      }
    );

    it("filters by member type and status through the URL and the request", async () => {
      setPermissions(WRITE);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye m1");

      await userEvent.click(screen.getByRole("combobox", { name: "Üye türü" }));
      await userEvent.click(await screen.findByRole("option", { name: "Kişi" }));
      await waitFor(() => {
        const last = callsTo("get", "/campaigns/c1/members").at(-1)?.[1];
        expect(last.params).toMatchObject({ memberType: "contact", page: 1 });
      });

      await userEvent.click(screen.getByRole("combobox", { name: "Üye durumu" }));
      await userEvent.click(await screen.findByRole("option", { name: "Gönderildi" }));
      await userEvent.click(await screen.findByRole("option", { name: "Yanıtladı" }));
      await waitFor(() => {
        const last = callsTo("get", "/campaigns/c1/members").at(-1)?.[1];
        expect(last.params).toMatchObject({ memberType: "contact", status: "sent,responded" });
      });
      expect(screen.getByTestId("location")).toHaveTextContent("tab=members");
    });

    it("clears the selection when the page changes", async () => {
      client.get.mockImplementation(async (url: string, config?: { params?: Record<string, number> }) => {
        if (url === "/campaigns/c1") return { data: current };
        if (url === "/campaigns/c1/members") {
          return {
            data: page([member(`p${config?.params?.page}`)], { totalCount: 60, page: config?.params?.page }),
          };
        }
        if (url === "/organization/members") return { data: MEMBERS };
        return { data: {} };
      });
      setPermissions(WRITE);
      renderDetail("/app/campaigns/c1?tab=members");
      await screen.findByText("Üye p1");

      await userEvent.click(screen.getByRole("checkbox", { name: "Üye p1 üyesini seç" }));
      expect(await screen.findByTestId("member-bulk-bar")).toBeInTheDocument();
      await userEvent.click(screen.getByRole("button", { name: "2" }));
      await screen.findByText("Üye p2");

      expect(screen.queryByTestId("member-bulk-bar")).not.toBeInTheDocument();
      expect(screen.getByRole("checkbox", { name: "Üye p2 üyesini seç" })).not.toBeChecked();
    });
  });
});
