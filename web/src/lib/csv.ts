import { intlLocale } from "@/lib/dates";

export type CsvCell = string | number | null | undefined;

/** Excel in Turkish (and other decimal-comma) locales splits on `;`; English Excel on `,`. */
export function csvDelimiter(locale: string = intlLocale()): string {
  return locale.startsWith("tr") ? ";" : ",";
}

function formatCell(cell: CsvCell, locale: string): { text: string; isNumber: boolean } {
  if (cell === null || cell === undefined) return { text: "", isNumber: false };
  if (typeof cell === "number") {
    // Decimal comma in Turkish through Intl; grouping is off so a spreadsheet re-parses it as a number.
    const text = Number.isFinite(cell)
      ? new Intl.NumberFormat(locale, { useGrouping: false, maximumFractionDigits: 2 }).format(cell)
      : "";
    return { text, isNumber: true };
  }
  return { text: cell, isNumber: false };
}

function escapeCell(cell: CsvCell, delimiter: string, locale: string): string {
  const { text, isNumber } = formatCell(cell, locale);
  // Spreadsheet formula injection: text that starts with one of these would be evaluated.
  const safe = !isNumber && /^[=+\-@\t\r]/.test(text) ? `'${text}` : text;
  return safe.includes(delimiter) || /["\r\n]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe;
}

/** RFC 4180 style CSV: quoted when needed, `"` doubled, CRLF line breaks, no trailing newline. */
export function toCsv(
  headers: readonly string[],
  rows: readonly (readonly CsvCell[])[],
  locale: string = intlLocale()
): string {
  const delimiter = csvDelimiter(locale);
  return [headers, ...rows]
    .map((row) => row.map((cell) => escapeCell(cell, delimiter, locale)).join(delimiter))
    .join("\r\n");
}

/** UTF-8 byte order mark: makes Excel read Turkish characters correctly. */
export const CSV_BOM = "﻿";

export function csvBlob(csv: string): Blob {
  return new Blob([CSV_BOM, csv], { type: "text/csv;charset=utf-8" });
}

/** Saves `csv` as a file through a temporary link (client side only, no request). */
export function downloadCsv(filename: string, csv: string): void {
  const url = URL.createObjectURL(csvBlob(csv));
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
