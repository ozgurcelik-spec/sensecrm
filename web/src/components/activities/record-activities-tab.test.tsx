import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  ME_ID,
  MEMBERS,
  clearSession,
  installApi,
  page,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { activity } from "@/test/activities";
import { toastApiError } from "@/hooks/use-toast";
import { RecordActivitiesTab } from "./record-activities-tab";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;
const RELATED = { type: "account", id: "a1", name: "Acme Ltd" } as const;

describe("RecordActivitiesTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    installApi(client, {
      "GET /activities": () =>
        page([
          activity("1", {
            subject: "Sözleşmeyi gönder",
            isOverdue: true,
            dueAt: "2026-05-02T09:00:00Z",
          }),
        ]),
      "GET /organization/members": () => MEMBERS,
      "POST /activities": () => ({ id: "new-id" }),
    });
  });
  afterEach(clearSession);

  const open = () => renderWithProviders(<RecordActivitiesTab related={RELATED} />);

  it("lists only this record's activities", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    open();

    expect(await screen.findByText("Sözleşmeyi gönder")).toBeInTheDocument();
    const params = client.get.mock.calls.find(([url]) => url === "/activities")?.[1].params;
    expect(params).toMatchObject({ relatedType: "account", relatedId: "a1" });
  });

  it("quick-adds an activity linked to the record and assigned to the current user", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    open();
    await screen.findByText("Sözleşmeyi gönder");

    const subject = screen.getByLabelText("Konu");
    await userEvent.type(subject, "  Demo planla ");
    await userEvent.click(screen.getByRole("button", { name: "Ekle" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post).toHaveBeenCalledWith("/activities", {
      type: "task",
      subject: "Demo planla",
      relatedType: "account",
      relatedId: "a1",
      assignedUserId: ME_ID,
    });
    // The form is cleared and the list is refetched.
    await waitFor(() => expect(subject).toHaveValue(""));
    await waitFor(() =>
      expect(client.get.mock.calls.filter(([url]) => url === "/activities").length).toBeGreaterThan(
        1
      )
    );
  });

  it("requires a subject", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    open();
    await screen.findByText("Sözleşmeyi gönder");

    await userEvent.click(screen.getByRole("button", { name: "Ekle" }));

    expect(await screen.findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("shows server field errors under the input and other errors as a toast", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    installApi(client, {
      "GET /activities": () => page([]),
      "GET /organization/members": () => MEMBERS,
      "POST /activities": () =>
        problem(400, { status: 400, code: "validation", errors: { Subject: ["Konu çok uzun"] } }),
    });
    open();

    await userEvent.type(await screen.findByLabelText("Konu"), "x");
    await userEvent.click(screen.getByRole("button", { name: "Ekle" }));
    expect(await screen.findByText("Konu çok uzun")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();

    const failure = problem(500, { status: 500, title: "Boom" });
    installApi(client, {
      "GET /activities": () => page([]),
      "GET /organization/members": () => MEMBERS,
      "POST /activities": () => failure,
    });
    await userEvent.click(screen.getByRole("button", { name: "Ekle" }));
    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(failure));
  });

  it("opens the detailed form with the related record locked", async () => {
    setPermissions(["crm.activities.read", "crm.activities.write"]);
    open();
    await screen.findByText("Sözleşmeyi gönder");

    await userEvent.click(screen.getByRole("button", { name: "Ayrıntılı ekle" }));

    expect(await screen.findByLabelText("İlişkili kayıt")).toHaveValue("Müşteri: Acme Ltd");
  });

  it("has no quick-add form without write permission", async () => {
    setPermissions(["crm.activities.read"]);
    open();

    expect(await screen.findByText("Sözleşmeyi gönder")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Ekle" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Tamamla" })).not.toBeInTheDocument();
  });

  it("shows an empty state", async () => {
    setPermissions(["crm.activities.read"]);
    installApi(client, { "GET /activities": () => page([]) });
    open();

    expect(await screen.findByText("Bu kayıt için aktivite yok")).toBeInTheDocument();
  });
});
