import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders } from "@/test-utils";
import {
  ME_ID,
  MEMBERS,
  clearSession,
  installApi,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { toastApiError } from "@/hooks/use-toast";
import { ActivityFormDialog } from "./activity-form-dialog";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

async function chooseType(label: string) {
  await userEvent.click(screen.getByRole("combobox", { name: "Tür" }));
  await userEvent.click(await screen.findByRole("option", { name: label }));
}

const setValue = (label: string, value: string) =>
  fireEvent.change(screen.getByLabelText(label), { target: { value } });

describe("ActivityFormDialog", () => {
  const onClose = vi.fn();
  const onSaved = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions([
      "crm.activities.read",
      "crm.activities.write",
      "crm.accounts.read",
      "crm.deals.read",
    ]);
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "GET /accounts": () => ({ items: [], page: 1, pageSize: 20, totalCount: 0 }),
      "GET /deals": () => ({ items: [], page: 1, pageSize: 20, totalCount: 0 }),
      "POST /activities": () => ({ id: "new-id" }),
    });
  });
  afterEach(clearSession);

  const open = (props: Partial<Parameters<typeof ActivityFormDialog>[0]> = {}) =>
    renderWithProviders(<ActivityFormDialog onClose={onClose} onSaved={onSaved} {...props} />);

  it("adapts its fields to the type: due for a task, start/end for a call or meeting, none for a note", async () => {
    open();

    // Task (default): a due date and a priority, no start/end.
    expect(screen.getByLabelText("Vade")).toBeInTheDocument();
    expect(screen.queryByLabelText("Başlangıç")).not.toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Öncelik" })).toBeInTheDocument();

    await chooseType("Arama");
    expect(screen.queryByLabelText("Vade")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Başlangıç")).toBeInTheDocument();
    expect(screen.getByLabelText("Bitiş")).toBeInTheDocument();

    await chooseType("Toplantı");
    expect(screen.getByLabelText("Başlangıç")).toBeInTheDocument();

    // A note has no date fields and no priority.
    await chooseType("Not");
    expect(screen.queryByLabelText("Vade")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Başlangıç")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Bitiş")).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Öncelik" })).not.toBeInTheDocument();
  });

  it("requires a subject", async () => {
    open();
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("creates a task with the due date converted from the organization's zone to UTC", async () => {
    open();
    await userEvent.type(screen.getByLabelText(/Konu/), "  Teklifi hazırla ");
    // Europe/Istanbul is UTC+3.
    setValue("Vade", "2026-09-21T10:00");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post).toHaveBeenCalledWith("/activities", {
      type: "task",
      subject: "Teklifi hazırla",
      priority: "normal",
      dueAt: "2026-09-21T07:00:00.000Z",
      assignedUserId: ME_ID,
    });
    await waitFor(() => expect(onSaved).toHaveBeenCalledWith("new-id"));
    expect(onClose).toHaveBeenCalled();
  });

  it("sends only start and end for a call, and nothing date-like or prioritised for a note", async () => {
    const { unmount } = open({ defaultType: "call" });
    await userEvent.type(screen.getByLabelText(/Konu/), "Müşteriyi ara");
    setValue("Başlangıç", "2026-09-21T10:00");
    setValue("Bitiş", "2026-09-21T10:30");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    const call = client.post.mock.calls[0]?.[1];
    expect(call).toMatchObject({
      type: "call",
      startAt: "2026-09-21T07:00:00.000Z",
      endAt: "2026-09-21T07:30:00.000Z",
    });
    expect(call).not.toHaveProperty("dueAt");
    unmount();

    client.post.mockClear();
    open({ defaultType: "note" });
    await userEvent.type(screen.getByLabelText(/Konu/), "Görüşme notu");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    const note = client.post.mock.calls[0]?.[1];
    expect(note).toEqual({ type: "note", subject: "Görüşme notu", assignedUserId: ME_ID });
  });

  it("rejects an end before the start on the client", async () => {
    open({ defaultType: "meeting" });
    await userEvent.type(screen.getByLabelText(/Konu/), "Toplantı");
    setValue("Başlangıç", "2026-09-21T11:00");
    setValue("Bitiş", "2026-09-21T10:00");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Bitiş, başlangıçtan önce olamaz")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("maps server validation errors onto the fields and keeps the dialog open", async () => {
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "POST /activities": () =>
        problem(400, {
          status: 400,
          code: "validation",
          errors: { Subject: ["Konu çok uzun"], DueAt: ["Vade geçersiz"] },
        }),
    });
    open();
    await userEvent.type(screen.getByLabelText(/Konu/), "x");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Konu çok uzun")).toBeInTheDocument();
    expect(screen.getByText("Vade geçersiz")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("maps activity.invalid_range to the end field", async () => {
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "POST /activities": () => problem(400, { status: 400, code: "activity.invalid_range" }),
    });
    open({ defaultType: "call" });
    await userEvent.type(screen.getByLabelText(/Konu/), "Ara");
    setValue("Başlangıç", "2026-09-21T10:00");
    setValue("Bitiş", "2026-09-21T11:00");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("Bitiş, başlangıçtan önce olamaz")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("maps activity.related_not_found to the related record field", async () => {
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "GET /deals": () => ({ items: [], page: 1, pageSize: 20, totalCount: 0 }),
      "POST /activities": () => problem(404, { status: 404, code: "activity.related_not_found" }),
    });
    open({ defaultRelated: { type: "deal", id: "d-gone", name: "Eski fırsat" } });
    await userEvent.type(screen.getByLabelText(/Konu/), "Takip");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    expect(await screen.findByText("İlişkili kayıt bulunamadı")).toBeInTheDocument();
    expect(client.post.mock.calls[0]?.[1]).toMatchObject({
      relatedType: "deal",
      relatedId: "d-gone",
    });
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("falls back to the error toast for an error that matches no field", async () => {
    const error = problem(500, { status: 500, title: "Boom" });
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "POST /activities": () => error,
    });
    open();
    await userEvent.type(screen.getByLabelText(/Konu/), "x");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
  });

  it("shows a locked related record when created from a record page", () => {
    open({ defaultRelated: { type: "account", id: "a1", name: "Acme Ltd" }, lockRelated: true });

    const related = screen.getByLabelText("İlişkili kayıt");
    expect(related).toBeDisabled();
    expect(related).toHaveValue("Müşteri: Acme Ltd");
  });

  it("edits through PUT, keeps the type fixed and sends the status", async () => {
    installApi(client, {
      "GET /organization/members": () => MEMBERS,
      "PUT /activities/a1": () => undefined,
    });
    open({
      activity: {
        id: "a1",
        type: "task",
        subject: "Eski konu",
        status: "open",
        priority: "high",
        dueAt: "2026-09-21T07:00:00Z",
        assignedUserId: "user-2",
        assignedUserName: "Grace Hopper",
        isOverdue: false,
        createdAt: "2026-05-01T00:00:00Z",
      },
    });

    expect(screen.getByRole("combobox", { name: "Tür" })).toBeDisabled();
    expect(screen.getByLabelText("Vade")).toHaveValue("2026-09-21T10:00");
    const subject = screen.getByLabelText(/Konu/);
    await userEvent.clear(subject);
    await userEvent.type(subject, "Yeni konu");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put).toHaveBeenCalledWith("/activities/a1", {
      type: "task",
      subject: "Yeni konu",
      priority: "high",
      status: "open",
      dueAt: "2026-09-21T07:00:00.000Z",
      assignedUserId: "user-2",
    });
  });
});
