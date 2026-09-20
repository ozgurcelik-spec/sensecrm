import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, type MockClient } from "@/test/crm";
import { platformMe, setMe, subscription } from "@/test/platform";
import HomePage from "./home";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const STATE = {
  dismissed: false,
  completedCount: 0,
  totalCount: 4,
  items: [
    { key: "profile", done: false },
    { key: "invite_user", done: false },
    { key: "create_lead", done: false },
    { key: "create_workflow_rule", done: false },
  ],
};

describe("HomePage onboarding card", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, { "GET /onboarding": () => STATE });
  });
  afterEach(clearSession);

  it("shows the first-run checklist to an organization manager", async () => {
    setMe(platformMe(["org.settings.manage"], { subscription: subscription() }));
    renderWithProviders(<HomePage />);

    const card = await screen.findByTestId("onboarding-card");
    expect(within(card).getByText("İlk kurulum")).toBeInTheDocument();
    expect(within(card).getByText("0 / 4 adım tamamlandı")).toBeInTheDocument();
  });

  it("does not request or show it to a user who cannot manage settings", async () => {
    setMe(platformMe(["crm.leads.read"]));
    installApi(client, { "GET /onboarding": () => STATE, "GET /leads": () => ({ items: [], page: 1, pageSize: 25, totalCount: 0 }) });
    renderWithProviders(<HomePage />);

    await screen.findByRole("heading", { level: 2 });
    expect(screen.queryByTestId("onboarding-card")).not.toBeInTheDocument();
    expect(client.get.mock.calls.some(([url]) => url === "/onboarding")).toBe(false);
  });
});
