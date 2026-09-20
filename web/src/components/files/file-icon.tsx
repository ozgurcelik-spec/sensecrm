import { File, FileImage, FileSpreadsheet, FileText, Presentation } from "lucide-react";
import { fileIconKind, type FileIconKind } from "@/lib/files";

const ICONS: Record<FileIconKind, typeof File> = {
  pdf: FileText,
  image: FileImage,
  document: FileText,
  sheet: FileSpreadsheet,
  presentation: Presentation,
  text: FileText,
  other: File,
};

const COLORS: Record<FileIconKind, string> = {
  pdf: "var(--mantine-color-red-6)",
  image: "var(--mantine-color-teal-6)",
  document: "var(--mantine-color-blue-6)",
  sheet: "var(--mantine-color-green-6)",
  presentation: "var(--mantine-color-orange-6)",
  text: "var(--mantine-color-gray-6)",
  other: "var(--mantine-color-gray-6)",
};

/** Icon by file extension (decorative: the file name next to it carries the meaning). */
export function FileIcon({ extension, size = 20 }: { extension: string; size?: number }) {
  const kind = fileIconKind(extension);
  const Icon = ICONS[kind];
  return (
    <Icon
      size={size}
      color={COLORS[kind]}
      aria-hidden="true"
      data-testid="file-icon"
      data-kind={kind}
    />
  );
}
