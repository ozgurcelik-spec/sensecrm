import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { LocationDisplay, clearSession, installApi, page, type MockClient } from "@/test/crm";
import { PLANS, orgRow } from "@/test/platform";
import PlatformAuditPage from "./platform-audit";
import PlatformPlansPage from "./plans";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

describe("Platform plans (read-only catalog)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, { "GET /platform/plans": () => PLANS });
  });
  afterEach(clearSession);

  it("lists the catalog with limits, modules, assigned counts and an inactive marker", async () => {
    renderWithProviders(<PlatformPlansPage />);
    const rows = await screen.findAllByTestId("plan-row");
    expect(rows).toHaveLength(4);

    const starter = rows[1] as HTMLElement;
    expect(within(starter).getByText("starter")).toBeInTheDocument();
    expect(within(starter).getByText("14")).toBeInTheDocument();
    expect(within(starter).getByText(/Kullanıcı: 5/)).toBeInTheDocument();
    expect(within(starter).getByText(/Satış: 5\.000/)).toBeInTheDocument();
    expect(within(starter).getByLabelText("Pazarlama: Plana dahil değil")).toBeInTheDocument();
    expect(within(starter).getByText("7")).toBeInTheDocument();

    const internal = rows[0] as HTMLElement;
    expect(within(internal).getByText(/Kullanıcı: Sınırsız/)).toBeInTheDocument();
    expect(within(internal).getByLabelText("Pazarlama: Dahil")).toBeInTheDocument();

    expect(within(rows[3] as HTMLElement).getByText("Pasif")).toBeInTheDocument();
  });

  it("has no write controls and says plans are managed by configuration", async () => {
    renderWithProviders(<PlatformPlansPage />);
    await screen.findAllByTestId("plan-row");
    expect(screen.getByText(/yapılandırmayla yönetilir/)).toBeInTheDocument();
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
    expect(client.put).not.toHaveBeenCalled();
    expect(client.delete).not.toHaveBeenCalled();
  });
});

describe("Platform audit page", () => {
  const ENTRY = {
    id: "a1",
    occurredAt: "2026-09-19T10:00:00Z",
    action: "organization.suspended",
    actorEmail: "ops@sense.com",
    targetTenantId: "t1",
    targetTenantName: "Acme A.Ş.",
    details: { reason: "Güvenlik" },
  };
  const lastAuditParams = () =>
    client.get.mock.calls.filter(([url]) => url === "/platform/audit").at(-1)?.[1].params;

  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /platform/audit": () => page([ENTRY], { totalCount: 60 }),
      "GET /platform/organizations": () => page([orgRow("t1", { name: "Acme A.Ş." })]),
      "GET /platform/organizations/t1": () => orgRow("t1", { name: "Acme A.Ş." }),
    });
  });
  afterEach(clearSession);

  it("lists entries newest first with action label, actor, organization link and expandable details", async () => {
    renderWithProviders(<PlatformAuditPage />);
    const row = (await screen.findByText("Askıya alındı", { selector: "td" })).closest("tr") as HTMLElement;
    expect(within(row).getByText("ops@sense.com")).toBeInTheDocument();
    expect(within(row).getByRole("link", { name: "Acme A.Ş." })).toHaveAttribute(
      "href",
      "/app/platform/organizations/t1"
    );
    expect(within(row).queryByTestId("audit-details")).not.toBeInTheDocument();
    await userEvent.click(within(row).getByRole("button", { name: "Göster" }));
    expect(within(row).getByTestId("audit-details")).toHaveTextContent("Güvenlik");
    expect(lastAuditParams()).toEqual({ page: 1, pageSize: 25 });
  });

  it("keeps action, organization, dates and page in the URL and sends them to the server", async () => {
    renderWithProviders(
      <>
        <PlatformAuditPage />
        <LocationDisplay />
      </>
    );
    await screen.findByText("ops@sense.com");

    await userEvent.click(screen.getByRole("combobox", { name: "İşlem" }));
    await userEvent.click(await screen.findByRole("option", { name: "Silme talebi" }));
    await waitFor(() => expect(lastAuditParams()).toMatchObject({ action: "deletion.requested" }));
    expect(screen.getByTestId("location")).toHaveTextContent("action=deletion.requested");

    await userEvent.click(screen.getByRole("combobox", { name: "Organizasyon" }));
    await userEvent.click(await screen.findByRole("option", { name: "Acme A.Ş." }));
    await waitFor(() => expect(lastAuditParams()).toMatchObject({ tenantId: "t1" }));

    await userEvent.type(screen.getByLabelText("Başlangıç günü"), "2026-09-01");
    await waitFor(() => expect(lastAuditParams()).toMatchObject({ from: "2026-09-01" }));
    await userEvent.type(screen.getByLabelText("Bitiş günü"), "2026-09-20");
    await waitFor(() => expect(lastAuditParams()).toMatchObject({ to: "2026-09-20" }));

    await userEvent.click(screen.getByRole("button", { name: "2" }));
    await waitFor(() => expect(lastAuditParams()).toMatchObject({ page: 2, action: "deletion.requested", tenantId: "t1" }));
    expect(screen.getByTestId("location")).toHaveTextContent("page=2");

    await userEvent.click(screen.getByRole("button", { name: "Filtreleri temizle" }));
    await waitFor(() => expect(lastAuditParams()).toEqual({ page: 1, pageSize: 25 }));
  });

  it("restores filters from a shared URL", async () => {
    renderWithProviders(<PlatformAuditPage />, {
      route: "/app/platform/audit?action=usage.exported&tenantId=t1&from=2026-09-01&page=2",
    });
    await screen.findByText("ops@sense.com");
    expect(lastAuditParams()).toEqual({
      page: 2,
      pageSize: 25,
      action: "usage.exported",
      tenantId: "t1",
      from: "2026-09-01",
    });
    expect(screen.getByLabelText("Başlangıç günü")).toHaveValue("2026-09-01");
    expect(screen.getByRole("combobox", { name: "Organizasyon" })).toHaveValue("Acme A.Ş.");
  });
});
