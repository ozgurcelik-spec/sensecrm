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
import { caseDetail, timelineItem } from "@/test/service";
import { toastApiError } from "@/hooks/use-toast";
import type { CaseDetail, CaseStatus } from "@/types";
import CaseDetailPage from "./case-detail";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const WRITE = ["crm.cases.read", "crm.cases.write"];

let current: CaseDetail;
let timelineHandler: (params: Record<string, unknown> | undefined) => unknown;

function install(overrides: Record<string, (r: never) => unknown> = {}) {
  installApi(client, {
    "GET /cases/1": () => current,
    "GET /cases/1/timeline": (r) => timelineHandler((r as { params?: Record<string, unknown> }).params),
    "GET /organization/members": () => MEMBERS,
    "POST /cases/1/status": () => undefined,
    "POST /cases/1/priority": () => undefined,
    "POST /cases/1/assign": () => undefined,
    "POST /cases/1/comments": () => ({ id: "cm1" }),
    "DELETE /cases/1": () => undefined,
    ...overrides,
  });
}

function renderDetail(route = "/app/cases/1") {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/app/cases/:id" element={<CaseDetailPage />} />
        <Route path="/app/cases" element={<div>cases list</div>} />
        <Route path="/app/accounts/:id" element={<div>account page</div>} />
      </Routes>
      <LocationDisplay />
    </>,
    { route }
  );
}

const post = (url: string) => client.post.mock.calls.filter(([u]) => u === url).at(-1)?.[1];

async function openStatusMenu() {
  await userEvent.click(await screen.findByRole("button", { name: /^Durum$/ }));
}

const menuLabels = () => screen.queryAllByRole("menuitem").map((el) => el.textContent);

describe("CaseDetailPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    current = caseDetail("1", {
      subject: "Fatura hatalı",
      accountId: "a1",
      accountName: "Acme A.Ş.",
      contactId: "c1",
      contactName: "Ayşe Yılmaz",
      priority: "high",
      firstResponseAt: undefined,
      slaState: "atRisk",
    });
    timelineHandler = () =>
      page([
        timelineItem("t1", { visibility: "public", body: "Merhaba, inceliyoruz" }),
        timelineItem("t2", {
          visibility: "internal",
          body: "Muhasebeye ilettim",
          actorName: "Grace Hopper",
        }),
        timelineItem("t3", { type: "created", actorName: "Ada Lovelace" }),
        timelineItem("t4", { type: "statusChanged", from: "new", to: "open" }),
        timelineItem("t5", {
          type: "statusChanged",
          from: "open",
          to: "resolved",
          note: "Fatura düzeltildi",
        }),
        timelineItem("t6", { type: "priorityChanged", from: "normal", to: "urgent" }),
        timelineItem("t7", { type: "assigned", toName: "Mert Kaya" }),
      ]);
    setPermissions([...WRITE, "crm.accounts.read", "crm.contacts.read"]);
    install();
  });
  afterEach(clearSession);

  it("shows the number, subject, badges and the info panel", async () => {
    renderDetail();
    expect(
      await screen.findByRole("heading", { name: "C-2026-0001 · Fatura hatalı" })
    ).toBeInTheDocument();
    expect(screen.getAllByText("Açık").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Yüksek").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Risk altında").length).toBeGreaterThan(0);

    expect(screen.getByRole("link", { name: "Acme A.Ş." })).toHaveAttribute(
      "href",
      "/app/accounts/a1"
    );
    expect(screen.getByRole("link", { name: "Ayşe Yılmaz" })).toHaveAttribute(
      "href",
      "/app/contacts/c1"
    );
    // No first response yet: "awaiting" with the target, and both SLA lines.
    expect(screen.getByText(/Bekleniyor — hedef/)).toBeInTheDocument();
    expect(screen.getByTestId("sla-line-firstResponse")).toHaveTextContent("hedef");
    expect(screen.getByTestId("sla-line-resolution")).toHaveTextContent("hedef");
    expect(screen.getByText("Fatura tutarı hatalı")).toBeInTheDocument();
  });

  it("marks a breached SLA line", async () => {
    current = caseDetail("1", {
      slaState: "breached",
      isSlaBreached: true,
      firstResponseBreached: true,
      firstResponseAt: undefined,
      firstResponseDueAt: "2026-09-19T09:00:00Z",
    });
    renderDetail();
    const line = await screen.findByTestId("sla-line-firstResponse");
    expect(line).toHaveAttribute("data-breached", "true");
    expect(screen.getByTestId("sla-line-resolution")).toHaveAttribute("data-breached", "false");
  });

  it("renders the merged timeline: comments, internal notes and events", async () => {
    renderDetail();
    const items = await screen.findAllByTestId("timeline-item");
    expect(items).toHaveLength(7);

    const publicComment = items[0] as HTMLElement;
    expect(publicComment).toHaveAttribute("data-visibility", "public");
    expect(within(publicComment).getByText("Merhaba, inceliyoruz")).toBeInTheDocument();
    expect(within(publicComment).queryByText("Dahili")).toBeNull();

    const internal = items[1] as HTMLElement;
    expect(internal).toHaveAttribute("data-visibility", "internal");
    expect(within(internal).getByText("Dahili")).toBeInTheDocument();
    expect(within(internal).getByText("Grace Hopper")).toBeInTheDocument();

    expect(items[2]).toHaveTextContent("talebi oluşturdu");
    expect(items[3]).toHaveTextContent("durumu değiştirdi: Yeni → Açık");
    expect(items[4]).toHaveTextContent("Açık → Çözüldü");
    expect(items[4]).toHaveTextContent("Not: Fatura düzeltildi");
    expect(items[5]).toHaveTextContent("önceliği değiştirdi: Normal → Acil");
    expect(items[6]).toHaveTextContent("atamayı değiştirdi: Atanmamış → Mert Kaya");
  });

  it("pages the timeline with 'Daha fazla göster' (50 per page)", async () => {
    timelineHandler = (params) =>
      params?.page === 2
        ? page([timelineItem("t99", { body: "Eski yorum" })], {
            page: 2,
            pageSize: 50,
            totalCount: 51,
          })
        : page([timelineItem("t1", { body: "Yeni yorum" })], {
            page: 1,
            pageSize: 50,
            totalCount: 51,
          });
    renderDetail();
    await screen.findByText("Yeni yorum");
    expect(client.get.mock.calls.find(([u]) => u === "/cases/1/timeline")?.[1].params).toEqual({
      page: 1,
      pageSize: 50,
    });

    await userEvent.click(screen.getByRole("button", { name: "Daha fazla göster" }));
    expect(await screen.findByText("Eski yorum")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Daha fazla göster" })).toBeNull();
  });

  describe("status menu offers only the state machine's transitions", () => {
    const CASES: [CaseStatus, Partial<CaseDetail>, string[]][] = [
      ["new", {}, ["Açık yap", "Beklemeye al", "Çöz", "Çözülmeden kapat"]],
      ["open", {}, ["Beklemeye al", "Çöz", "Çözülmeden kapat"]],
      ["pending", {}, ["Açık yap", "Çöz", "Çözülmeden kapat"]],
      ["resolved", { resolvedAt: "2026-09-19T10:00:00Z" }, ["Yeniden aç", "Kapat"]],
      [
        "closed",
        { resolvedAt: "2026-09-19T10:00:00Z", closedAt: new Date().toISOString() },
        ["Yeniden aç"],
      ],
    ];
    it.each(CASES)("%s", async (status, extra, expected) => {
      current = caseDetail("1", { status, ...extra });
      renderDetail();
      await openStatusMenu();
      expect(menuLabels()).toEqual(expected);
    });

    it("hides Reopen for a case closed more than 14 days ago", async () => {
      current = caseDetail("1", {
        status: "closed",
        closedAt: new Date(Date.now() - 20 * 24 * 60 * 60 * 1000).toISOString(),
      });
      renderDetail();
      expect(await screen.findByRole("button", { name: /^Durum$/ })).toBeDisabled();
    });
  });

  it("asks for a resolution note, refuses an empty one and posts the status with it", async () => {
    renderDetail();
    await openStatusMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: "Çöz" }));

    const dialog = await screen.findByRole("dialog", { name: "Talebi çöz" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Çöz" }));
    expect(within(dialog).getByText("Çözüm notu zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();

    // Whitespace only is still empty.
    await userEvent.type(within(dialog).getByRole("textbox", { name: /Çözüm notu/ }), "   ");
    await userEvent.click(within(dialog).getByRole("button", { name: "Çöz" }));
    expect(client.post).not.toHaveBeenCalled();

    await userEvent.type(within(dialog).getByRole("textbox", { name: /Çözüm notu/ }), "Fatura düzeltildi");
    await userEvent.click(within(dialog).getByRole("button", { name: "Çöz" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(client.post.mock.calls[0]?.[0]).toBe("/cases/1/status");
    expect(post("/cases/1/status")).toEqual({ status: "resolved", resolutionNote: "Fatura düzeltildi" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("closing without resolving also needs a note; closing a resolved case does not", async () => {
    renderDetail();
    await openStatusMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: "Çözülmeden kapat" }));
    const dialog = await screen.findByRole("dialog", { name: "Talebi çözülmeden kapat" });
    await userEvent.type(within(dialog).getByRole("textbox", { name: /Çözüm notu/ }), "Müşteri vazgeçti");
    await userEvent.click(within(dialog).getByRole("button", { name: "Çözülmeden kapat" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(post("/cases/1/status")).toEqual({ status: "closed", resolutionNote: "Müşteri vazgeçti" });
  });

  it("closes a resolved case straight away, without a note", async () => {
    current = caseDetail("1", { status: "resolved", resolvedAt: "2026-09-19T10:00:00Z" });
    renderDetail();
    await openStatusMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: "Kapat" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(post("/cases/1/status")).toEqual({ status: "closed" });
  });

  it("reopens and moves to pending without a note", async () => {
    current = caseDetail("1", { status: "resolved", resolvedAt: "2026-09-19T10:00:00Z" });
    const first = renderDetail();
    await openStatusMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: "Yeniden aç" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(post("/cases/1/status")).toEqual({ status: "open" });
    first.unmount();

    vi.clearAllMocks();
    current = caseDetail("1", { status: "open" });
    install();
    renderDetail();
    await openStatusMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: "Beklemeye al" }));
    await waitFor(() => expect(client.post).toHaveBeenCalled());
    expect(post("/cases/1/status")).toEqual({ status: "pending" });
  });

  it("changes the priority and the assignee (including Unassigned)", async () => {
    renderDetail();
    await userEvent.click(await screen.findByRole("combobox", { name: "Öncelik" }));
    await userEvent.click(await screen.findByRole("option", { name: "Acil" }));
    await waitFor(() => expect(post("/cases/1/priority")).toEqual({ priority: "urgent" }));

    await userEvent.click(screen.getByRole("combobox", { name: "Atanan" }));
    await userEvent.click(await screen.findByRole("option", { name: "Grace Hopper" }));
    await waitFor(() => expect(post("/cases/1/assign")).toEqual({ assignedUserId: "user-2" }));

    await userEvent.click(screen.getByRole("combobox", { name: "Atanan" }));
    await userEvent.click(await screen.findByRole("option", { name: "Atanmamış" }));
    await waitFor(() => expect(post("/cases/1/assign")).toEqual({ assignedUserId: null }));
  });

  it("disables edit, priority and assignee once the case is resolved", async () => {
    current = caseDetail("1", { status: "resolved", resolvedAt: "2026-09-19T10:00:00Z" });
    renderDetail();
    expect(await screen.findByRole("button", { name: "Düzenle" })).toBeDisabled();
    expect(screen.getByRole("combobox", { name: "Öncelik" })).toBeDisabled();
    expect(screen.getByRole("combobox", { name: "Atanan" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Sil" })).toBeEnabled();
  });

  it("deletes after a confirmation and returns to the list", async () => {
    renderDetail();
    await userEvent.click(await screen.findByRole("button", { name: "Sil" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/cases/1"));
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/app/cases"));
  });

  describe("reply box", () => {
    it("has nothing selected and a disabled Send until a type is chosen", async () => {
      renderDetail();
      const send = await screen.findByRole("button", { name: "Gönder" });
      const publicOption = screen.getByRole("radio", { name: "Herkese açık yanıt" });
      const internalOption = screen.getByRole("radio", { name: "Dahili not" });
      expect(publicOption).not.toBeChecked();
      expect(internalOption).not.toBeChecked();

      await userEvent.type(screen.getByRole("textbox", { name: "Yanıt metni" }), "Merhaba");
      expect(send).toBeDisabled();

      await userEvent.click(publicOption);
      expect(send).toBeEnabled();
    });

    it("posts a public reply with visibility=public and clears the box (and the choice)", async () => {
      renderDetail();
      await userEvent.click(await screen.findByRole("radio", { name: "Herkese açık yanıt" }));
      await userEvent.type(screen.getByRole("textbox", { name: "Yanıt metni" }), "  Merhaba  ");
      await userEvent.click(screen.getByRole("button", { name: "Gönder" }));

      await waitFor(() => expect(client.post).toHaveBeenCalled());
      expect(post("/cases/1/comments")).toEqual({ visibility: "public", body: "Merhaba" });
      await waitFor(() => expect(screen.getByRole("textbox", { name: "Yanıt metni" })).toHaveValue(""));
      expect(screen.getByRole("radio", { name: "Herkese açık yanıt" })).not.toBeChecked();
    });

    it("posts an internal note with visibility=internal; Ctrl+Enter sends", async () => {
      renderDetail();
      await userEvent.click(await screen.findByRole("radio", { name: "Dahili not" }));
      await userEvent.type(screen.getByRole("textbox", { name: "Yanıt metni" }), "Not{Control>}{Enter}{/Control}");

      await waitFor(() => expect(client.post).toHaveBeenCalled());
      expect(post("/cases/1/comments")).toEqual({ visibility: "internal", body: "Not" });
    });

    it("is replaced by a 'Talep kapalı' banner on a closed case", async () => {
      current = caseDetail("1", { status: "closed", closedAt: new Date().toISOString() });
      renderDetail();
      expect(await screen.findByText("Talep kapalı")).toBeInTheDocument();
      expect(screen.queryByRole("textbox", { name: "Yanıt metni" })).toBeNull();
      expect(screen.queryByRole("button", { name: "Gönder" })).toBeNull();
    });

    it("is still offered on a resolved case", async () => {
      current = caseDetail("1", { status: "resolved", resolvedAt: "2026-09-19T10:00:00Z" });
      renderDetail();
      expect(await screen.findByRole("button", { name: "Gönder" })).toBeInTheDocument();
    });

    it("shows the case.closed error under the box when the case was closed meanwhile", async () => {
      install({ "POST /cases/1/comments": () => problem(409, { code: "case.closed" }) });
      renderDetail();
      await userEvent.click(await screen.findByRole("radio", { name: "Herkese açık yanıt" }));
      await userEvent.type(screen.getByRole("textbox", { name: "Yanıt metni" }), "Merhaba");
      await userEvent.click(screen.getByRole("button", { name: "Gönder" }));

      expect(await screen.findByText("Talep kapalı; yorum eklenemez")).toBeInTheDocument();
    });
  });

  describe("server rejections", () => {
    async function pickResolvedTransition() {
      await openStatusMenu();
      await userEvent.click(screen.getByRole("menuitem", { name: "Beklemeye al" }));
    }
    const detailCalls = () => client.get.mock.calls.filter(([u]) => u === "/cases/1").length;

    it.each([
      ["case.reopen_window_expired", 409],
      ["case.not_active", 409],
      ["case.invalid_transition", 409],
      ["general.concurrency_conflict", 409],
    ])("%s shows an error toast and reloads the case", async (code, status) => {
      install({ "POST /cases/1/status": () => problem(status, { code }) });
      renderDetail();
      await screen.findByRole("heading", { name: /Fatura hatalı/ });
      const before = detailCalls();
      await pickResolvedTransition();

      await waitFor(() => expect(toastApiError).toHaveBeenCalled());
      await waitFor(() => expect(detailCalls()).toBeGreaterThan(before));
    });

    it("keeps the note dialog open on case.resolution_required", async () => {
      install({ "POST /cases/1/status": () => problem(400, { code: "case.resolution_required" }) });
      renderDetail();
      await openStatusMenu();
      await userEvent.click(screen.getByRole("menuitem", { name: "Çöz" }));
      const dialog = await screen.findByRole("dialog");
      await userEvent.type(within(dialog).getByRole("textbox", { name: /Çözüm notu/ }), "x");
      await userEvent.click(within(dialog).getByRole("button", { name: "Çöz" }));
      expect(
        await within(dialog).findByText(/çözüm notu girilmelidir/)
      ).toBeInTheDocument();
    });

    it("shows a not-found card when the case does not exist", async () => {
      install({ "GET /cases/1": () => problem(404, { code: "not_found" }) });
      renderDetail();
      expect(await screen.findByText("Kayıt bulunamadı")).toBeInTheDocument();
    });
  });

  describe("permission gates", () => {
    it("without crm.cases.write there are no actions and no reply box", async () => {
      setPermissions(["crm.cases.read", "crm.accounts.read"]);
      renderDetail();
      await screen.findByRole("heading", { name: /Fatura hatalı/ });
      expect(screen.queryByRole("button", { name: /^Durum$/ })).toBeNull();
      expect(screen.queryByRole("button", { name: "Düzenle" })).toBeNull();
      expect(screen.queryByRole("button", { name: "Sil" })).toBeNull();
      expect(screen.queryByRole("combobox", { name: "Öncelik" })).toBeNull();
      expect(screen.queryByRole("textbox", { name: "Yanıt metni" })).toBeNull();
      // Reading is unaffected.
      expect(await screen.findByText("Merhaba, inceliyoruz")).toBeInTheDocument();
    });

    it("shows account and contact as plain text without the matching read permissions", async () => {
      setPermissions(WRITE);
      renderDetail();
      await screen.findByRole("heading", { name: /Fatura hatalı/ });
      expect(screen.queryByRole("link", { name: "Acme A.Ş." })).toBeNull();
      expect(screen.getByText("Acme A.Ş.")).toBeInTheDocument();
    });
  });

  it("offers the audit tab for the case entity", async () => {
    install({
      "GET /audit": () => ({ items: [], total: 0 }),
    });
    renderDetail("/app/cases/1?tab=audit");
    await screen.findByRole("heading", { name: /Fatura hatalı/ });
    await waitFor(() =>
      expect(
        client.get.mock.calls.some(
          ([u, c]) => u === "/audit" && c?.params?.entityType === "Case" && c?.params?.entityId === "1"
        )
      ).toBe(true)
    );
  });
});
