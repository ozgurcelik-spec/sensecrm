/**
 * App-wide `toast()` backed by Mantine notifications. Callable from anywhere (mutation callbacks,
 * interceptors) without a hook; `<Notifications />` is mounted in `MantineRoot`.
 */
import { createElement, type ReactNode } from "react";
import { notifications } from "@mantine/notifications";
import { PlanLimitMessage } from "@/components/subscription/plan-limit-message";
import i18n from "@/i18n";
import { getApiErrorMessage, getApiProblem } from "@/lib/api-error";
import { PLAN_LIMIT_EXCEEDED } from "@/lib/entitlement-errors";

export interface ToastOptions {
  title?: ReactNode;
  description?: ReactNode;
  variant?: "default" | "success" | "destructive";
  /** Auto close delay in ms (default 5000); `false` keeps it open. */
  duration?: number | false;
}

const COLOR: Record<NonNullable<ToastOptions["variant"]>, string | undefined> = {
  default: undefined,
  success: "green",
  destructive: "red",
};

export function toast({ title, description, variant = "default", duration = 5000 }: ToastOptions) {
  const id = notifications.show({
    title,
    message: description ?? "",
    color: COLOR[variant],
    autoClose: duration,
  });
  return { id, dismiss: () => notifications.hide(id) };
}

/** Red toast with the translated API error (`common:errors.<code>`, then title, then generic). */
export function toastApiError(error: unknown) {
  const message = getApiErrorMessage(error);
  return toast({
    variant: "destructive",
    title: i18n.t("common:errors.title"),
    // A reached plan limit links to "Plan ve kullanım"; the form / dialog stays open (the caller decides).
    description:
      getApiProblem(error)?.code === PLAN_LIMIT_EXCEEDED
        ? createElement(PlanLimitMessage, { message })
        : message,
  });
}
