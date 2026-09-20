import { describe, expect, it } from "vitest";
import { permissionModule } from "@/lib/entitlements";
import { PERMISSIONS } from "@/types";
import {
  availableInvoiceActions,
  availablePurchaseOrderActions,
  availableQuoteActions,
  orderInvoiceAction,
} from "./commerce-actions";

describe("availableQuoteActions (negotiation)", () => {
  const ctx = { canWriteQuotes: true, canWriteOrders: false, converted: false };

  it("a sent quote may negotiate, accept, reject, extend and revert", () => {
    expect(availableQuoteActions("sent", ctx)).toEqual(["negotiate", "accept", "reject", "extend", "revert"]);
  });

  it("a quote in negotiation may accept, reject, extend and revert but not negotiate again", () => {
    expect(availableQuoteActions("negotiation", ctx)).toEqual(["accept", "reject", "extend", "revert"]);
  });

  it("an expired quote (a derived status) never negotiates", () => {
    expect(availableQuoteActions("expired", ctx)).not.toContain("negotiate");
  });

  it("a read-only user gets nothing on either status", () => {
    const readOnly = { ...ctx, canWriteQuotes: false };
    expect(availableQuoteActions("sent", readOnly)).toEqual([]);
    expect(availableQuoteActions("negotiation", readOnly)).toEqual([]);
  });
});

describe("orderInvoiceAction", () => {
  const perms = { canWriteInvoices: true, canReadInvoices: true };

  it.each([
    ["confirmed", "create"],
    ["fulfilled", "create"],
    ["draft", null],
    ["cancelled", null],
  ] as const)("%s without an invoice -> %s", (status, expected) => {
    expect(orderInvoiceAction({ status }, perms)).toBe(expected);
  });

  it("needs crm.invoices.write to create", () => {
    expect(orderInvoiceAction({ status: "confirmed" }, { canWriteInvoices: false, canReadInvoices: true })).toBeNull();
  });

  it("an active invoice turns the action into a link (with read access) and never a second conversion", () => {
    expect(orderInvoiceAction({ status: "confirmed", invoiceId: "i1" }, perms)).toBe("link");
    expect(orderInvoiceAction({ status: "fulfilled", invoiceId: "i1" }, perms)).toBe("link");
    expect(orderInvoiceAction({ status: "confirmed", invoiceId: "i1" }, { canWriteInvoices: true, canReadInvoices: false })).toBeNull();
  });
});

describe("availableInvoiceActions", () => {
  const write = (paidAmount = 0) => ({ canWriteInvoices: true, paidAmount });

  it("draft: edit, send, cancel, delete", () => {
    expect(availableInvoiceActions("draft", write())).toEqual(["edit", "send", "cancel", "delete"]);
  });

  it.each(["sent", "overdue", "partiallyPaid"] as const)("%s without payments: pay, revert, cancel", (status) => {
    expect(availableInvoiceActions(status, write())).toEqual(["pay", "revert", "cancel"]);
  });

  it.each(["sent", "overdue", "partiallyPaid"] as const)("%s with payments: only pay (revert and cancel need no payments)", (status) => {
    expect(availableInvoiceActions(status, write(10))).toEqual(["pay"]);
  });

  it.each(["paid", "cancelled"] as const)("%s: view only", (status) => {
    expect(availableInvoiceActions(status, write())).toEqual([]);
  });

  it("nothing without crm.invoices.write", () => {
    expect(availableInvoiceActions("draft", { canWriteInvoices: false, paidAmount: 0 })).toEqual([]);
    expect(availableInvoiceActions("sent", { canWriteInvoices: false, paidAmount: 0 })).toEqual([]);
  });
});

describe("availablePurchaseOrderActions", () => {
  it("follows the lifecycle draft -> confirmed -> received", () => {
    expect(availablePurchaseOrderActions("draft", true)).toEqual(["edit", "confirm", "cancel", "delete"]);
    expect(availablePurchaseOrderActions("confirmed", true)).toEqual(["receive", "cancel"]);
    expect(availablePurchaseOrderActions("received", true)).toEqual([]);
    expect(availablePurchaseOrderActions("cancelled", true)).toEqual([]);
  });

  it("needs crm.purchaseorders.write", () => {
    expect(availablePurchaseOrderActions("draft", false)).toEqual([]);
  });
});

describe("plan module of the new permissions", () => {
  it.each([
    PERMISSIONS.crmInvoicesRead,
    PERMISSIONS.crmInvoicesWrite,
    PERMISSIONS.crmPriceBooksRead,
    PERMISSIONS.crmPriceBooksWrite,
    PERMISSIONS.crmVendorsRead,
    PERMISSIONS.crmVendorsWrite,
    PERMISSIONS.crmPurchaseOrdersRead,
    PERMISSIONS.crmPurchaseOrdersWrite,
  ])("%s belongs to the commerce module", (permission) => {
    expect(permissionModule(permission)).toBe("commerce");
  });
});
