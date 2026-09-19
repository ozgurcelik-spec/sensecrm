import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { apiClient } from "@/lib/api-client";
import { toastApiError } from "@/hooks/use-toast";
import { renderWithProviders } from "@/test-utils";
import { clearSession, installApi, problem, setPermissions, type MockClient } from "@/test/crm";
import { ROLES, dealRule, leadRule } from "@/test/workflows";
import { RuleFormDialog } from "./rule-form-dialog";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));
vi.mock("@/hooks/use-toast", () => ({ toast: vi.fn(), toastApiError: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  const client = { get: vi.fn(), post: vi.fn(), put: vi.fn(), patch: vi.fn(), delete: vi.fn() };
  return { ...actual, apiClient: client, default: client };
});

const client = apiClient as unknown as MockClient;

async function choose(label: string, option: string) {
  await userEvent.click(screen.getByRole("combobox", { name: new RegExp(`^${label}`) }));
  await userEvent.click(await screen.findByRole("option", { name: option }));
}

const setValue = (label: string, value: string) =>
  fireEvent.change(screen.getByLabelText(new RegExp(`^${label}`)), { target: { value } });

const submit = () => userEvent.click(screen.getByRole("button", { name: "Oluştur" }));

describe("RuleFormDialog", () => {
  const onClose = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    setPermissions(["org.workflows.manage"]);
    installApi(client, {
      "GET /organization/roles": () => ROLES,
      "POST /workflows/rules": () => ({ id: "new-rule" }),
      "PUT /workflows/rules/rule-1": () => undefined,
    });
  });
  afterEach(clearSession);

  const open = (rule?: Parameters<typeof RuleFormDialog>[0]["rule"]) =>
    renderWithProviders(<RuleFormDialog rule={rule} onClose={onClose} />);

  it("shows the lead assignment fields by default and swaps them when the kind changes", async () => {
    open();
    await screen.findByRole("combobox", { name: /^Atanacak rol/ });

    expect(screen.getByRole("combobox", { name: /^Potansiyel kaynakları/ })).toBeInTheDocument();
    expect(screen.getByLabelText(/^Takip süresi/)).toHaveValue("24");
    expect(screen.queryByLabelText(/^En düşük tutar/)).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: /^Onaylayıcı rol/ })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("combobox", { name: "Tür" }));
    await userEvent.click(await screen.findByRole("option", { name: "Fırsat onayı" }));

    expect(screen.getByLabelText(/^En düşük tutar/)).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: /^Onaylayıcı rol/ })).toBeInTheDocument();
    expect(screen.queryByLabelText(/^Takip süresi/)).not.toBeInTheDocument();
    expect(
      screen.queryByRole("combobox", { name: /^Potansiyel kaynakları/ })
    ).not.toBeInTheDocument();
  });

  it("validates the active kind client-side and sends nothing until it is valid", async () => {
    open();
    await screen.findByRole("combobox", { name: /^Atanacak rol/ });

    // Empty name and role.
    await submit();
    expect(await screen.findByText("Bu alan zorunludur")).toBeInTheDocument();
    expect(screen.getByText("Bir rol seçin")).toBeInTheDocument();

    // Out-of-range follow-up hours.
    setValue("Takip süresi", "721");
    await submit();
    expect(await screen.findByText("1 ile 720 arasında bir tam sayı girin")).toBeInTheDocument();
    setValue("Takip süresi", "0");
    await submit();
    expect(await screen.findByText("1 ile 720 arasında bir tam sayı girin")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();

    // Deal approval: amount must be greater than 0 and an approver role is required.
    await userEvent.click(screen.getByRole("combobox", { name: "Tür" }));
    await userEvent.click(await screen.findByRole("option", { name: "Fırsat onayı" }));
    setValue("En düşük tutar", "0");
    await submit();
    expect(await screen.findByText("0'dan büyük bir tutar girin")).toBeInTheDocument();
    expect(screen.getByText("Bir rol seçin")).toBeInTheDocument();
    expect(client.post).not.toHaveBeenCalled();
  });

  it("creates a lead assignment rule: empty sources are left out, hours are a number", async () => {
    open();
    await screen.findByRole("combobox", { name: /^Atanacak rol/ });

    await userEvent.type(screen.getByRole("textbox", { name: /^Kural adı/ }), "  Yeni kural ");
    await choose("Atanacak rol", "Satış Temsilcisi");
    setValue("Takip süresi", "48");
    await submit();

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post.mock.calls[0]).toEqual([
      "/workflows/rules",
      {
        name: "Yeni kural",
        kind: "leadAssignment",
        isEnabled: true,
        params: { assigneeRoleId: "role-sales", followUpHours: 48 },
      },
    ]);
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it("sends the chosen sources of a lead assignment rule", async () => {
    open();
    await screen.findByRole("combobox", { name: /^Atanacak rol/ });

    await userEvent.type(screen.getByRole("textbox", { name: /^Kural adı/ }), "Kaynaklı");
    await userEvent.click(screen.getByRole("combobox", { name: /^Potansiyel kaynakları/ }));
    await userEvent.click(await screen.findByRole("option", { name: "Web" }));
    await userEvent.click(await screen.findByRole("option", { name: "Tavsiye" }));
    await choose("Atanacak rol", "Satış Müdürü");
    await submit();

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post.mock.calls[0]?.[1]).toMatchObject({
      params: { sources: ["web", "referral"], assigneeRoleId: "role-manager", followUpHours: 24 },
    });
  });

  it("creates a deal approval rule with amount and approver role only", async () => {
    open();
    await screen.findByRole("combobox", { name: /^Atanacak rol/ });

    await userEvent.type(screen.getByRole("textbox", { name: /^Kural adı/ }), "Onay");
    await userEvent.click(screen.getByRole("combobox", { name: "Tür" }));
    await userEvent.click(await screen.findByRole("option", { name: "Fırsat onayı" }));
    setValue("En düşük tutar", "150000.5");
    await choose("Onaylayıcı rol", "Satış Müdürü");
    await submit();

    await waitFor(() => expect(client.post).toHaveBeenCalledTimes(1));
    expect(client.post.mock.calls[0]?.[1]).toEqual({
      name: "Onay",
      kind: "dealApproval",
      isEnabled: true,
      params: { minAmount: 150000.5, approverRoleId: "role-manager" },
    });
  });

  it("edits an existing rule: the kind is fixed and the enabled state is not sent", async () => {
    open(leadRule());
    await screen.findByDisplayValue("Web potansiyelleri");

    expect(screen.getByRole("combobox", { name: "Tür" })).toBeDisabled();
    expect(screen.getByLabelText(/^Takip süresi/)).toHaveValue("24");

    setValue("Takip süresi", "12");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    await waitFor(() => expect(client.put).toHaveBeenCalledTimes(1));
    expect(client.put.mock.calls[0]).toEqual([
      "/workflows/rules/rule-1",
      {
        name: "Web potansiyelleri",
        kind: "leadAssignment",
        params: { sources: ["web", "referral"], assigneeRoleId: "role-sales", followUpHours: 12 },
      },
    ]);
  });

  it("loads a deal approval rule into its own fields", async () => {
    open(dealRule());
    await screen.findByDisplayValue("Büyük fırsat onayı");
    expect(screen.getByLabelText(/^En düşük tutar/)).toHaveValue("100000");
    await waitFor(() =>
      expect(screen.getByRole("combobox", { name: /^Onaylayıcı rol/ })).toHaveValue("Satış Müdürü")
    );
  });

  it("maps server validation errors on params.<field> onto the fields", async () => {
    installApi(client, {
      "GET /organization/roles": () => ROLES,
      "POST /workflows/rules": () =>
        problem(400, {
          code: "validation",
          errors: {
            Name: ["Bu ad zaten kullanılıyor"],
            "params.followUpHours": ["Takip süresi 720 saati aşamaz"],
            "Params.Sources[1]": ["Bilinmeyen kaynak"],
          },
        }),
    });
    open();
    await screen.findByRole("combobox", { name: /^Atanacak rol/ });

    await userEvent.type(screen.getByRole("textbox", { name: /^Kural adı/ }), "Aynı ad");
    await choose("Atanacak rol", "Satış Temsilcisi");
    await submit();

    expect(await screen.findByText("Bu ad zaten kullanılıyor")).toBeInTheDocument();
    expect(screen.getByText("Takip süresi 720 saati aşamaz")).toBeInTheDocument();
    expect(screen.getByText("Bilinmeyen kaynak")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("maps workflow.role_not_found onto the role field of the selected kind", async () => {
    installApi(client, {
      "GET /organization/roles": () => ROLES,
      "POST /workflows/rules": () => problem(400, { code: "workflow.role_not_found" }),
    });
    open();
    await screen.findByRole("combobox", { name: /^Atanacak rol/ });

    await userEvent.type(screen.getByRole("textbox", { name: /^Kural adı/ }), "Silinmiş rol");
    await userEvent.click(screen.getByRole("combobox", { name: "Tür" }));
    await userEvent.click(await screen.findByRole("option", { name: "Fırsat onayı" }));
    setValue("En düşük tutar", "10");
    await choose("Onaylayıcı rol", "Satış Müdürü");
    await submit();

    expect(await screen.findByText("Seçilen rol bu organizasyonda bulunamadı")).toBeInTheDocument();
    expect(toastApiError).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("falls back to an error toast for errors that belong to no field", async () => {
    installApi(client, {
      "GET /organization/roles": () => ROLES,
      "POST /workflows/rules": () => problem(500, { title: "Boom" }),
    });
    open();
    await screen.findByRole("combobox", { name: /^Atanacak rol/ });

    await userEvent.type(screen.getByRole("textbox", { name: /^Kural adı/ }), "Hata");
    await choose("Atanacak rol", "Satış Temsilcisi");
    await submit();

    await waitFor(() => expect(toastApiError).toHaveBeenCalled());
    expect(onClose).not.toHaveBeenCalled();
  });
});
