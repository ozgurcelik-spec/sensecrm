import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Route, Routes } from "react-router";
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { focusManager } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import { LocationDisplay, clearSession, installApi, meWith, type MockClient } from "@/test/crm";
import { INVITATION_POLL_INTERVAL_MS } from "@/hooks/use-invitations";
import { toast } from "@/hooks/use-toast";
import { useAuthStore } from "@/store/auth.store";
import { InvitationsBell } from "./invitations-bell";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const INVITATIONS = [
  {
    id: "i1",
    organizationId: "o2",
    organizationName: "Globex",
    roleName: "Satış",
    invitedAt: "2026-09-01T10:00:00Z",
  },
  {
    id: "i2",
    organizationId: "o3",
    organizationName: "Initech",
    roleName: "Yönetici",
    invitedAt: "2026-09-02T10:00:00Z",
  },
];

const invitationCalls = () =>
  client.get.mock.calls.filter(([url]) => url === "/me/invitations").length;

async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

function renderBell() {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="*" element={<InvitationsBell />} />
      </Routes>
      <LocationDisplay />
    </>,
    { route: "/app/leads" }
  );
}

describe("InvitationsBell", () => {
  let invitations = INVITATIONS;
  let meOrganizations = meWith([]).organizations;

  beforeEach(() => {
    vi.clearAllMocks();
    invitations = INVITATIONS;
    meOrganizations = meWith([]).organizations;
    act(() => useAuthStore.setState({ token: "t", refreshToken: "r", me: meWith([]) }));
    installApi(client, {
      "GET /me/invitations": () => invitations,
      "POST /me/invitations/i1/accept": () => {
        invitations = invitations.filter((i) => i.id !== "i1");
        meOrganizations = [...meOrganizations, { id: "o2", name: "Globex", slug: "globex" }];
        return undefined;
      },
      "POST /me/invitations/i2/decline": () => {
        invitations = invitations.filter((i) => i.id !== "i2");
        return undefined;
      },
      "GET /me": () => ({ ...meWith([]), organizations: meOrganizations }),
      "POST /auth/switch-organization": () => ({
        accessToken: "switched-access",
        refreshToken: "switched-refresh",
        expiresAt: "2030-01-01T00:00:00Z",
      }),
    });
  });
  afterEach(() => {
    focusManager.setFocused(undefined);
    vi.useRealTimers();
    clearSession();
    act(() => useAuthStore.setState({ token: null, refreshToken: null }));
  });

  it("shows a badge with the number of pending invitations", async () => {
    renderBell();

    const button = await screen.findByRole("button", { name: "Bekleyen davetler: 2" });
    expect(button.parentElement).toHaveTextContent("2");
  });

  it("renders nothing while there are no invitations", async () => {
    invitations = [];
    renderBell();

    await waitFor(() => expect(invitationCalls()).toBe(1));
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("stays hidden when the invitations request fails", async () => {
    installApi(client, {
      "GET /me/invitations": () => new Error("boom"),
    });
    renderBell();

    await waitFor(() => expect(invitationCalls()).toBe(1));
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("lists the invitations in a dialog", async () => {
    renderBell();
    await userEvent.click(await screen.findByRole("button", { name: "Bekleyen davetler: 2" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getAllByTestId("invitation")).toHaveLength(2);
    expect(within(dialog).getByText("Globex")).toBeInTheDocument();
    expect(within(dialog).getByText(/Rol: Satış/)).toBeInTheDocument();
    expect(within(dialog).getByText("Initech")).toBeInTheDocument();
  });

  it("accepts an invitation, reloads /me and offers to switch to the new organization", async () => {
    renderBell();
    await userEvent.click(await screen.findByRole("button", { name: "Bekleyen davetler: 2" }));
    const dialog = await screen.findByRole("dialog");

    await userEvent.click(within(dialog).getByRole("button", { name: "Globex davetini kabul et" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/me/invitations/i1/accept"));
    await waitFor(() =>
      expect(useAuthStore.getState().me?.organizations.map((o) => o.id)).toContain("o2")
    );
    expect(toast).toHaveBeenCalledWith(
      expect.objectContaining({ description: "Globex davetini kabul ettiniz" })
    );
    // The accepted row is gone from the list, the switch prompt is shown.
    expect(
      await within(dialog).findByText("Globex organizasyonuna katıldınız.")
    ).toBeInTheDocument();
    expect(within(dialog).getAllByTestId("invitation")).toHaveLength(1);

    await userEvent.click(within(dialog).getByRole("button", { name: "Şimdi geç" }));

    await waitFor(() =>
      expect(client.post).toHaveBeenCalledWith("/auth/switch-organization", {
        organizationId: "o2",
      })
    );
    await waitFor(() => expect(screen.getByTestId("location")).toHaveTextContent("/app"));
    expect(useAuthStore.getState().token).toBe("switched-access");
  });

  it("declines an invitation", async () => {
    renderBell();
    await userEvent.click(await screen.findByRole("button", { name: "Bekleyen davetler: 2" }));
    const dialog = await screen.findByRole("dialog");

    await userEvent.click(within(dialog).getByRole("button", { name: "Initech davetini reddet" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledWith("/me/invitations/i2/decline"));
    await waitFor(() => expect(within(dialog).getAllByTestId("invitation")).toHaveLength(1));
    expect(within(dialog).queryByText("Initech")).not.toBeInTheDocument();
    expect(toast).toHaveBeenCalledWith(
      expect.objectContaining({ description: "Davet reddedildi" })
    );
    // Declining does not touch the organization list.
    expect(client.get).not.toHaveBeenCalledWith("/me");
  });

  it("polls every 60 seconds while the tab is visible", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderBell();
    await screen.findByRole("button", { name: "Bekleyen davetler: 2" });
    expect(invitationCalls()).toBe(1);
    expect(INVITATION_POLL_INTERVAL_MS).toBe(60_000);

    invitations = [...INVITATIONS, { ...INVITATIONS[0]!, id: "i3", organizationName: "Umbrella" }];
    await advance(30_000);
    expect(invitationCalls()).toBe(1);
    await advance(31_000);
    await waitFor(() => expect(invitationCalls()).toBe(2));
    expect(await screen.findByRole("button", { name: "Bekleyen davetler: 3" })).toBeInTheDocument();
  });
});
