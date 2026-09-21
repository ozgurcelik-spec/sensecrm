import { useId, useRef, useState, type DragEvent } from "react";
import { useTranslation } from "react-i18next";
import { Button, Stack, Text } from "@mantine/core";
import { CloudUpload } from "lucide-react";
import { acceptAttribute, formatBytes } from "@/lib/files";
import type { FileLimits } from "@/types";

interface FileDropzoneProps {
  limits: FileLimits | undefined;
  onFiles: (files: File[]) => void;
  /** Smaller layout (activity dialog). */
  compact?: boolean;
}

const hasFiles = (event: DragEvent) => Array.from(event.dataTransfer?.types ?? []).includes("Files");

/**
 * Drop area (native drag and drop, no dependency) with a visible "select files" button and a hidden
 * multi-file input, so it works by keyboard and screen reader as well. The limits line is tied to
 * both through `aria-describedby`. It only hands the files over; validation and upload are the queue's.
 */
export function FileDropzone({ limits, onFiles, compact = false }: FileDropzoneProps) {
  const { t } = useTranslation(["files"]);
  const inputRef = useRef<HTMLInputElement>(null);
  const hintId = useId();
  const [dragging, setDragging] = useState(false);
  // dragenter / dragleave also fire for child elements: count them so the highlight does not flicker.
  const depth = useRef(0);

  function pick(list: FileList | null | undefined) {
    const files = Array.from(list ?? []);
    if (files.length > 0) onFiles(files);
  }

  function enter(event: DragEvent) {
    if (!hasFiles(event)) return;
    event.preventDefault();
    depth.current += 1;
    setDragging(true);
  }

  function over(event: DragEvent) {
    if (!hasFiles(event)) return;
    // Without preventDefault the browser navigates to the dropped file.
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
  }

  function leave(event: DragEvent) {
    if (!hasFiles(event)) return;
    depth.current = Math.max(0, depth.current - 1);
    if (depth.current === 0) setDragging(false);
  }

  function drop(event: DragEvent) {
    if (!hasFiles(event)) return;
    event.preventDefault();
    depth.current = 0;
    setDragging(false);
    pick(event.dataTransfer.files);
  }

  return (
    <Stack
      gap={compact ? 4 : "xs"}
      align="center"
      p={compact ? "sm" : "lg"}
      data-testid="file-dropzone"
      data-dragging={dragging ? "true" : undefined}
      onDragEnter={enter}
      onDragOver={over}
      onDragLeave={leave}
      onDrop={drop}
      style={{
        border: `2px dashed var(--mantine-color-${dragging ? "blue-6" : "gray-4"})`,
        borderRadius: "var(--mantine-radius-md)",
        background: dragging ? "var(--mantine-color-blue-light)" : undefined,
      }}
    >
      {!compact && <CloudUpload size={28} aria-hidden="true" />}
      <Text size="sm" ta="center">
        {dragging ? t("files:dropzone.release") : t("files:dropzone.prompt")}
      </Text>
      <Button
        type="button"
        variant="default"
        size={compact ? "xs" : "sm"}
        aria-describedby={hintId}
        onClick={() => inputRef.current?.click()}
      >
        {t("files:dropzone.select")}
      </Button>
      <Text size="xs" c="dimmed" ta="center" id={hintId}>
        {limits
          ? t("files:dropzone.limits", {
              maxSize: formatBytes(limits.maxFileBytes),
              types: limits.allowedExtensions.map((e) => e.toUpperCase()).join(", "),
            })
          : t("files:dropzone.limitsUnknown")}
      </Text>
      <input
        ref={inputRef}
        type="file"
        multiple
        hidden
        data-testid="file-input"
        aria-label={t("files:dropzone.select")}
        accept={acceptAttribute(limits)}
        onChange={(event) => {
          pick(event.currentTarget.files);
          // Picking the same file again must fire `change` again.
          event.currentTarget.value = "";
        }}
      />
    </Stack>
  );
}
