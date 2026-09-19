import { describe, expect, it } from "vitest";
import { createTestI18n } from "@/test-utils";
import {
  CAMPAIGN_STATUSES,
  CAMPAIGN_TRANSITIONS,
  MEMBER_MANUAL_STATUSES,
  MEMBER_STATUSES,
  isCampaignClosed,
} from "@/types";
import { formatCampaignDates, splitList, summarizeAddResult, summarizeStatusResult } from "./campaign";

import trCampaigns from "../../public/locales/tr/campaigns.json";
import enCampaigns from "../../public/locales/en/campaigns.json";
import trCommon from "../../public/locales/tr/common.json";
import enCommon from "../../public/locales/en/common.json";
import trNavigation from "../../public/locales/tr/navigation.json";
import enNavigation from "../../public/locales/en/navigation.json";
import trUsers from "../../public/locales/tr/users.json";
import enUsers from "../../public/locales/en/users.json";

/** "a.b.c" paths of every string leaf. */
function paths(value: unknown, prefix = ""): string[] {
  if (typeof value === "string") return [prefix];
  if (value && typeof value === "object") {
    return Object.entries(value).flatMap(([key, child]) =>
      paths(child, prefix ? `${prefix}.${key}` : key)
    );
  }
  return [];
}

function get(value: unknown, path: string): string {
  return path.split(".").reduce<unknown>((node, key) => (node as Record<string, unknown>)[key], value) as string;
}

const placeholders = (text: string) => [...text.matchAll(/{{\s*(\w+)\s*}}/g)].map((m) => m[1]).sort();

describe("campaign status transition table", () => {
  it("matches the binding contract table", () => {
    expect(CAMPAIGN_TRANSITIONS).toEqual({
      planned: ["active", "cancelled"],
      active: ["completed", "cancelled"],
      completed: ["active"],
      cancelled: ["planned"],
    });
  });

  it("never offers the current status as a target and covers every status", () => {
    for (const status of CAMPAIGN_STATUSES) {
      expect(CAMPAIGN_TRANSITIONS[status]).not.toContain(status);
      expect(CAMPAIGN_TRANSITIONS[status].length).toBeGreaterThan(0);
    }
  });

  it("treats completed and cancelled as closed", () => {
    expect(CAMPAIGN_STATUSES.filter(isCampaignClosed)).toEqual(["completed", "cancelled"]);
  });

  it("never lets a member status be set to converted by hand", () => {
    expect(MEMBER_MANUAL_STATUSES).not.toContain("converted");
    expect([...MEMBER_MANUAL_STATUSES, "converted"].sort()).toEqual([...MEMBER_STATUSES].sort());
  });
});

describe("helpers", () => {
  it("splits a comma separated URL value", () => {
    expect(splitList("planned,active")).toEqual(["planned", "active"]);
    expect(splitList("")).toEqual([]);
    expect(splitList(undefined)).toEqual([]);
  });

  it("formats the campaign dates", () => {
    expect(formatCampaignDates({})).toBe("-");
    expect(formatCampaignDates({ startDate: "2026-09-01", endDate: "2026-09-30" })).toContain(" - ");
  });

  it("summarizes a bulk add in Turkish and English", () => {
    const result = {
      addedCount: 3,
      alreadyMemberCount: 1,
      skipped: [
        { memberId: "a", reason: "lead_converted" as const },
        { memberId: "b", reason: "not_found" as const },
      ],
    };
    const tr = createTestI18n("tr");
    const en = createTestI18n("en");
    expect(summarizeAddResult(result, tr.t)).toBe(
      "3 eklendi, 1 zaten üyeydi, 2 atlandı (1 dönüşmüş, 1 bulunamadı)"
    );
    expect(summarizeAddResult(result, en.t)).toBe(
      "3 added, 1 already members, 2 skipped (1 converted, 1 not found)"
    );
    expect(summarizeAddResult({ addedCount: 0, alreadyMemberCount: 0, skipped: [] }, tr.t)).toBe(
      "Eklenecek yeni üye yok"
    );
  });

  it("summarizes a bulk status change", () => {
    const tr = createTestI18n("tr");
    expect(summarizeStatusResult({ updatedCount: 2, skippedCount: 0 }, tr.t)).toBe(
      "2 üyenin durumu güncellendi"
    );
    expect(summarizeStatusResult({ updatedCount: 2, skippedCount: 1 }, tr.t)).toBe(
      "2 üyenin durumu güncellendi, 1 dönüşmüş üye atlandı"
    );
  });
});

describe("TR / EN key parity", () => {
  it("has the same keys and placeholders in both campaigns.json files", () => {
    const trPaths = paths(trCampaigns).sort();
    const enPaths = paths(enCampaigns).sort();
    expect(trPaths).toEqual(enPaths);
    for (const path of trPaths) {
      expect(placeholders(get(enCampaigns, path)), path).toEqual(placeholders(get(trCampaigns, path)));
    }
  });

  it("has a label for every campaign type, status and member status in both languages", () => {
    for (const bundle of [trCampaigns, enCampaigns]) {
      for (const status of CAMPAIGN_STATUSES) expect(get(bundle, `statuses.${status}`)).toBeTruthy();
      for (const status of MEMBER_STATUSES) expect(get(bundle, `memberStatuses.${status}`)).toBeTruthy();
      for (const type of ["email", "event", "webinar", "advertising", "other"]) {
        expect(get(bundle, `types.${type}`)).toBeTruthy();
      }
    }
  });

  it("has the campaign error codes, navigation entry and permission labels in both languages", () => {
    const keys: [unknown, unknown, string][] = [
      [trCommon, enCommon, "errors.campaign.invalid_date_range"],
      [trCommon, enCommon, "errors.campaign.invalid_status_transition"],
      [trCommon, enCommon, "errors.campaign.closed"],
      [trCommon, enCommon, "selectAll"],
      [trNavigation, enNavigation, "campaigns"],
      [trUsers, enUsers, "permissions.crm.campaigns.read"],
      [trUsers, enUsers, "permissions.crm.campaigns.write"],
    ];
    for (const [tr, en, path] of keys) {
      expect(get(tr, path), `tr ${path}`).toBeTruthy();
      expect(get(en, path), `en ${path}`).toBeTruthy();
    }
  });
});
