import { afterEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test-utils";
import { caseItem } from "@/test/service";
import { CasePriorityBadge, CaseStatusBadge, SlaBadge } from "./case-badges";

vi.mock("@/i18n", async () => ({ default: (await import("@/test-utils")).testI18n }));

const stateOf = (label: string) => screen.getByText(label).closest("[data-sla-state]") as HTMLElement;

describe("SlaBadge", () => {
  afterEach(() => vi.useRealTimers());

  it("shows ok (green), at risk (yellow) and breached (red) for an active case", () => {
    renderWithProviders(
      <>
        <SlaBadge item={caseItem("1", { slaState: "ok" })} />
        <SlaBadge item={caseItem("2", { slaState: "atRisk" })} />
        <SlaBadge
          item={caseItem("3", {
            slaState: "breached",
            isSlaBreached: true,
            resolutionBreached: true,
          })}
        />
      </>
    );
    expect(stateOf("Zamanında")).toHaveAttribute("data-sla-state", "ok");
    expect(stateOf("Zamanında").getAttribute("style")).toContain("green");
    expect(stateOf("Risk altında")).toHaveAttribute("data-sla-state", "atRisk");
    expect(stateOf("Risk altında").getAttribute("style")).toContain("yellow");
    expect(stateOf("Aşıldı")).toHaveAttribute("data-sla-state", "breached");
    expect(stateOf("Aşıldı").getAttribute("style")).toContain("red");
  });

  it("lists both targets with the time left or past in the tooltip", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-09-19T10:00:00Z"));
    renderWithProviders(
      <SlaBadge
        item={caseItem("1", {
          slaState: "breached",
          isSlaBreached: true,
          firstResponseAt: undefined,
          firstResponseDueAt: "2026-09-19T09:20:00Z",
          firstResponseBreached: true,
          dueAt: "2026-09-19T12:15:00Z",
        })}
      />
    );
    await userEvent.hover(screen.getByText("Aşıldı"));
    expect(await screen.findByText(/İlk yanıt: hedef .* · 40 dk geçti/)).toBeInTheDocument();
    expect(screen.getByText(/Çözüm: hedef .* · 2 sa 15 dk kaldı/)).toBeInTheDocument();
  });

  it("shows no badge for a finished case unless its SLA was breached", () => {
    renderWithProviders(
      <>
        <SlaBadge item={caseItem("1", { status: "resolved", resolvedAt: "2026-09-19T10:00:00Z" })} />
        <SlaBadge item={caseItem("2", { status: "closed", closedAt: "2026-09-19T10:00:00Z" })} />
        <SlaBadge
          item={caseItem("3", {
            status: "closed",
            isSlaBreached: true,
            slaState: "breached",
            resolutionBreached: true,
          })}
        />
      </>
    );
    expect(screen.queryByText("Zamanında")).toBeNull();
    expect(screen.getAllByText("-")).toHaveLength(2);
    expect(screen.getByText("Aşıldı")).toBeInTheDocument();
  });
});

describe("status and priority badges", () => {
  it("translate the enum values", () => {
    renderWithProviders(
      <>
        <CaseStatusBadge status="pending" />
        <CasePriorityBadge priority="urgent" />
      </>
    );
    expect(screen.getByText("Beklemede")).toBeInTheDocument();
    expect(screen.getByText("Acil")).toBeInTheDocument();
  });
});
