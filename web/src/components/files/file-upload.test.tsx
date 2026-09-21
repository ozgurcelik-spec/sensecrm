import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CanceledError } from "axios";
import { apiClient } from "@/lib/api-client";
import { renderWithProviders, testI18n } from "@/test-utils";
import { FILE_LIMITS, fileItem, makeFile, makeFileOfSize } from "@/test/files";
import { clearSession, installApi, page, problem, setPermissions, type MockClient } from "@/test/crm";
import { toast } from "@/hooks/use-toast";
import type { FileAttachment, FileUploadResult } from "@/types";
import { I18nextProvider } from "react-i18next";
import { MantineProvider } from "@mantine/core";
import { MemoryRouter } from "react-router";
import { AttachmentsTab } from "./attachments-tab";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

interface PendingUpload {
  url: string;
  body: FormData;
  config: {
    params: Record<string, string>;
    headers: Record<string, string>;
    timeout: number;
    signal: AbortSignal;
    onUploadProgress: (event: { loaded: number; total: number; progress: number }) => void;
  };
  resolve: (result: FileUploadResult) => void;
  reject: (error: unknown) => void;
}

let uploads: PendingUpload[];
let stored: FileAttachment[];

const WRITE = ["crm.accounts.read", "crm.accounts.write"];

/** Every POST /files stays pending until the test settles it (progress, cancel, errors). */
function install() {
  installApi(client, {
    "GET /files": () => page(stored, { pageSize: 20 }),
    "GET /files/limits": () => FILE_LIMITS,
  });
  client.post.mockImplementation(
    (url: string, body: FormData, config: PendingUpload["config"]) =>
      new Promise((resolve, reject) => {
        uploads.push({
          url,
          body,
          config,
          resolve: (result) => resolve({ data: result, status: 201 }),
          reject,
        });
        config.signal.addEventListener("abort", () => reject(new CanceledError("canceled")));
      })
  );
}

const ok = (...items: FileAttachment[]): FileUploadResult => ({ items, failed: [] });

function renderTab() {
  return renderWithProviders(<AttachmentsTab recordType="account" recordId="a1" />);
}

/** Selecting files with the picker (a hidden multi-file input behind the "Dosya seç" button). */
async function pick(...picked: File[]) {
  // The limits (client side pre-checks) come from GET /files/limits: wait until the hint shows them.
  await screen.findByText(/Dosya başına en çok 25 MB/);
  const input = screen.getByTestId("file-input");
  await act(async () => {
    fireEvent.change(input, { target: { files: picked } });
  });
}

const rows = () => screen.queryAllByTestId("upload-row");
const listCalls = () => client.get.mock.calls.filter(([url]) => url === "/files");

describe("file drop area and upload queue", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    uploads = [];
    stored = [];
    setPermissions(WRITE);
    install();
  });
  afterEach(clearSession);

  it("has a visible, keyboard reachable select button tied to the limits text", async () => {
    renderTab();
    const button = await screen.findByRole("button", { name: "Dosya seç" });
    const hint = await screen.findByText(/Dosya başına en çok 25 MB/);
    expect(hint).toHaveTextContent("PDF, PNG, JPG");
    expect(button).toHaveAttribute("aria-describedby", hint.id);
    expect(button).toHaveAttribute("type", "button");
    // The hidden input takes several files and offers only the allowed extensions to the OS dialog.
    const input = screen.getByTestId("file-input");
    expect(input).toHaveAttribute("multiple");
    expect(input).toHaveAttribute("accept", expect.stringContaining(".pdf"));

    const clickSpy = vi.spyOn(input, "click");
    button.focus();
    await userEvent.keyboard("{Enter}");
    expect(clickSpy).toHaveBeenCalled();
  });

  it("uploads a picked file as multipart, one request per file, with the record in the query", async () => {
    renderTab();
    const file = makeFile("Teklif.pdf", 2048, "application/pdf");
    await pick(file);

    await waitFor(() => expect(uploads).toHaveLength(1));
    const upload = uploads[0] as PendingUpload;
    expect(upload.url).toBe("/files");
    expect(upload.config.params).toEqual({ recordType: "account", recordId: "a1" });
    // FormData stores its own copy of the File (same name, type and size).
    expect(upload.body.get("file")).toMatchObject({ name: "Teklif.pdf", size: 2048, type: "application/pdf" });
    // The client default is JSON: multipart must be requested explicitly, with the long timeout.
    expect(upload.config.headers["Content-Type"]).toBe("multipart/form-data");
    expect(upload.config.timeout).toBe(300000);
  });

  it("accepts files dropped on the area (highlighting while dragging) and ignores drags that carry no files", async () => {
    renderTab();
    const zone = await screen.findByTestId("file-dropzone");

    fireEvent.dragEnter(zone, { dataTransfer: { types: ["text/plain"] } });
    expect(zone).not.toHaveAttribute("data-dragging");

    fireEvent.dragEnter(zone, { dataTransfer: { types: ["Files"] } });
    expect(zone).toHaveAttribute("data-dragging", "true");
    expect(zone).toHaveTextContent("Yüklemek için bırakın");
    fireEvent.dragLeave(zone, { dataTransfer: { types: ["Files"] } });
    expect(zone).not.toHaveAttribute("data-dragging");

    fireEvent.dragEnter(zone, { dataTransfer: { types: ["Files"] } });
    await act(async () => {
      fireEvent.drop(zone, {
        dataTransfer: { types: ["Files"], files: [makeFile("a.pdf"), makeFile("b.png")] },
      });
    });
    expect(zone).not.toHaveAttribute("data-dragging");
    await waitFor(() => expect(uploads).toHaveLength(2));
    expect(rows()).toHaveLength(2);
  });

  it("shows per-file progress and lets the user cancel one upload (its request is aborted, the row goes)", async () => {
    renderTab();
    await pick(makeFile("a.pdf"), makeFile("b.pdf"));
    await waitFor(() => expect(uploads).toHaveLength(2));

    act(() => uploads[0]?.config.onUploadProgress({ loaded: 50, total: 100, progress: 0.5 }));
    const first = within(rows()[0] as HTMLElement);
    expect(first.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "50");
    expect(first.getByText("%50")).toBeInTheDocument();
    expect(within(rows()[1] as HTMLElement).getByRole("progressbar")).toHaveAttribute("aria-valuenow", "0");

    await userEvent.click(first.getByRole("button", { name: "Yüklemeyi iptal et: a.pdf" }));
    expect(uploads[0]?.config.signal.aborted).toBe(true);
    expect(uploads[1]?.config.signal.aborted).toBe(false);
    await waitFor(() => expect(rows()).toHaveLength(1));
    expect(within(rows()[0] as HTMLElement).getByText("b.pdf")).toBeInTheDocument();
    // A cancelled upload is not an error: nothing is toasted.
    expect(toast).not.toHaveBeenCalled();
  });

  it("runs at most three uploads at a time; the rest wait in the queue and start as slots free up", async () => {
    renderTab();
    await pick(...["1", "2", "3", "4", "5"].map((n) => makeFile(`dosya${n}.pdf`)));

    await waitFor(() => expect(uploads).toHaveLength(3));
    expect(rows().map((r) => r.getAttribute("data-status"))).toEqual([
      "uploading",
      "uploading",
      "uploading",
      "queued",
      "queued",
    ]);
    expect(within(rows()[3] as HTMLElement).getByText("Sırada bekliyor")).toBeInTheDocument();

    await act(async () => uploads[0]?.resolve(ok(fileItem("n1"))));
    await waitFor(() => expect(uploads).toHaveLength(4));
    expect(uploads.filter((u) => !u.config.signal.aborted)).toHaveLength(4);
    expect(uploads[3]?.body.get("file")).toMatchObject({ name: "dosya4.pdf" });
  });

  it("refreshes the list after an upload and removes its row once the list shows the file", async () => {
    renderTab();
    expect(await screen.findByTestId("files-empty")).toBeInTheDocument();
    const before = listCalls().length;

    await pick(makeFile("Yeni.pdf"));
    await waitFor(() => expect(uploads).toHaveLength(1));
    const created = fileItem("n1", { name: "Yeni.pdf" });
    stored = [created];
    await act(async () => uploads[0]?.resolve(ok(created)));

    expect(await screen.findByText("Yeni.pdf", { selector: "button" })).toBeInTheDocument();
    await waitFor(() => expect(rows()).toHaveLength(0));
    expect(listCalls().length).toBeGreaterThan(before);
  });

  describe("client side pre-checks (no request is sent)", () => {
    it.each([
      ["run.exe", makeFile("run.exe"), "Bu dosya türüne izin verilmiyor (.exe)."],
      ["README", makeFile("README"), "Bu dosya türüne izin verilmiyor (.)."],
      ["big.pdf", makeFileOfSize("big.pdf", FILE_LIMITS.maxFileBytes + 1), "Dosya çok büyük. En çok 25 MB yüklenebilir."],
      ["empty.pdf", new File([], "empty.pdf"), "Dosya boş."],
    ])("%s is rejected in its own row", async (_name, file, message) => {
      renderTab();
      await pick(file);
      const row = await screen.findByTestId("upload-row");
      expect(row).toHaveAttribute("data-status", "error");
      expect(within(row).getByRole("alert")).toHaveTextContent(message);
      expect(uploads).toHaveLength(0);
      expect(client.post).not.toHaveBeenCalled();
    });

    it("the exact size limit passes, and valid files next to a rejected one still upload", async () => {
      renderTab();
      await pick(makeFileOfSize("exact.pdf", FILE_LIMITS.maxFileBytes), makeFile("run.exe"));
      await waitFor(() => expect(uploads).toHaveLength(1));
      expect(rows().map((r) => r.getAttribute("data-status"))).toEqual(["uploading", "error"]);
    });

    it("an error row is dismissed with its close button", async () => {
      renderTab();
      await pick(makeFile("run.exe"));
      await userEvent.click(await screen.findByRole("button", { name: "Satırı kapat: run.exe" }));
      expect(rows()).toHaveLength(0);
    });
  });

  describe("server errors are worded per code", () => {
    it.each([
      [413, "file.too_large", { maxBytes: 26214400 }, "Dosya çok büyük. En çok 25 MB yüklenebilir."],
      [415, "file.type_not_allowed", { extension: "exe" }, "Bu dosya türüne izin verilmiyor (.exe)."],
      [422, "file.content_mismatch", undefined, "Dosya içeriği uzantısıyla uyuşmuyor ya da güvenli olmayan içerik barındırıyor."],
      [422, "file.infected", undefined, "Dosyada zararlı içerik bulundu; yüklenmedi."],
      [400, "file.empty", undefined, "Dosya boş."],
      [400, "file.name_invalid", undefined, "Dosya adı geçersiz."],
      [400, "file.too_many_files", { max: 10 }, "Tek seferde en çok 10 dosya yüklenebilir."],
      [400, "file.upload_invalid", undefined, "Yükleme isteği geçersiz. Dosyayı yeniden seçin."],
      [503, "file.storage_unavailable", undefined, "Dosya depolama şu an kullanılamıyor. Biraz sonra tekrar deneyin."],
      [503, "file.scan_unavailable", undefined, "Virüs taraması şu an yapılamıyor. Biraz sonra tekrar deneyin."],
      [404, "file.record_not_found", undefined, "Kayıt bulunamadı; ekleri görüntülenemez."],
      [429, "general.rate_limit_exceeded", undefined, "Çok fazla istek gönderildi. Biraz bekleyip tekrar deneyin."],
    ])("%s %s", async (status, code, args, message) => {
      renderTab();
      await pick(makeFile("a.pdf"));
      await waitFor(() => expect(uploads).toHaveLength(1));
      await act(async () => uploads[0]?.reject(problem(status, { code, status, args })));

      const row = await screen.findByTestId("upload-row");
      expect(row).toHaveAttribute("data-status", "error");
      expect(within(row).getByRole("alert")).toHaveTextContent(message);
      // Only a reached quota gets a toast; the others live in their row.
      expect(toast).not.toHaveBeenCalled();
    });

    it("a bare 413 from the proxy (no ProblemDetails) is still 'too large'", async () => {
      renderTab();
      await pick(makeFile("a.pdf"));
      await waitFor(() => expect(uploads).toHaveLength(1));
      await act(async () => uploads[0]?.reject(problem(413, { html: "<html>413</html>" })));
      expect(await screen.findByRole("alert")).toHaveTextContent("Dosya çok büyük. Sunucunun kabul ettiği boyutu aşıyor.");
    });

    it("a 200 answer that stored nothing and lists a failure is an error row too", async () => {
      renderTab();
      await pick(makeFile("a.pdf"));
      await waitFor(() => expect(uploads).toHaveLength(1));
      await act(async () =>
        uploads[0]?.resolve({
          items: [],
          failed: [{ fileName: "a.pdf", code: "file.type_not_allowed", args: { extension: "pdf" } }],
        })
      );
      expect(await screen.findByRole("alert")).toHaveTextContent("Bu dosya türüne izin verilmiyor (.pdf).");
    });

    it("retry sends the file again", async () => {
      renderTab();
      await pick(makeFile("a.pdf"));
      await waitFor(() => expect(uploads).toHaveLength(1));
      await act(async () => uploads[0]?.reject(problem(503, { code: "file.storage_unavailable", status: 503 })));

      await userEvent.click(await screen.findByRole("button", { name: "Tekrar dene: a.pdf" }));
      await waitFor(() => expect(uploads).toHaveLength(2));
      expect(rows()[0]).toHaveAttribute("data-status", "uploading");
      await act(async () => uploads[1]?.resolve(ok(fileItem("n1"))));
      await waitFor(() => expect(rows()).toHaveLength(0));
    });
  });

  describe("quota exceeded", () => {
    const QUOTA = {
      code: "file.quota_exceeded",
      status: 402,
      args: { maxBytes: 1048576, usedBytes: 1000000, requestedBytes: 2097152 },
    };

    async function hitQuota() {
      renderTab();
      await pick(makeFile("Buyuk.pdf"));
      await waitFor(() => expect(uploads).toHaveLength(1));
      await act(async () => uploads[0]?.reject(problem(402, QUOTA)));
    }

    /** The toast body is a React node: render it the way the notification would. */
    const renderToastBody = (description: unknown) =>
      render(
        <I18nextProvider i18n={testI18n}>
          <MantineProvider env="test">
            <MemoryRouter>{description as never}</MemoryRouter>
          </MantineProvider>
        </I18nextProvider>
      );

    it("words the numbers in the row and toasts, with a link to 'Plan ve kullanım' for org.settings.manage", async () => {
      setPermissions([...WRITE, "org.settings.manage"]);
      await hitQuota();

      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Depolama kotası dolu. Kullanılan 976,6 KB, sınır 1 MB; bu dosya 2 MB."
      );
      expect(toast).toHaveBeenCalledTimes(1);
      const options = vi.mocked(toast).mock.calls[0]?.[0];
      expect(options).toMatchObject({ variant: "destructive", title: "Depolama kotası doldu" });
      renderToastBody(options?.description);
      expect(screen.getAllByText(/Depolama kotası dolu/).length).toBeGreaterThan(0);
      expect(screen.getByRole("button", { name: "Plan ve kullanım" })).toBeInTheDocument();
    });

    it("toasts without the link for a user who cannot manage the organization", async () => {
      await hitQuota();
      expect(await screen.findByRole("alert")).toBeInTheDocument();
      const options = vi.mocked(toast).mock.calls[0]?.[0];
      expect(typeof options?.description).toBe("string");
      expect(options?.description).toContain("Depolama kotası dolu");
    });
  });

  it("asks before leaving the page while uploads run, and stops asking once they are done", async () => {
    renderTab();
    await pick(makeFile("a.pdf"));
    await waitFor(() => expect(uploads).toHaveLength(1));

    const during = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(during);
    expect(during.defaultPrevented).toBe(true);

    await act(async () => uploads[0]?.resolve(ok(fileItem("n1"))));
    await waitFor(() => expect(rows()).toHaveLength(0));
    const after = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(after);
    expect(after.defaultPrevented).toBe(false);
  });

  it("a user without write permission gets no drop area and no way to start an upload", async () => {
    setPermissions(["crm.accounts.read"]);
    install();
    renderTab();
    await screen.findByTestId("files-empty");
    expect(screen.queryByTestId("file-dropzone")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Dosya seç" })).not.toBeInTheDocument();
  });

  it("does not request the file limits without the read permission", () => {
    setPermissions([]);
    install();
    renderTab();
    expect(client.get).not.toHaveBeenCalled();
  });
});
