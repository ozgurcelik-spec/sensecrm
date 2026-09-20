import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, setPermissions, type MockClient } from "@/test/crm";
import { PLANS } from "@/test/platform";
import AccountPage from "@/pages/account";
import PlatformPlansPage from "@/pages/platform/plans";
import type { PlatformPlan } from "@/types";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

describe("Platform plan table - notification plan flags (M8A)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    const plans: PlatformPlan[] = PLANS.map((plan) =>
      plan.code === "business"
        ? { ...plan, features: { "notifications.email": true, "notifications.sms": false }, limits: { ...plan.limits, maxEmailsPerDay: 500 } }
        : plan.code === "starter"
          ? { ...plan, limits: { ...plan.limits, maxEmailsPerDay: null } }
          : plan
    );
    installApi(client, { "GET /platform/plans": () => plans });
  });
  afterEach(clearSession);

  it("shows the e-mail / SMS flags as badges (a missing flag reads as off) and the daily e-mail limit", async () => {
    renderWithProviders(<PlatformPlansPage />);
    const rows = await screen.findAllByTestId("plan-row");
    const business = rows.find((row) => within(row).queryByText("Business")) as HTMLElement;
    expect(within(business).getByTestId("feature-notifications.email")).toHaveAttribute(
      "aria-label",
      "E-posta bildirimi: Dahil"
    );
    expect(within(business).getByTestId("feature-notifications.sms")).toHaveAttribute(
      "aria-label",
      "SMS bildirimi: Plana dahil değil"
    );
    expect(within(business).getByText(/Günlük e-posta: 500/)).toBeInTheDocument();

    const starter = rows.find((row) => within(row).queryByText("Starter")) as HTMLElement;
    expect(within(starter).getByTestId("feature-notifications.email")).toHaveAttribute(
      "aria-label",
      "E-posta bildirimi: Plana dahil değil"
    );
    expect(within(starter).getByText(/Günlük e-posta: platform varsayılanı/)).toBeInTheDocument();
  });
});

describe("Profile page - notification preferences link", () => {
  afterEach(clearSession);

  it("links to the preferences page", () => {
    setPermissions([]);
    renderWithProviders(<AccountPage />);
    expect(screen.getByRole("link", { name: "Tercihleri aç" })).toHaveAttribute("href", "/app/notifications/preferences");
  });
});
