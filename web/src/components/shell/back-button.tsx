import { useLocation, useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { ActionIcon } from "@mantine/core";
import { ArrowLeft } from "lucide-react";

/** One step back in the browser history; disabled on the first page of the session. */
export function BackButton() {
  const { t } = useTranslation(["common"]);
  const navigate = useNavigate();
  // Re-evaluated on every navigation; react-router keeps the entry index in history.state.
  useLocation();
  const canGoBack = ((window.history.state as { idx?: number } | null)?.idx ?? 0) > 0;

  return (
    <ActionIcon
      variant="subtle"
      color="gray"
      size="lg"
      disabled={!canGoBack}
      onClick={() => void navigate(-1)}
      aria-label={t("common:shell.back")}
    >
      <ArrowLeft size={20} />
    </ActionIcon>
  );
}
