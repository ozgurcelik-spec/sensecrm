/** Fixtures of the M9C tests (docs/plan/m9c-envanter.md): invoices, price books, vendors, purchase orders. */

export const ACCOUNT = {
  id: "a1",
  name: "Acme Ltd",
  ownerUserId: "user-1",
  createdAt: "2026-05-01T10:00:00Z",
};

export const CONTACT = { id: "c1", lastName: "Yılmaz", fullName: "Ayşe Yılmaz", accountId: "a1" };

export function line(overrides: Record<string, unknown> = {}) {
  return {
    id: "l1",
    position: 0,
    description: "CRM Pro lisansı",
    quantity: 2,
    unitPrice: 100,
    discountPercent: 0,
    taxRate: 20,
    lineSubtotal: 200,
    lineDiscount: 0,
    lineTax: 40,
    lineTotal: 240,
    ...overrides,
  };
}

export function payment(overrides: Record<string, unknown> = {}) {
  return {
    id: "pay1",
    amount: 50,
    paidOn: "2026-09-10",
    method: "bankTransfer",
    reference: "EFT-1",
    recordedByUserId: "user-1",
    recordedAt: "2026-09-10T09:00:00Z",
    ...overrides,
  };
}

export function invoice(overrides: Record<string, unknown> = {}) {
  return {
    id: "i1",
    number: "INV-2026-0001",
    subject: "Yıllık lisans faturası",
    status: "draft",
    accountId: "a1",
    accountName: "Acme Ltd",
    ownerUserId: "user-1",
    ownerName: "Ada Lovelace",
    currency: "TRY",
    subtotal: 200,
    discountTotal: 0,
    taxTotal: 40,
    adjustment: 0,
    grandTotal: 240,
    paidAmount: 0,
    balanceAmount: 240,
    invoiceDate: "2026-09-01",
    dueDate: "2026-10-01",
    createdAt: "2026-09-01T10:00:00Z",
    payments: [],
    lines: [line()],
    ...overrides,
  };
}

export function invoiceSummary(overrides: Record<string, unknown> = {}) {
  const summary: Record<string, unknown> = { ...invoice(overrides) };
  delete summary.lines;
  delete summary.payments;
  return summary;
}

export function priceBook(overrides: Record<string, unknown> = {}) {
  return {
    id: "pb1",
    name: "Kurumsal liste",
    ownerUserId: "user-1",
    ownerName: "Ada Lovelace",
    isActive: true,
    pricingModel: "perProduct",
    currency: "TRY",
    isEffective: true,
    entryCount: 1,
    createdAt: "2026-09-01T10:00:00Z",
    ...overrides,
  };
}

export function vendor(overrides: Record<string, unknown> = {}) {
  return {
    id: "v1",
    name: "Tedarik A.Ş.",
    ownerUserId: "user-1",
    ownerName: "Ada Lovelace",
    category: "Donanım",
    phone: "0212 000 00 00",
    email: "info@tedarik.example",
    emailOptOut: false,
    productCount: 1,
    purchaseOrderCount: 1,
    createdAt: "2026-09-01T10:00:00Z",
    ...overrides,
  };
}

export function purchaseOrder(overrides: Record<string, unknown> = {}) {
  return {
    id: "po1",
    number: "PO-2026-0001",
    subject: "Sunucu alımı",
    status: "draft",
    vendorId: "v1",
    vendorName: "Tedarik A.Ş.",
    ownerUserId: "user-1",
    ownerName: "Ada Lovelace",
    currency: "TRY",
    subtotal: 200,
    discountTotal: 0,
    taxTotal: 40,
    adjustment: 0,
    grandTotal: 240,
    poDate: "2026-09-01",
    createdAt: "2026-09-01T10:00:00Z",
    lines: [line({ description: "Sunucu" })],
    ...overrides,
  };
}

export function product(overrides: Record<string, unknown> = {}) {
  return {
    id: "p1",
    name: "CRM Pro",
    code: "CRM",
    unitPrice: 100,
    currency: "TRY",
    taxRate: 20,
    isActive: true,
    createdAt: "2026-05-01T10:00:00Z",
    ...overrides,
  };
}
