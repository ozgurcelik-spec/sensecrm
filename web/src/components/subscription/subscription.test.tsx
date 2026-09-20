import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, type MockClient } from "@/test/crm";
import { platformMe, setMe, subscription } from "@/test/platform";
import { useAuthStore } from "@/store/auth.store";
import { toastApiError } from "@/hooks/use-toast";
import type { MeSubscription, OnboardingState } from "@/types";
import { BlockedScreen } from "./blocked-screen";
import { OnboardingCard } from "./onboarding-card";
import { SubscriptionBanner } from "./subscription-banner";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

function renderBanner(sub: MeSubscription | undefined, permissions: string[] = ["org.settings.manage"]) {
  setMe(platformMe(permissions, { subscription: sub }));
  return renderWithProviders(<SubscriptionBanner />);
}

describe("SubscriptionBanner", () => {
  afterEach(clearSession);

  const banner = () => screen.queryByTestId("subscription-banner");

  it("shows nothing without a subscription (older server) and for a healthy plan", () => {
    const { unmount } = renderBanner(undefined);
    expect(banner()).not.toBeInTheDocument();
    unmount();
    renderBanner(subscription({ status: "active" }));
    expect(banner()).not.toBeInTheDocument();
  });

  it("shows nothing for a trial that still has more than 7 days", () => {
    renderBanner(subscription({ status: "trial", trialDaysLeft: 8 }));
    expect(banner()).not.toBeInTheDocument();
  });

  it.each([
    [7, "Deneme sürenizin bitmesine 7 gün kaldı."],
    [3, "Deneme sürenizin bitmesine 3 gün kaldı."],
    [2, "Deneme sürenizin bitmesine 2 gün kaldı."],
    [1, "Deneme sürenizin bitmesine 1 gün kaldı."],
    [0, "Deneme süreniz bugün bitiyor."],
  ])("a trial with %s day(s) left says '%s'", (days, text) => {
    renderBanner(subscription({ status: "trial", trialDaysLeft: days }));
    expect(banner()).toHaveTextContent(text);
    expect(banner()).toHaveAttribute("data-status", "trial");
    expect(screen.getByRole("link", { name: "Plan ve kullanım" })).toHaveAttribute("href", "/app/settings/plan");
  });

  it("the warning color starts at 2 days: 3 days is blue, 2 days is orange", () => {
    const { unmount } = renderBanner(subscription({ status: "trial", trialDaysLeft: 3 }));
    const blue = screen.getByTestId("subscription-banner").getAttribute("style") ?? "";
    unmount();
    renderBanner(subscription({ status: "trial", trialDaysLeft: 2 }));
    const orange = screen.getByTestId("subscription-banner").getAttribute("style") ?? "";
    expect(blue).toContain("blue");
    expect(orange).toContain("orange");
  });

  it("an expired trial says records are read-only", () => {
    renderBanner(subscription({ status: "trial_expired", accessLevel: "readOnly" }));
    expect(screen.getByTestId("subscription-banner")).toHaveTextContent(
      "Deneme süresi doldu; kayıtlar salt okunur."
    );
  });

  it("a read-only suspension says the account is suspended", () => {
    renderBanner(subscription({ status: "suspended", accessLevel: "readOnly" }));
    expect(screen.getByTestId("subscription-banner")).toHaveTextContent(
      "Hesabınız askıya alındı; kayıtlar salt okunur."
    );
  });

  it("a blocked tenant gets no banner (the blocked screen replaces the app)", () => {
    renderBanner(subscription({ status: "suspended", accessLevel: "none" }));
    expect(banner()).not.toBeInTheDocument();
  });

  it("only links to the plan page for users who may open it", () => {
    renderBanner(subscription({ status: "trial_expired", accessLevel: "readOnly" }), ["crm.leads.read"]);
    expect(screen.queryByRole("link", { name: "Plan ve kullanım" })).not.toBeInTheDocument();
  });
});

describe("BlockedScreen", () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  it("explains the reason by status, offers switching organization and re-checking", async () => {
    setMe(platformMe([], { subscription: subscription({ status: "pending_deletion", accessLevel: "none" }) }));
    // The re-check re-reads /me, which needs a session.
    act(() => useAuthStore.setState({ token: "t", refreshToken: "r" }));
    installApi(client, { "GET /me": () => platformMe([], { subscription: subscription({ status: "pending_deletion", accessLevel: "none" }) }) });
    renderWithProviders(<BlockedScreen />);

    expect(screen.getByTestId("blocked-screen")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Acme organizasyonuna erişim kapalı" })).toBeInTheDocument();
    expect(screen.getByText(/silme talebi alındı/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Organizasyon değiştir" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Durumu yeniden kontrol et" }));
    await waitFor(() => expect(client.get).toHaveBeenCalledWith("/me"));
    act(() => useAuthStore.setState({ token: null, refreshToken: null }));
  });

  it.each([
    ["suspended", /askıya alındı/],
    ["deleted", /silindi/],
  ] as const)("says the right thing for %s", (status, text) => {
    setMe(platformMe([], { subscription: subscription({ status, accessLevel: "none" }) }));
    renderWithProviders(<BlockedScreen />);
    expect(screen.getByText(text)).toBeInTheDocument();
  });
});

describe("OnboardingCard", () => {
  const STATE: OnboardingState = {
    dismissed: false,
    completedCount: 1,
    totalCount: 4,
    items: [
      { key: "profile", done: true },
      { key: "invite_user", done: false },
      { key: "create_lead", done: false },
      { key: "create_workflow_rule", done: false },
    ],
  };
  const calls = (url: string) => client.get.mock.calls.filter(([u]) => u === url);

  beforeEach(() => vi.clearAllMocks());
  afterEach(clearSession);

  function renderCard(state: OnboardingState, options: { permissions?: string[]; sub?: MeSubscription } = {}) {
    setMe(platformMe(options.permissions ?? ["org.settings.manage"], { subscription: options.sub }));
    installApi(client, {
      "GET /onboarding": () => state,
      "POST /onboarding/dismiss": () => undefined,
    });
    return renderWithProviders(<OnboardingCard />);
  }

  it("shows progress and the four steps with links; done steps are struck through", async () => {
    renderCard(STATE);

    const card = await screen.findByTestId("onboarding-card");
    expect(card).toHaveTextContent("1 / 4 adım tamamlandı");
    expect(screen.getByTestId("onboarding-profile")).toHaveAttribute("data-done", "true");
    expect(screen.queryByRole("link", { name: "Organizasyon bilgilerini tamamla" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "İlk kullanıcıyı davet et" })).toHaveAttribute("href", "/app/settings/users");
    expect(screen.getByRole("link", { name: "İlk potansiyel müşteriyi oluştur" })).toHaveAttribute("href", "/app/leads");
    expect(screen.getByRole("link", { name: "İlk iş akışı kuralını oluştur" })).toHaveAttribute("href", "/app/settings/workflows");
    expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "25");
  });

  it("drops the workflow step when the plan has no workflows (server omits it, client hides it too)", async () => {
    // The server already leaves the step out; a stale card must not offer it either.
    renderCard(STATE, { sub: subscription({ modules: { workflows: false, commerce: true, service: true, marketing: true } }) });
    await screen.findByTestId("onboarding-card");
    expect(screen.queryByTestId("onboarding-create_workflow_rule")).not.toBeInTheDocument();
    expect(screen.getByTestId("onboarding-card")).toHaveTextContent("1 / 3 adım tamamlandı");
  });

  it("dismiss posts and the card goes away after the reload", async () => {
    let dismissed = false;
    setMe(platformMe(["org.settings.manage"]));
    installApi(client, {
      "GET /onboarding": () => ({ ...STATE, dismissed }),
      "POST /onboarding/dismiss": () => {
        dismissed = true;
        return undefined;
      },
    });
    renderWithProviders(<OnboardingCard />);
    await userEvent.click(await screen.findByRole("button", { name: "Kapat" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/onboarding/dismiss"));
    await waitFor(() => expect(screen.queryByTestId("onboarding-card")).not.toBeInTheDocument());
  });

  it("is hidden when dismissed or when every step is done", async () => {
    const dismissed = renderCard({ ...STATE, dismissed: true });
    await waitFor(() => expect(calls("/onboarding")).toHaveLength(1));
    expect(screen.queryByTestId("onboarding-card")).not.toBeInTheDocument();
    dismissed.unmount();

    vi.clearAllMocks();
    renderCard({
      dismissed: false,
      completedCount: 4,
      totalCount: 4,
      items: STATE.items.map((i) => ({ ...i, done: true })),
    });
    await waitFor(() => expect(calls("/onboarding")).toHaveLength(1));
    expect(screen.queryByTestId("onboarding-card")).not.toBeInTheDocument();
  });

  it("needs org.settings.manage: no permission means no request and no card", async () => {
    renderCard(STATE, { permissions: ["crm.leads.read"] });
    expect(screen.queryByTestId("onboarding-card")).not.toBeInTheDocument();
    expect(calls("/onboarding")).toHaveLength(0);
  });

  it("is not shown (and not requested) while the tenant is read-only", () => {
    renderCard(STATE, { sub: subscription({ status: "trial_expired", accessLevel: "readOnly" }) });
    expect(screen.queryByTestId("onboarding-card")).not.toBeInTheDocument();
    expect(calls("/onboarding")).toHaveLength(0);
  });

  it("renders nothing on a load error and never breaks the page", async () => {
    setMe(platformMe(["org.settings.manage"]));
    installApi(client, { "GET /onboarding": () => problem(500, { title: "hata" }) });
    renderWithProviders(<OnboardingCard />);
    await waitFor(() => expect(calls("/onboarding")).toHaveLength(1));
    expect(screen.queryByTestId("onboarding-card")).not.toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("toasts a failed dismiss", async () => {
    setMe(platformMe(["org.settings.manage"]));
    installApi(client, {
      "GET /onboarding": () => STATE,
      "POST /onboarding/dismiss": () => problem(403, { code: "tenant.suspended", args: { reason: "suspended" } }),
    });
    renderWithProviders(<OnboardingCard />);
    await userEvent.click(await screen.findByRole("button", { name: "Kapat" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalled());
    expect(screen.getByTestId("onboarding-card")).toBeInTheDocument();
  });
});
