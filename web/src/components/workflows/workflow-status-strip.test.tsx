import type { ComponentType } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  clearSession,
  installApi,
  page,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { execution } from "@/test/workflows";
import DealDetailPage from "@/pages/crm/deal-detail";
import LeadDetailPage from "@/pages/crm/lead-detail";
import { WorkflowStatusStrip } from "./workflow-status-strip";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const executionCalls = () =>
  client.get.mock.calls.filter(([url]) => url === "/workflows/executions");

const base = {
  ownerUserId: "user-1",
  ownerName: "Ada Lovelace",
  createdAt: "2026-05-01T10:00:00Z",
};

describe("WorkflowStatusStrip", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  const renderStrip = () =>
    renderWithProviders(<WorkflowStatusStrip subjectType="lead" subjectId="lead-1" />);

  it("shows rule and status of the newest execution and links to its drawer", async () => {
    setPermissions(["org.workflows.manage"]);
    installApi(client, {
      "GET /workflows/executions": () =>
        page([execution("e1", { ruleName: "Web potansiyelleri", status: "running" })]),
    });
    renderStrip();

    const strip = await screen.findByTestId("workflow-strip");
    expect(strip).toHaveTextContent("İş akışı: Web potansiyelleri — çalışıyor");
    expect(screen.getByRole("link", { name: /Web potansiyelleri/ })).toHaveAttribute(
      "href",
      "/app/settings/workflows?tab=executions&execution=e1"
    );
    expect(executionCalls()[0]?.[1].params).toEqual({
      subjectType: "lead",
      subjectId: "lead-1",
      page: 1,
      pageSize: 1,
    });
  });

  it.each([
    ["completed", "tamamlandı"],
    ["failed", "hata"],
  ] as const)("says %s as %s", async (status, text) => {
    setPermissions(["org.workflows.manage"]);
    installApi(client, {
      "GET /workflows/executions": () => page([execution("e1", { ruleName: "Kural", status })]),
    });
    renderStrip();
    expect(await screen.findByTestId("workflow-strip")).toHaveTextContent(`— ${text}`);
  });

  it("is hidden, and asks nothing, without org.workflows.manage", async () => {
    setPermissions(["crm.leads.read"]);
    installApi(client, { "GET /workflows/executions": () => page([execution("e1")]) });
    renderStrip();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByTestId("workflow-strip")).not.toBeInTheDocument();
    expect(executionCalls()).toHaveLength(0);
  });

  it("is hidden when the record has no execution", async () => {
    setPermissions(["org.workflows.manage"]);
    installApi(client, { "GET /workflows/executions": () => page([]) });
    renderStrip();
    await waitFor(() => expect(executionCalls()).toHaveLength(1));
    expect(screen.queryByTestId("workflow-strip")).not.toBeInTheDocument();
  });

  it("stays silent when the request fails", async () => {
    setPermissions(["org.workflows.manage"]);
    installApi(client, { "GET /workflows/executions": () => problem(500, { title: "Boom" }) });
    renderStrip();
    await waitFor(() => expect(executionCalls()).toHaveLength(1));
    expect(screen.queryByTestId("workflow-strip")).not.toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
});

interface Case {
  name: string;
  path: string;
  Page: ComponentType;
  read: string;
  subjectType: string;
  record: object;
  extra?: Record<string, () => unknown>;
}

const CASES: Case[] = [
  {
    name: "lead",
    path: "leads",
    Page: LeadDetailPage,
    read: "crm.leads.read",
    subjectType: "lead",
    record: {
      id: "r1",
      lastName: "Demir",
      fullName: "Can Demir",
      company: "Demir A.Ş.",
      source: "web",
      status: "new",
      ...base,
    },
  },
  {
    name: "deal",
    path: "deals",
    Page: DealDetailPage,
    read: "crm.deals.read",
    subjectType: "deal",
    record: {
      id: "r1",
      name: "Alfa fırsatı",
      accountId: "a1",
      accountName: "Acme Ltd",
      pipelineId: "p1",
      pipelineName: "Ana",
      stageId: "s1",
      stageName: "Teklif",
      stageKind: "open",
      probability: 50,
      currency: "TRY",
      ...base,
    },
    extra: { "GET /pipelines": () => [] },
  },
];

describe.each(CASES)("$name detail: workflow strip on the Genel tab", (c) => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  function open(execs: unknown[]) {
    installApi(client, {
      [`GET /${c.path}/r1`]: () => c.record,
      "GET /workflows/executions": () => page(execs as never[]),
      ...c.extra,
    });
    renderWithProviders(
      <Routes>
        <Route path={`/app/${c.path}/:id`} element={<c.Page />} />
      </Routes>,
      { route: `/app/${c.path}/r1` }
    );
  }

  it("is shown with the permission when an execution exists", async () => {
    setPermissions([c.read, "org.workflows.manage"]);
    open([execution("e9", { ruleName: "Kural X", status: "failed" })]);
    expect(await screen.findByTestId("workflow-strip")).toHaveTextContent(
      "İş akışı: Kural X — hata"
    );
    expect(executionCalls()[0]?.[1].params).toMatchObject({
      subjectType: c.subjectType,
      subjectId: "r1",
      pageSize: 1,
    });
  });

  it("is hidden without org.workflows.manage", async () => {
    setPermissions([c.read]);
    open([execution("e9")]);
    await screen.findAllByRole("heading");
    await waitFor(() => expect(screen.getAllByRole("tab").length).toBeGreaterThan(0));
    expect(screen.queryByTestId("workflow-strip")).not.toBeInTheDocument();
    expect(executionCalls()).toHaveLength(0);
  });

  it("is hidden when there is no execution", async () => {
    setPermissions([c.read, "org.workflows.manage"]);
    open([]);
    await waitFor(() => expect(executionCalls()).toHaveLength(1));
    expect(screen.queryByTestId("workflow-strip")).not.toBeInTheDocument();
  });
});
