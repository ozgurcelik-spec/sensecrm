import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toast, toastApiError } from "@/hooks/use-toast";
import { ApprovalsBell } from "@/components/shell/approvals-bell";
import { renderWithProviders } from "@/test-utils";
import {
  LocationDisplay,
  clearSession,
  installApi,
  page,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { approval } from "@/test/workflows";
import ApprovalsPage from "./approvals";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const renderPage = (route = "/app/approvals") =>
  renderWithProviders(
    <>
      <ApprovalsBell />
      <ApprovalsPage />
      <LocationDisplay />
    </>,
    { route }
  );

const rowFor = (text: string) => screen.getByText(text).closest("tr") as HTMLElement;
const calls = (url: string) => client.get.mock.calls.filter(([u]) => u === url);
const lastListParams = () => calls("/approvals").at(-1)?.[1].params;

const PENDING = [
  approval("1", { title: "Fırsat onayı: Büyük anlaşma", subjectName: "Büyük anlaşma" }),
  approval("2", {
    title: "Fırsat onayı: Orta anlaşma",
    subjectName: "Orta anlaşma",
    amount: 90000,
  }),
];

describe("ApprovalsPage", () => {
  let pendingRows = PENDING;

  beforeEach(() => {
    vi.clearAllMocks();
    pendingRows = PENDING;
    setPermissions(["crm.approvals.decide", "crm.deals.read"]);
    installApi(client, {
      "GET /approvals": ({ params }) =>
        params?.["status"] === "pending"
          ? page(pendingRows)
          : page([
              approval("3", {
                title: "Fırsat onayı: Eski anlaşma",
                subjectName: "Eski anlaşma",
                status: "rejected",
                decidedAt: "2026-05-12T10:00:00Z",
                comment: "Bütçe uygun değil",
              }),
            ]),
      "GET /approvals/summary": () => ({ pendingCount: pendingRows.length }),
      "POST /approvals/1/decision": () => undefined,
    });
  });
  afterEach(clearSession);

  it("lists the caller's pending approvals with amount and a link to the deal", async () => {
    renderPage();
    await screen.findByText("Fırsat onayı: Büyük anlaşma");

    expect(lastListParams()).toEqual({ page: 1, pageSize: 25, mine: true, status: "pending" });
    const row = rowFor("Fırsat onayı: Büyük anlaşma");
    expect(within(row).getByRole("link", { name: "Büyük anlaşma" })).toHaveAttribute(
      "href",
      "/app/deals/deal-1"
    );
    expect(within(row).getByText(/250\.000/)).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: /onayını onayla/ })).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: /onayını reddet/ })).toBeInTheDocument();
  });

  it("shows plain text instead of a link when the record may not be read", async () => {
    setPermissions(["crm.approvals.decide"]);
    renderPage();
    await screen.findByText("Fırsat onayı: Büyük anlaşma");
    const row = rowFor("Fırsat onayı: Büyük anlaşma");
    expect(within(row).getByText("Büyük anlaşma")).toBeInTheDocument();
    expect(within(row).queryByRole("link")).not.toBeInTheDocument();
  });

  it("without crm.approvals.decide the list is read-only", async () => {
    setPermissions([]);
    renderPage();
    await screen.findByText("Fırsat onayı: Büyük anlaşma");
    expect(screen.queryByRole("button", { name: /onayını onayla/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /onayını reddet/ })).not.toBeInTheDocument();
  });

  it("shows the history with decision, comment and an optional status filter kept in the URL", async () => {
    renderPage("/app/approvals?tab=history");
    await screen.findByText("Fırsat onayı: Eski anlaşma");

    expect(lastListParams()).toEqual({ page: 1, pageSize: 25, mine: true });
    const row = rowFor("Fırsat onayı: Eski anlaşma");
    expect(within(row).getByText("Reddedildi")).toBeInTheDocument();
    expect(within(row).getByText("Bütçe uygun değil")).toBeInTheDocument();
    expect(within(row).queryByRole("button", { name: /onayını/ })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Onaylandı" }));
    await waitFor(() =>
      expect(lastListParams()).toEqual({ page: 1, pageSize: 25, mine: true, status: "approved" })
    );
    expect(screen.getByTestId("location")).toHaveTextContent(
      "/app/approvals?tab=history&status=approved"
    );

    await userEvent.click(screen.getByRole("tab", { name: "Bekleyenler" }));
    await screen.findByText("Fırsat onayı: Büyük anlaşma");
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/approvals$/);
  });

  it("approves without a comment and refreshes the list, the badge and the executions", async () => {
    renderPage();
    await screen.findByText("Fırsat onayı: Büyük anlaşma");
    await screen.findByRole("link", { name: "Bekleyen onaylar: 2" });
    const listCalls = calls("/approvals").length;
    const summaryCalls = calls("/approvals/summary").length;

    pendingRows = [PENDING[1]!];
    await userEvent.click(
      within(rowFor("Fırsat onayı: Büyük anlaşma")).getByRole("button", { name: /onayını onayla/ })
    );
    const dialog = await screen.findByRole("dialog", { name: "Onay kararı" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Onayla" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    // An empty comment is left out of the body.
    expect(client.post).toHaveBeenCalledWith("/approvals/1/decision", { decision: "approve" });
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));

    await waitFor(() => expect(calls("/approvals").length).toBeGreaterThan(listCalls));
    await waitFor(() => expect(calls("/approvals/summary").length).toBeGreaterThan(summaryCalls));
    await waitFor(() =>
      expect(screen.queryByText("Fırsat onayı: Büyük anlaşma")).not.toBeInTheDocument()
    );
    expect(await screen.findByRole("link", { name: "Bekleyen onaylar: 1" })).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("requires a comment to reject: nothing is sent until one is entered", async () => {
    renderPage();
    await screen.findByText("Fırsat onayı: Büyük anlaşma");

    await userEvent.click(
      within(rowFor("Fırsat onayı: Büyük anlaşma")).getByRole("button", { name: /onayını reddet/ })
    );
    const dialog = await screen.findByRole("dialog", { name: "Onay kararı" });
    const submit = within(dialog).getByRole("button", { name: "Reddet" });

    await userEvent.click(submit);
    expect(
      await within(dialog).findByText("Reddetmek için yorum girmelisiniz")
    ).toBeInTheDocument();
    // Whitespace does not count.
    await userEvent.type(within(dialog).getByRole("textbox", { name: /^Yorum/ }), "   ");
    await userEvent.click(submit);
    expect(within(dialog).getByText("Reddetmek için yorum girmelisiniz")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();

    await userEvent.type(within(dialog).getByRole("textbox", { name: /^Yorum/ }), "Bütçe yetersiz");
    // Typing clears the error.
    expect(within(dialog).queryByText("Reddetmek için yorum girmelisiniz")).not.toBeInTheDocument();
    await userEvent.click(submit);

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post).toHaveBeenCalledWith("/approvals/1/decision", {
      decision: "reject",
      comment: "Bütçe yetersiz",
    });
  });

  it("switching the decision in the dialog toggles the comment requirement", async () => {
    renderPage();
    await screen.findByText("Fırsat onayı: Büyük anlaşma");
    await userEvent.click(
      within(rowFor("Fırsat onayı: Büyük anlaşma")).getByRole("button", { name: /onayını reddet/ })
    );
    const dialog = await screen.findByRole("dialog", { name: "Onay kararı" });
    expect(within(dialog).queryByText("İsteğe bağlı")).not.toBeInTheDocument();

    await userEvent.click(within(dialog).getByRole("radio", { name: "Onayla" }));
    expect(within(dialog).getByText("İsteğe bağlı")).toBeInTheDocument();
  });

  it("puts a server comment validation error on the comment field", async () => {
    installApi(client, {
      "GET /approvals": () => page(PENDING),
      "GET /approvals/summary": () => ({ pendingCount: 2 }),
      "POST /approvals/1/decision": () =>
        problem(400, {
          code: "validation",
          errors: { Comment: ["Yorum en fazla 500 karakter olabilir"] },
        }),
    });
    renderPage();
    await screen.findByText("Fırsat onayı: Büyük anlaşma");
    await userEvent.click(
      within(rowFor("Fırsat onayı: Büyük anlaşma")).getByRole("button", { name: /onayını reddet/ })
    );
    const dialog = await screen.findByRole("dialog", { name: "Onay kararı" });
    await userEvent.type(within(dialog).getByRole("textbox", { name: /^Yorum/ }), "Uzun yorum");
    await userEvent.click(within(dialog).getByRole("button", { name: "Reddet" }));

    expect(
      await within(dialog).findByText("Yorum en fazla 500 karakter olabilir")
    ).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("handles approval.already_decided with a friendly message and refreshes the lists", async () => {
    installApi(client, {
      "GET /approvals": () => page(pendingRows),
      "GET /approvals/summary": () => ({ pendingCount: pendingRows.length }),
      "POST /approvals/1/decision": () => problem(409, { code: "approval.already_decided" }),
    });
    renderPage();
    await screen.findByText("Fırsat onayı: Büyük anlaşma");
    const listCalls = calls("/approvals").length;
    const summaryCalls = calls("/approvals/summary").length;

    // Somebody else decided in the meantime: the refreshed list no longer has it.
    pendingRows = [PENDING[1]!];
    await userEvent.click(
      within(rowFor("Fırsat onayı: Büyük anlaşma")).getByRole("button", { name: /onayını onayla/ })
    );
    const dialog = await screen.findByRole("dialog", { name: "Onay kararı" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Onayla" }));

    await waitFor(() =>
      expect(toast).toHaveBeenCalledWith({
        description: "Bu onay için zaten karar verilmiş. Liste yenilendi.",
      })
    );
    expect(toastApiError).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    await waitFor(() => expect(calls("/approvals").length).toBeGreaterThan(listCalls));
    await waitFor(() => expect(calls("/approvals/summary").length).toBeGreaterThan(summaryCalls));
    await waitFor(() =>
      expect(screen.queryByText("Fırsat onayı: Büyük anlaşma")).not.toBeInTheDocument()
    );
  });

  it("shows other errors as a toast and keeps the dialog open", async () => {
    installApi(client, {
      "GET /approvals": () => page(PENDING),
      "GET /approvals/summary": () => ({ pendingCount: 2 }),
      "POST /approvals/1/decision": () => problem(403, { code: "forbidden" }),
    });
    renderPage();
    await screen.findByText("Fırsat onayı: Büyük anlaşma");
    await userEvent.click(
      within(rowFor("Fırsat onayı: Büyük anlaşma")).getByRole("button", { name: /onayını onayla/ })
    );
    const dialog = await screen.findByRole("dialog", { name: "Onay kararı" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Onayla" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
    expect(screen.getByRole("dialog", { name: "Onay kararı" })).toBeInTheDocument();
  });
});
