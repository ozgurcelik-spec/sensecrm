import { describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders, createTestI18n } from "@/test-utils";
import type { Permission } from "@/types";
import { PermissionChecklist } from "./permission-checklist";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

const CATALOG: Permission[] = [
  { key: "org.users.read", group: "org" },
  { key: "org.workflows.manage", group: "org" },
  { key: "crm.approvals.decide", group: "crm" },
];

describe("PermissionChecklist - workflow permissions", () => {
  it("labels the new permissions in Turkish and toggles them", async () => {
    const onChange = vi.fn();
    renderWithProviders(<PermissionChecklist catalog={CATALOG} value={[]} onChange={onChange} />);

    await userEvent.click(
      screen.getByRole("checkbox", { name: "İş akışı kurallarını ve yürütmelerini yönetme" })
    );
    expect(onChange).toHaveBeenLastCalledWith(["org.workflows.manage"]);

    await userEvent.click(screen.getByRole("checkbox", { name: "Onay taleplerine karar verme" }));
    expect(onChange).toHaveBeenLastCalledWith(["crm.approvals.decide"]);
  });

  it("has English labels for the new permissions as well", () => {
    const en = createTestI18n("en");
    expect(en.t("users:permissions.org.workflows.manage")).toBe(
      "Manage workflow rules and executions"
    );
    expect(en.t("users:permissions.crm.approvals.decide")).toBe("Decide on approval requests");
    expect(en.t("navigation:workflows")).toBe("Workflows");
    expect(en.t("navigation:approvals")).toBe("My approvals");
    expect(en.t("common:errors.workflow.role_not_found")).toBeTruthy();
    expect(en.t("common:errors.approval.already_decided")).toBeTruthy();
  });
});
