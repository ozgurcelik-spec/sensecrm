import { describe, expect, it, vi } from "vitest";
import { AxiosError } from "axios";
import { problem } from "@/test/crm";
import { getApiErrorMessage } from "./api-error";
import { describeFileError, fileErrorText } from "./file-errors";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

describe("fileErrorText", () => {
  it("words the byte counts of a quota error as sizes", () => {
    expect(
      fileErrorText("file.quota_exceeded", {
        maxBytes: 1073741824,
        usedBytes: 1048576,
        requestedBytes: 5242880,
      })
    ).toBe("Depolama kotası dolu. Kullanılan 1 MB, sınır 1 GB; bu dosya 5 MB.");
  });

  it("words the size limit, extension and file count", () => {
    expect(fileErrorText("file.too_large", { maxBytes: 26214400 })).toBe(
      "Dosya çok büyük. En çok 25 MB yüklenebilir."
    );
    expect(fileErrorText("file.type_not_allowed", { extension: "exe" })).toBe(
      "Bu dosya türüne izin verilmiyor (.exe)."
    );
    expect(fileErrorText("file.too_many_files", { max: 10 })).toBe(
      "Tek seferde en çok 10 dosya yüklenebilir."
    );
  });

  it("falls back to the common texts (forbidden, not_found) and to the generic message for unknown codes", () => {
    expect(fileErrorText("forbidden")).toBe("Bu işlem için yetkiniz yok");
    expect(fileErrorText("not_found")).toBe("Kayıt bulunamadı");
    expect(fileErrorText("file.something_new")).toBe("Beklenmeyen bir hata oluştu");
  });
});

describe("describeFileError / getApiErrorMessage", () => {
  it("returns the code, args and translated message of a ProblemDetails", () => {
    const error = problem(413, { code: "file.too_large", status: 413, args: { maxBytes: 26214400 } });
    expect(describeFileError(error)).toEqual({
      code: "file.too_large",
      args: { maxBytes: 26214400 },
      message: "Dosya çok büyük. En çok 25 MB yüklenebilir.",
    });
  });

  it("toasts (getApiErrorMessage) know the file codes too", () => {
    expect(getApiErrorMessage(problem(410, { code: "file.content_missing" }))).toBe(
      "Dosyanın içeriğine ulaşılamıyor. Dosyayı silip yeniden yükleyin."
    );
    expect(getApiErrorMessage(problem(409, { code: "file.quarantined" }))).toBe(
      "Dosya karantinada olduğu için indirilemez."
    );
    expect(getApiErrorMessage(problem(402, { code: "file.quota_exceeded", args: { maxBytes: 1048576 } }))).toContain(
      "sınır 1 MB"
    );
  });

  it("treats a bare 413 (proxy body limit, no ProblemDetails) as too large", () => {
    expect(describeFileError(problem(413, {}))).toMatchObject({ code: "file.too_large" });
    expect(describeFileError(problem(413, { html: "x" })).message).toBe(
      "Dosya çok büyük. Sunucunun kabul ettiği boyutu aşıyor."
    );
  });

  it("uses the network message when the server cannot be reached", () => {
    const error = new AxiosError("Network Error", "ERR_NETWORK");
    expect(describeFileError(error)).toEqual({
      code: undefined,
      args: undefined,
      message: "Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.",
    });
  });
});
