import { useTranslation } from "react-i18next";
import { ActionIcon, Code, CopyButton, Group, Text, Tooltip } from "@mantine/core";
import { Check, Copy } from "lucide-react";

interface CodeBlockProps {
  code: string;
  /** Accessible name / caption of the block. */
  label?: string;
  testId?: string;
}

/** Monospace block with a copy button (JSON envelopes, `curl` and verification examples). */
export function CodeBlock({ code, label, testId }: CodeBlockProps) {
  const { t } = useTranslation(["integrations"]);
  return (
    <div>
      <Group justify={label ? "space-between" : "flex-end"} mb={4} wrap="nowrap">
        {label && (
          <Text size="xs" c="dimmed">
            {label}
          </Text>
        )}
        <CopyButton value={code} timeout={2000}>
          {({ copied, copy }) => (
            <Tooltip label={copied ? t("integrations:reveal.copied") : t("integrations:reveal.copy")}>
              <ActionIcon
                variant="subtle"
                size="sm"
                color={copied ? "green" : "gray"}
                aria-label={label ? t("integrations:code.copyNamed", { name: label }) : t("integrations:reveal.copy")}
                onClick={copy}
              >
                {copied ? <Check size={14} /> : <Copy size={14} />}
              </ActionIcon>
            </Tooltip>
          )}
        </CopyButton>
      </Group>
      <Code block data-testid={testId} aria-label={label} style={{ whiteSpace: "pre", overflowX: "auto" }}>
        {code}
      </Code>
    </div>
  );
}
