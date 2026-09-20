import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { ATTACHMENT_PERMISSIONS } from "@/lib/files";
import { renderWithProviders } from "@/test-utils";
import { FILE_LIMITS, fileItem } from "@/test/files";
import {
  clearSession,
  installApi,
  meWith,
  page,
  problem,
  setPermissions,
  type MockClient,
} from "@/test/crm";
import { toast, toastApiError } from "@/hooks/use-toast";
import { useAuthStore } from "@/store/auth.store";
import { ATTACHMENT_RECORD_TYPES, type AttachmentRecordType, type FileAttachment } from "@/types";
import { AttachmentsTab } from "./attachments-tab";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

const listCalls = () => client.get.mock.calls.filter(([url]) => url === "/files");
const lastListParams = () => listCalls().at(-1)?.[1]?.params;

let files: FileAttachment[];

function install(extra: Record<string, (r: never) => unknown> = {}) {
  installApi(client, {
    "GET /files": () => page(files, { pageSize: 20 }),
    "GET /files/limits": () => FILE_LIMITS,
    ...extra,
  });
}

function renderTab(recordType: AttachmentRecordType = "account", recordId = "a1") {
  return renderWithProviders(<AttachmentsTab recordType={recordType} recordId={recordId} />);
}

/** Read+write of a record type (what a user with the record's own permissions has). */
const rw = (type: AttachmentRecordType) => [
  ATTACHMENT_PERMISSIONS[type].read,
  ATTACHMENT_PERMISSIONS[type].write,
];

describe("AttachmentsTab list", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(rw("account"));
    files = [
      fileItem("f1", { name: "Sözleşme.pdf" }),
      fileItem("f2", {
        name: "Logo.png",
        extension: "png",
        contentType: "image/png",
        sizeBytes: 2048,
        uploadedByName: undefined,
      }),
      fileItem("f3", { name: "Virüslü.docx", extension: "docx", state: "quarantined", canPreview: false }),
      fileItem("f4", { name: "Kayıp.xlsx", extension: "xlsx", state: "missing", canPreview: false }),
    ];
  });
  afterEach(clearSession);

  it("asks the record's list (newest first, 20 per page) and shows icon, name, size, uploader, date and state", async () => {
    install();
    renderTab();

    const rows = await screen.findAllByTestId("file-row");
    expect(rows).toHaveLength(4);
    expect(lastListParams()).toEqual({
      recordType: "account",
      recordId: "a1",
      sort: "-uploadedAt",
      page: 1,
      pageSize: 20,
    });

    const first = within(rows[0] as HTMLElement);
    expect(first.getByText("Sözleşme.pdf")).toBeInTheDocument();
    expect(first.getByText("179,9 KB")).toBeInTheDocument();
    expect(first.getByText("Ayşe Yılmaz")).toBeInTheDocument();
    // 09:00 UTC in the organization's zone (Europe/Istanbul).
    expect(first.getByText(/20 Eyl 2026/)).toBeInTheDocument();
    expect(first.getByTestId("file-icon")).toHaveAttribute("data-kind", "pdf");

    const second = within(rows[1] as HTMLElement);
    expect(second.getByText("2 KB")).toBeInTheDocument();
    expect(second.getByTestId("file-icon")).toHaveAttribute("data-kind", "image");
    // No uploader name resolved: a dash.
    expect(second.getByText("-")).toBeInTheDocument();

    expect(within(rows[2] as HTMLElement).getByTestId("state-badge")).toHaveTextContent("Karantinada");
    expect(within(rows[3] as HTMLElement).getByTestId("state-badge")).toHaveTextContent("Kullanılamıyor");
    expect(within(rows[0] as HTMLElement).queryByTestId("state-badge")).not.toBeInTheDocument();
  });

  it("offers download only for ready files and preview only where the server says so", async () => {
    install();
    renderTab();
    await screen.findAllByTestId("file-row");

    expect(screen.getByRole("button", { name: "Önizle: Sözleşme.pdf" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "İndir: Sözleşme.pdf" })).toBeInTheDocument();
    // quarantined / missing: no download, no preview; the name is plain text but they can be deleted.
    expect(screen.queryByRole("button", { name: "İndir: Virüslü.docx" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Önizle: Virüslü.docx" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "İndir: Kayıp.xlsx" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sil: Virüslü.docx" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sil: Kayıp.xlsx" })).toBeInTheDocument();
  });

  it("shows a skeleton while loading, then an empty state that invites the drop (writers) or just says so (readers)", async () => {
    files = [];
    install();
    const view = renderTab();
    expect(screen.queryByTestId("files-empty")).not.toBeInTheDocument();
    expect(await screen.findByTestId("files-empty")).toHaveTextContent(
      "Henüz ek yok. Dosyaları yukarıdaki alana sürükleyip bırakın."
    );
    view.unmount();

    setPermissions(["crm.accounts.read"]);
    renderTab();
    expect(await screen.findByTestId("files-empty")).toHaveTextContent("Bu kayıtta ek yok.");
  });

  it("shows the API error with a retry button", async () => {
    let calls = 0;
    install({
      "GET /files": () => {
        calls += 1;
        return calls === 1
          ? problem(404, { code: "file.record_not_found", status: 404 })
          : page([fileItem("f1", { name: "Geldi.pdf" })]);
      },
    });
    renderTab();

    expect(await screen.findByText("Kayıt bulunamadı; ekleri görüntülenemez.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Tekrar dene" }));
    expect(await screen.findByText("Geldi.pdf")).toBeInTheDocument();
  });

  it("sorts by a column header (name ascending, then descending) and goes back to page 1", async () => {
    install();
    renderTab();
    await screen.findAllByTestId("file-row");

    await userEvent.click(screen.getByRole("button", { name: "Ad" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ sort: "name", page: 1 }));
    await userEvent.click(screen.getByRole("button", { name: "Ad" }));
    await waitFor(() => expect(lastListParams()).toMatchObject({ sort: "-name" }));
    expect(screen.getByRole("columnheader", { name: "Ad" })).toHaveAttribute("aria-sort", "descending");
  });

  it("searches by name after the typing pause", async () => {
    install();
    renderTab();
    await screen.findAllByTestId("file-row");

    await userEvent.type(screen.getByRole("searchbox", { name: "Ek adında ara" }), "logo");
    await waitFor(() => expect(lastListParams()).toMatchObject({ q: "logo", page: 1 }));
  });

  it("pages through more than 20 attachments", async () => {
    install({
      "GET /files": (r: never) =>
        (r as { params: { page: number } }).params.page === 2
          ? page([fileItem("f21", { name: "Sayfa2.pdf" })], { page: 2, pageSize: 20, totalCount: 21 })
          : page(files, { pageSize: 20, totalCount: 21 }),
    });
    renderTab();
    await screen.findAllByTestId("file-row");
    expect(screen.getByText("Toplam 21 ek")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "2" }));
    expect(await screen.findByText("Sayfa2.pdf")).toBeInTheDocument();
    expect(lastListParams()).toMatchObject({ page: 2 });
  });
});

describe("AttachmentsTab actions", () => {
  let created: string[];
  let revoked: string[];
  let clicked: Array<{ download: string; href: string }>;

  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(rw("account"));
    files = [fileItem("f1", { name: "Sözleşme.pdf" }), fileItem("f2", { name: "Logo.png", extension: "png" })];
    created = [];
    revoked = [];
    clicked = [];
    URL.createObjectURL = vi.fn((blob: Blob | MediaSource) => {
      created.push((blob as Blob).type);
      return "blob:download";
    });
    URL.revokeObjectURL = vi.fn((url: string) => void revoked.push(url));
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push({ download: this.download, href: this.href });
    });
  });
  afterEach(() => {
    vi.restoreAllMocks();
    clearSession();
  });

  it("downloads through an authenticated blob request and saves it under the listed name", async () => {
    install({ "GET /files/f1/content": () => new Blob(["%PDF-"], { type: "application/pdf" }) });
    renderTab();
    await screen.findAllByTestId("file-row");

    await userEvent.click(screen.getByRole("button", { name: "İndir: Sözleşme.pdf" }));

    await waitFor(() => expect(clicked).toHaveLength(1));
    const call = client.get.mock.calls.find(([url]) => url === "/files/f1/content");
    expect(call?.[1]).toMatchObject({ params: { disposition: "attachment" }, responseType: "blob" });
    // The token is a header of the client, never part of the URL.
    expect(String(call?.[0])).not.toMatch(/token|access/i);
    expect(clicked[0]).toMatchObject({ download: "Sözleşme.pdf", href: expect.stringContaining("blob:") });
    expect(revoked).toEqual(["blob:download"]);
  });

  it("clicking the file name downloads as well", async () => {
    install({ "GET /files/f2/content": () => new Blob(["x"]) });
    renderTab();
    await userEvent.click(await screen.findByRole("button", { name: "Logo.png" }));
    await waitFor(() => expect(clicked[0]?.download).toBe("Logo.png"));
  });

  it("toasts the API error of a download that fails (quarantined, missing, storage down)", async () => {
    const error = problem(410, { code: "file.content_missing", status: 410 });
    install({ "GET /files/f1/content": () => error });
    renderTab();
    await screen.findAllByTestId("file-row");

    await userEvent.click(screen.getByRole("button", { name: "İndir: Sözleşme.pdf" }));

    await waitFor(() => expect(toastApiError).toHaveBeenCalledWith(error));
    expect(clicked).toHaveLength(0);
  });

  it("asks before deleting: cancelling sends nothing, confirming deletes and refreshes the list", async () => {
    let deleted = false;
    install({
      "GET /files": () => page(deleted ? [files[1] as FileAttachment] : files, { pageSize: 20 }),
      "DELETE /files/f1": () => {
        deleted = true;
        return undefined;
      },
    });
    renderTab();
    await screen.findAllByTestId("file-row");

    await userEvent.click(screen.getByRole("button", { name: "Sil: Sözleşme.pdf" }));
    const dialog = await screen.findByRole("dialog", { name: "Eki sil" });
    expect(dialog).toHaveTextContent("\"Sözleşme.pdf\" silinsin mi?");
    await userEvent.click(within(dialog).getByRole("button", { name: "Vazgeç" }));
    expect(client.delete).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole("button", { name: "Sil: Sözleşme.pdf" }));
    await userEvent.click(within(await screen.findByRole("dialog", { name: "Eki sil" })).getByRole("button", { name: "Sil" }));

    await waitFor(() => expect(client.delete).toHaveBeenCalledWith("/files/f1"));
    await waitFor(() => expect(screen.queryByText("Sözleşme.pdf")).not.toBeInTheDocument());
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ variant: "success", description: "Ek silindi" }));
  });

  it("renames keeping the extension: the base name is selected, a different extension is refused before any request", async () => {
    install({ "PATCH /files/f1": () => undefined });
    renderTab();
    await screen.findAllByTestId("file-row");

    await userEvent.click(screen.getByRole("button", { name: "Yeniden adlandır: Sözleşme.pdf" }));
    const dialog = await screen.findByRole("dialog", { name: "Dosyayı yeniden adlandır" });
    const input = within(dialog).getByRole("textbox", { name: "Dosya adı" }) as HTMLInputElement;
    expect(input).toHaveValue("Sözleşme.pdf");
    expect(dialog).toHaveTextContent("Uzantı değiştirilemez.");

    await userEvent.clear(input);
    await userEvent.type(input, "Sözleşme.exe");
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
    expect(await within(dialog).findByText("Dosya uzantısı değiştirilemez.")).toBeInTheDocument();
    expect(client.patch).not.toHaveBeenCalled();

    await userEvent.clear(input);
    await userEvent.type(input, "Yeni Sözleşme.PDF");
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(client.patch).toHaveBeenCalledWith("/files/f1", { name: "Yeni Sözleşme.PDF" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Dosyayı yeniden adlandır" })).not.toBeInTheDocument());
  });

  it("puts the server's extension / name errors of a rename on the field", async () => {
    install({
      "PATCH /files/f1": () => problem(400, { code: "file.extension_change_not_allowed", status: 400 }),
    });
    renderTab();
    await screen.findAllByTestId("file-row");
    await userEvent.click(screen.getByRole("button", { name: "Yeniden adlandır: Sözleşme.pdf" }));
    const dialog = await screen.findByRole("dialog", { name: "Dosyayı yeniden adlandır" });
    const input = within(dialog).getByRole("textbox", { name: "Dosya adı" });
    await userEvent.clear(input);
    await userEvent.type(input, "Başka.pdf");
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));

    expect(await within(dialog).findByText("Dosya uzantısı değiştirilemez.")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
  });

  it("an empty name is a field error and sends nothing", async () => {
    install();
    renderTab();
    await screen.findAllByTestId("file-row");
    await userEvent.click(screen.getByRole("button", { name: "Yeniden adlandır: Sözleşme.pdf" }));
    const dialog = await screen.findByRole("dialog", { name: "Dosyayı yeniden adlandır" });
    await userEvent.clear(within(dialog).getByRole("textbox", { name: "Dosya adı" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Kaydet" }));
    expect(await within(dialog).findByText("Bir ad girin")).toBeInTheDocument();
    expect(client.patch).not.toHaveBeenCalled();
  });

  it("opens the preview of a previewable file", async () => {
    install({ "GET /files/f1/content": () => new Blob(["x"], { type: "image/png" }) });
    renderTab();
    await screen.findAllByTestId("file-row");
    await userEvent.click(screen.getByRole("button", { name: "Önizle: Sözleşme.pdf" }));
    expect(await screen.findByRole("dialog", { name: "Sözleşme.pdf" })).toBeInTheDocument();
    await waitFor(() => expect(created).toEqual(["image/png"]));
  });
});

describe("AttachmentsTab permissions (record type x role matrix)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    files = [fileItem("f1", { name: "Sözleşme.pdf" })];
    install();
  });
  afterEach(clearSession);

  describe.each(ATTACHMENT_RECORD_TYPES)("%s", (type) => {
    const { read, write } = ATTACHMENT_PERMISSIONS[type];
    const other = ATTACHMENT_PERMISSIONS[type === "account" ? "contact" : "account"];

    it("read + write: drop area, download, rename and delete", async () => {
      setPermissions([read, write]);
      renderTab(type, "r1");
      expect(await screen.findByText("Sözleşme.pdf")).toBeInTheDocument();
      expect(screen.getByTestId("file-dropzone")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "İndir: Sözleşme.pdf" })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Yeniden adlandır: Sözleşme.pdf" })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Sil: Sözleşme.pdf" })).toBeInTheDocument();
      expect(lastListParams()).toMatchObject({ recordType: type, recordId: "r1" });
    });

    it("read only: the list and download, but no drop area, rename or delete", async () => {
      setPermissions([read]);
      renderTab(type, "r1");
      expect(await screen.findByText("Sözleşme.pdf")).toBeInTheDocument();
      expect(screen.queryByTestId("file-dropzone")).not.toBeInTheDocument();
      expect(screen.queryByTestId("file-input")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "İndir: Sözleşme.pdf" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /^Yeniden adlandır/ })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /^Sil:/ })).not.toBeInTheDocument();
    });

    it.each([
      ["write only", [write]],
      ["no permission", []],
      ["another record type's read + write", [other.read, other.write]],
    ] as const)("%s: nothing is shown and no request is sent", (_label, permissions) => {
      setPermissions([...permissions]);
      renderTab(type, "r1");
      expect(screen.queryByTestId("attachments-tab")).not.toBeInTheDocument();
      expect(screen.queryByTestId("file-dropzone")).not.toBeInTheDocument();
      expect(client.get).not.toHaveBeenCalled();
    });
  });

  it("a read-only tenant (trial over / suspended) hides uploads, rename and delete but keeps the list and downloads", async () => {
    act(() =>
      useAuthStore.setState({
        me: {
          ...meWith(rw("account")),
          subscription: {
            planCode: "starter",
            planName: "Starter",
            status: "trial_expired",
            accessLevel: "readOnly",
            modules: {},
          },
        },
      })
    );
    renderTab();
    expect(await screen.findByText("Sözleşme.pdf")).toBeInTheDocument();
    expect(screen.queryByTestId("file-dropzone")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Yeniden adlandır/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Sil:/ })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "İndir: Sözleşme.pdf" })).toBeInTheDocument();
  });

  it.each([
    ["quote", "commerce"],
    ["order", "commerce"],
    ["case", "service"],
    ["campaign", "marketing"],
  ] as const)("%s: no request at all while the plan switches the %s module off", (type, module) => {
    act(() =>
      useAuthStore.setState({
        me: {
          ...meWith(rw(type)),
          subscription: {
            planCode: "starter",
            planName: "Starter",
            status: "active",
            accessLevel: "full",
            modules: { [module]: false },
          },
        },
      })
    );
    renderTab(type, "r1");
    expect(screen.queryByTestId("attachments-tab")).not.toBeInTheDocument();
    expect(client.get).not.toHaveBeenCalled();
  });
});
