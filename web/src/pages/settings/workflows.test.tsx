import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toast, toastApiError } from "@/hooks/use-toast";
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
import { ROLES, dealRule, execution, executionDetail, leadRule } from "@/test/workflows";
import WorkflowsPage from "./workflows";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const renderPage = (route = "/app/settings/workflows") =>
  renderWithProviders(
    <>
      <WorkflowsPage />
      <LocationDisplay />
    </>,
    { route }
  );

const rowFor = (text: string) => screen.getByText(text).closest("tr") as HTMLElement;

function lastListParams() {
  return client.get.mock.calls.filter(([url]) => url === "/workflows/executions").at(-1)?.[1]
    .params;
}

describe("WorkflowsPage - rules", () => {
  let rules = [leadRule(), dealRule()];

  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["org.workflows.manage"]);
    rules = [leadRule(), dealRule()];
    installApi(client, {
      "GET /workflows/rules": () => rules,
      "GET /organization/roles": () => ROLES,
    });
  });
  afterEach(clearSession);

  it("lists the rules with kind badge, parameter summary and enabled switch", async () => {
    renderPage();
    await screen.findByText("Web potansiyelleri");

    const lead = rowFor("Web potansiyelleri");
    expect(within(lead).getByText("Potansiyel atama")).toBeInTheDocument();
    await waitFor(() =>
      expect(
        within(lead).getByText(/Web, Tavsiye · Rol: Satış Temsilcisi · 24 saat/)
      ).toBeInTheDocument()
    );
    expect(within(lead).getByRole("switch", { name: /Web potansiyelleri/ })).toBeChecked();

    const deal = rowFor("Büyük fırsat onayı");
    expect(within(deal).getByText("Fırsat onayı")).toBeInTheDocument();
    expect(within(deal).getByText(/Onaylayıcı: Satış Müdürü/)).toBeInTheDocument();
    expect(within(deal).getByRole("switch", { name: /Büyük fırsat onayı/ })).not.toBeChecked();
  });

  it("flips the switch at once and keeps it when the server accepts", async () => {
    client.post.mockClear();
    installApi(client, {
      "GET /workflows/rules": () => rules,
      "GET /organization/roles": () => ROLES,
      "POST /workflows/rules/rule-2/enable": () => {
        rules = rules.map((r) => (r.id === "rule-2" ? { ...r, isEnabled: true } : r));
        return undefined;
      },
    });
    renderPage();
    await screen.findByText("Büyük fırsat onayı");

    const toggle = within(rowFor("Büyük fırsat onayı")).getByRole("switch");
    await userEvent.click(toggle);
    expect(toggle).toBeChecked();

    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/workflows/rules/rule-2/enable"));
    await waitFor(() =>
      expect(
        client.get.mock.calls.filter(([url]) => url === "/workflows/rules").length
      ).toBeGreaterThan(1)
    );
    expect(within(rowFor("Büyük fırsat onayı")).getByRole("switch")).toBeChecked();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("rolls the switch back and shows an error when disabling fails", async () => {
    let fail: (error: unknown) => void = () => undefined;
    const gate = new Promise((_, reject) => {
      fail = reject;
    });
    installApi(client, {
      "GET /workflows/rules": () => rules,
      "GET /organization/roles": () => ROLES,
      "POST /workflows/rules/rule-1/disable": () => gate,
    });
    renderPage();
    await screen.findByText("Web potansiyelleri");

    const toggle = within(rowFor("Web potansiyelleri")).getByRole("switch");
    expect(toggle).toBeChecked();
    await userEvent.click(toggle);
    // Optimistic: unchecked while the request is still in flight (and locked against double clicks).
    expect(toggle).not.toBeChecked();
    expect(toggle).toBeDisabled();

    fail(problem(500, { title: "Boom" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(within(rowFor("Web potansiyelleri")).getByRole("switch")).toBeChecked()
    );
    expect(within(rowFor("Web potansiyelleri")).getByRole("switch")).not.toBeDisabled();
  });

  it("confirms before deleting a rule", async () => {
    installApi(client, {
      "GET /workflows/rules": () => rules,
      "GET /organization/roles": () => ROLES,
      "DELETE /workflows/rules/rule-1": () => {
        rules = rules.filter((r) => r.id !== "rule-1");
        return undefined;
      },
    });
    renderPage();
    await screen.findByText("Web potansiyelleri");

    await userEvent.click(
      within(rowFor("Web potansiyelleri")).getByRole("button", { name: "Sil" })
    );
    const dialog = await screen.findByRole("dialog", { name: "Kuralı sil" });
    expect(within(dialog).getByText(/"Web potansiyelleri" kuralı silinecek/)).toBeInTheDocument();
    expect(client.delete).not.toHaveBeenCalled();

    await userEvent.click(within(dialog).getByRole("button", { name: "Sil" }));
    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/workflows/rules/rule-1"));
    await waitFor(() => expect(screen.queryByText("Web potansiyelleri")).not.toBeInTheDocument());
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));
  });

  it("opens the create dialog and the edit dialog with the rule loaded", async () => {
    renderPage();
    await screen.findByText("Web potansiyelleri");

    await userEvent.click(screen.getByRole("button", { name: "Yeni kural" }));
    expect(await screen.findByRole("dialog", { name: "Yeni kural" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Vazgeç" }));

    await userEvent.click(
      within(rowFor("Büyük fırsat onayı")).getByRole("button", { name: "Düzenle" })
    );
    const dialog = await screen.findByRole("dialog", { name: "Kuralı düzenle" });
    expect(within(dialog).getByDisplayValue("Büyük fırsat onayı")).toBeInTheDocument();
  });
});

describe("WorkflowsPage - executions", () => {
  const ROWS = [
    execution("1", { ruleName: "Atama kuralı", status: "running", endedAt: undefined }),
    execution("2", {
      ruleName: "Onay kuralı",
      kind: "dealApproval",
      status: "failed",
      subjectType: "deal",
      subjectId: "deal-2",
      subjectName: "Büyük anlaşma",
      error: "no_assignee",
    }),
  ];

  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["org.workflows.manage", "crm.leads.read"]);
    installApi(client, {
      "GET /workflows/rules": () => [leadRule(), dealRule()],
      "GET /organization/roles": () => ROLES,
      "GET /workflows/executions": () => page(ROWS),
      "GET /workflows/executions/1": () =>
        executionDetail("1", { ruleName: "Atama kuralı", status: "running", endedAt: undefined }),
      "GET /workflows/executions/2": () =>
        executionDetail("2", {
          ruleName: "Onay kuralı",
          kind: "dealApproval",
          status: "failed",
          error: "no_assignee",
          subjectType: "deal",
          subjectId: "deal-2",
          subjectName: "Büyük anlaşma",
          approvals: [
            { id: "ap-1", approverName: "Grace Hopper", status: "pending" },
            {
              id: "ap-2",
              approverName: "Alan Turing",
              status: "approved",
              decidedAt: "2026-05-10T10:00:00Z",
            },
          ],
        }),
      "POST /workflows/executions/1/terminate": () => undefined,
      "POST /workflows/executions/2/retry": () => undefined,
    });
  });
  afterEach(clearSession);

  it("lists executions and links the record only when the user may read it", async () => {
    renderPage("/app/settings/workflows?tab=executions");
    await screen.findByText("Atama kuralı");

    const lead = rowFor("Atama kuralı");
    expect(within(lead).getByText("Çalışıyor")).toBeInTheDocument();
    expect(within(lead).getByRole("link", { name: "Potansiyel 1" })).toHaveAttribute(
      "href",
      "/app/leads/lead-1"
    );
    // No crm.deals.read: the deal is shown as plain text.
    const deal = rowFor("Onay kuralı");
    expect(within(deal).getByText("Hata")).toBeInTheDocument();
    expect(within(deal).getByText("Büyük anlaşma")).toBeInTheDocument();
    expect(within(deal).queryByRole("link", { name: "Büyük anlaşma" })).not.toBeInTheDocument();
    expect(lastListParams()).toEqual({ page: 1, pageSize: 25 });
  });

  it("syncs status, rule and date range filters with the URL and the request", async () => {
    renderPage("/app/settings/workflows?tab=executions&page=3");
    await screen.findByText("Atama kuralı");

    await userEvent.click(screen.getByRole("combobox", { name: "Durum" }));
    await userEvent.click(await screen.findByRole("option", { name: "Hata" }));
    await waitFor(() =>
      expect(lastListParams()).toEqual({ page: 1, pageSize: 25, status: "failed" })
    );
    expect(screen.getByTestId("location")).toHaveTextContent(
      "/app/settings/workflows?tab=executions&status=failed"
    );

    await userEvent.click(screen.getByRole("combobox", { name: "Kural" }));
    await userEvent.click(await screen.findByRole("option", { name: "Büyük fırsat onayı" }));
    await waitFor(() => expect(lastListParams().ruleId).toBe("rule-2"));

    // Calendar days of the organization's zone (Europe/Istanbul, UTC+3) become UTC instants.
    fireEvent.change(screen.getByLabelText("Başlangıç (en erken)"), {
      target: { value: "2026-05-01" },
    });
    fireEvent.change(screen.getByLabelText("Başlangıç (en geç)"), {
      target: { value: "2026-05-10" },
    });
    await waitFor(() => expect(lastListParams().to).toBeDefined());
    expect(lastListParams()).toEqual({
      page: 1,
      pageSize: 25,
      status: "failed",
      ruleId: "rule-2",
      from: "2026-04-30T21:00:00.000Z",
      to: "2026-05-10T20:59:59.999Z",
    });
    expect(screen.getByTestId("location")).toHaveTextContent(
      "tab=executions&status=failed&ruleId=rule-2&from=2026-05-01&to=2026-05-10"
    );

    await userEvent.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    await waitFor(() => expect(lastListParams()).toEqual({ page: 1, pageSize: 25 }));
    expect(screen.getByTestId("location")).toHaveTextContent(
      "/app/settings/workflows?tab=executions"
    );
    expect(screen.getByTestId("location")).not.toHaveTextContent("status=");
  });

  it("restores the filters from the URL on load", async () => {
    renderPage(
      "/app/settings/workflows?tab=executions&status=running&ruleId=rule-1&from=2026-05-01&to=2026-05-31&page=2"
    );
    await screen.findByText("Atama kuralı");

    expect(
      client.get.mock.calls.find(([url]) => url === "/workflows/executions")?.[1].params
    ).toEqual({
      page: 2,
      pageSize: 25,
      status: "running",
      ruleId: "rule-1",
      from: "2026-04-30T21:00:00.000Z",
      to: "2026-05-31T20:59:59.999Z",
    });
    expect(screen.getByLabelText("Başlangıç (en erken)")).toHaveValue("2026-05-01");
    expect(screen.getByLabelText("Başlangıç (en geç)")).toHaveValue("2026-05-31");
    expect(screen.getByRole("combobox", { name: "Durum" })).toHaveValue("Çalışıyor");
  });

  it("shows the step timeline, approvals and no retry button for a running execution", async () => {
    renderPage("/app/settings/workflows?tab=executions");
    await screen.findByText("Atama kuralı");

    await userEvent.click(
      screen.getByRole("button", { name: "Atama kuralı yürütmesinin ayrıntıları" })
    );
    const drawer = await screen.findByRole("dialog", { name: "Atama kuralı" });
    const timeline = await within(drawer).findByTestId("step-timeline");
    expect(within(timeline).getByText("assign_lead")).toBeInTheDocument();
    expect(within(timeline).getByText("create_follow_up")).toBeInTheDocument();
    expect(within(timeline).getByText("Tamamlandı")).toBeInTheDocument();
    expect(within(timeline).getByText("Sürüyor")).toBeInTheDocument();
    expect(within(drawer).getByText("Bu yürütmede onay yok")).toBeInTheDocument();
    expect(within(drawer).getByRole("button", { name: "Sonlandır" })).toBeInTheDocument();
    expect(within(drawer).queryByRole("button", { name: "Yeniden dene" })).not.toBeInTheDocument();
    expect(screen.getByTestId("location")).toHaveTextContent("execution=1");
  });

  it("terminates a running execution after a confirmation and refreshes the list", async () => {
    renderPage("/app/settings/workflows?tab=executions&execution=1");
    const drawer = await screen.findByRole("dialog", { name: "Atama kuralı" });
    await userEvent.click(await within(drawer).findByRole("button", { name: "Sonlandır" }));

    const confirm = await screen.findByRole("dialog", { name: "Yürütmeyi sonlandır" });
    expect(client.post).not.toHaveBeenCalled();
    const listCalls = client.get.mock.calls.filter(
      ([url]) => url === "/workflows/executions"
    ).length;
    await userEvent.click(within(confirm).getByRole("button", { name: "Sonlandır" }));

    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/workflows/executions/1/terminate")
    );
    await waitFor(() =>
      expect(
        client.get.mock.calls.filter(([url]) => url === "/workflows/executions").length
      ).toBeGreaterThan(listCalls)
    );
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }));
  });

  it("shows the server message when the execution stopped running in the meantime (409)", async () => {
    installApi(client, {
      "GET /workflows/rules": () => [],
      "GET /workflows/executions": () => page(ROWS),
      "GET /workflows/executions/1": () =>
        executionDetail("1", { ruleName: "Atama kuralı", status: "running", endedAt: undefined }),
      "POST /workflows/executions/1/terminate": () =>
        problem(409, { code: "workflow.not_running" }),
    });
    renderPage("/app/settings/workflows?tab=executions&execution=1");
    const drawer = await screen.findByRole("dialog", { name: "Atama kuralı" });
    await userEvent.click(await within(drawer).findByRole("button", { name: "Sonlandır" }));
    const confirm = await screen.findByRole("dialog", { name: "Yürütmeyi sonlandır" });
    await userEvent.click(within(confirm).getByRole("button", { name: "Sonlandır" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledTimes(1));
    expect(toast).not.toHaveBeenCalled();
  });

  it("shows the translated error, approvals and retries a failed execution after a confirmation", async () => {
    renderPage("/app/settings/workflows?tab=executions&execution=2");
    const drawer = await screen.findByRole("dialog", { name: "Onay kuralı" });

    expect(
      await within(drawer).findByText("Seçilen rolde atanabilecek aktif kullanıcı yok")
    ).toBeInTheDocument();
    expect(within(drawer).getByText("Grace Hopper")).toBeInTheDocument();
    expect(within(drawer).getByText("Alan Turing")).toBeInTheDocument();
    expect(within(drawer).getByText("Onaylandı")).toBeInTheDocument();
    expect(within(drawer).queryByRole("button", { name: "Sonlandır" })).not.toBeInTheDocument();

    await userEvent.click(within(drawer).getByRole("button", { name: "Yeniden dene" }));
    const confirm = await screen.findByRole("dialog", { name: "Yürütmeyi yeniden dene" });
    expect(client.post).not.toHaveBeenCalled();
    await userEvent.click(within(confirm).getByRole("button", { name: "Yeniden dene" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/workflows/executions/2/retry"));
    await waitFor(() =>
      expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success" }))
    );
  });

  it("closing the drawer removes the execution from the URL", async () => {
    renderPage("/app/settings/workflows?tab=executions&status=failed&execution=1");
    const drawer = await screen.findByRole("dialog", { name: "Atama kuralı" });
    await userEvent.click(within(drawer).getByRole("button", { name: "Kapat" }));
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent(
        "/app/settings/workflows?tab=executions&status=failed"
      )
    );
    expect(screen.getByTestId("location")).not.toHaveTextContent("execution=");
  });

  it("switches between the tabs through ?tab=", async () => {
    renderPage();
    expect(await screen.findByRole("tab", { name: "Kurallar" })).toHaveAttribute(
      "aria-selected",
      "true"
    );
    await userEvent.click(screen.getByRole("tab", { name: "Yürütmeler" }));
    await screen.findByText("Atama kuralı");
    expect(screen.getByTestId("location")).toHaveTextContent(
      "/app/settings/workflows?tab=executions"
    );
    await userEvent.click(screen.getByRole("tab", { name: "Kurallar" }));
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/app\/settings\/workflows$/);
  });
});
