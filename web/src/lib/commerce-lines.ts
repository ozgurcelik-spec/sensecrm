/**
 * Line item drafts of the quote / order editor: creation, conversion to and from the API shape,
 * client-side checks and the mapping of server `lines[i].field` errors onto grid cells.
 */
import type { DocumentLine, DocumentLineInput } from "@/types";

export const LINE_FIELDS = [
  "productId",
  "description",
  "quantity",
  "unitPrice",
  "discountPercent",
  "taxRate",
] as const;
export type LineField = (typeof LINE_FIELDS)[number];

/** Row index -> field -> message (an i18n key for client checks, plain text from the server). */
export type LineErrors = Record<number, Partial<Record<LineField, string>>>;

export const MAX_LINES = 100;

/**
 * Where the unit price of a row came from (M9C): a price book (`entry`, `flat`), the catalog, a vendor
 * purchase price. Editing the price by hand is an override and clears it.
 */
export type LinePriceSource = "entry" | "flat" | "catalog" | "purchase";

/** A grid row. Numeric fields keep what the number input holds (a number, or text while typing). */
export interface LineDraft {
  /** Client-only React key. */
  key: string;
  productId?: string;
  /** Name shown in the product picker for the selected product. */
  productLabel?: string;
  description: string;
  quantity: number | string;
  unitPrice: number | string;
  discountPercent: number | string;
  taxRate: number | string;
  /** Set right after a product pick / price resolution; cleared when the user types a price. */
  priceSource?: LinePriceSource;
}

let sequence = 0;
const nextKey = () => `line-${++sequence}`;

export function newLine(overrides: Partial<LineDraft> = {}): LineDraft {
  return {
    key: nextKey(),
    description: "",
    quantity: 1,
    unitPrice: 0,
    discountPercent: 0,
    taxRate: 0,
    ...overrides,
  };
}

export function linesFromServer(lines: readonly DocumentLine[]): LineDraft[] {
  return [...lines]
    .sort((a, b) => a.position - b.position)
    .map((line) =>
      newLine({
        productId: line.productId,
        productLabel: line.productId ? line.description : undefined,
        description: line.description,
        quantity: line.quantity,
        unitPrice: line.unitPrice,
        discountPercent: line.discountPercent,
        taxRate: line.taxRate,
      })
    );
}

function num(value: number | string): number {
  const parsed = typeof value === "number" ? value : Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

/** Request lines: only the contract fields, never the computed amounts. */
export function toLineInputs(lines: readonly LineDraft[]): DocumentLineInput[] {
  return lines.map((line) => ({
    ...(line.productId ? { productId: line.productId } : {}),
    description: line.description.trim(),
    quantity: num(line.quantity),
    unitPrice: num(line.unitPrice),
    discountPercent: num(line.discountPercent),
    taxRate: num(line.taxRate),
  }));
}

function decimals(value: number): number {
  const text = value.toString();
  // Exponent notation only shows up for tiny values (1e-7): far more decimals than allowed.
  if (text.includes("e")) return Number.POSITIVE_INFINITY;
  const [, fraction = ""] = text.split(".");
  return fraction.length;
}

function isBlank(value: number | string): boolean {
  return typeof value === "string" && value.trim() === "";
}

/** Client-side checks that mirror the contract; the messages are i18n keys. */
export function validateLines(lines: readonly LineDraft[]): LineErrors {
  const errors: LineErrors = {};
  const put = (index: number, field: LineField, message: string) => {
    (errors[index] ??= {})[field] = message;
  };
  lines.forEach((line, index) => {
    if (!line.description.trim()) put(index, "description", "auth:validation.required");
    else if (line.description.trim().length > 500) put(index, "description", "commerce:validation.descriptionMax");

    const quantity = num(line.quantity);
    if (isBlank(line.quantity) || quantity <= 0) put(index, "quantity", "commerce:validation.quantityPositive");
    else if (quantity > 1_000_000) put(index, "quantity", "commerce:validation.quantityMax");
    else if (decimals(quantity) > 4) put(index, "quantity", "commerce:validation.decimals4");

    const price = num(line.unitPrice);
    if (isBlank(line.unitPrice) || price < 0) put(index, "unitPrice", "commerce:validation.priceMin");
    else if (price > 1_000_000_000) put(index, "unitPrice", "commerce:validation.priceMax");
    else if (decimals(price) > 4) put(index, "unitPrice", "commerce:validation.decimals4");

    for (const field of ["discountPercent", "taxRate"] as const) {
      const value = num(line[field]);
      if (value < 0 || value > 100) put(index, field, "commerce:validation.percentRange");
      else if (decimals(value) > 2) put(index, field, "commerce:validation.decimals2");
    }
  });
  return errors;
}

export function hasLineErrors(errors: LineErrors): boolean {
  return Object.keys(errors).length > 0;
}

const LINE_KEY = /^lines\[(\d+)\]\.(\w+)$/i;

/**
 * Splits a ProblemDetails `errors` map into cell errors (`lines[3].quantity`) and everything else.
 * Keys that do not address a known cell (for example a bare `lines` about the 100 line limit) stay in `rest`.
 */
export function splitLineErrors(errors: Record<string, string[]> | undefined): {
  lineErrors: LineErrors;
  rest: Record<string, string[]>;
} {
  const lineErrors: LineErrors = {};
  const rest: Record<string, string[]> = {};
  for (const [key, messages] of Object.entries(errors ?? {})) {
    const match = LINE_KEY.exec(key);
    const field = match?.[2] ? match[2].charAt(0).toLowerCase() + match[2].slice(1) : undefined;
    if (match && field && (LINE_FIELDS as readonly string[]).includes(field) && messages[0]) {
      (lineErrors[Number(match[1])] ??= {})[field as LineField] = messages[0];
    } else {
      rest[key] = messages;
    }
  }
  return { lineErrors, rest };
}
