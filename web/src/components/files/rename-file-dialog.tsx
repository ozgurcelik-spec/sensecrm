import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Modal, Stack, TextInput } from "@mantine/core";
import { useRenameFile } from "@/hooks/use-files";
import { toast, toastApiError } from "@/hooks/use-toast";
import { getApiProblem } from "@/lib/api-error";
import { baseNameLength, sameExtension } from "@/lib/files";
import type { FileAttachment } from "@/types";

interface RenameFileDialogProps {
  file: FileAttachment;
  onClose: () => void;
}

/**
 * Renames a file. The whole name is editable but the base name (without the extension) is what is
 * selected on open, and a different extension is refused before any request; the server enforces the
 * same rule (`file.extension_change_not_allowed`, and `validation` on `name`), both land on the field.
 */
export function RenameFileDialog({ file, onClose }: RenameFileDialogProps) {
  const { t } = useTranslation(["files", "common"]);
  const rename = useRenameFile();
  const [name, setName] = useState(file.name);
  const [error, setError] = useState<string>();

  async function submit(event: FormEvent) {
    event.preventDefault();
    // The dialog can sit inside another form (activity dialog): the submit must not bubble to it
    // through the React tree.
    event.stopPropagation();
    const next = name.trim();
    if (!next) {
      setError(t("files:rename.required"));
      return;
    }
    if (!sameExtension(file.name, next)) {
      setError(t("files:errors.file.extension_change_not_allowed"));
      return;
    }
    if (next === file.name) {
      onClose();
      return;
    }
    setError(undefined);
    try {
      await rename.mutateAsync({ id: file.id, name: next });
      toast({ variant: "success", description: t("files:rename.done") });
      onClose();
    } catch (err) {
      const problem = getApiProblem(err);
      const nameErrors = problem?.errors && Object.entries(problem.errors).find(([key]) => key.toLowerCase() === "name")?.[1];
      if (problem?.code === "file.extension_change_not_allowed") {
        setError(t("files:errors.file.extension_change_not_allowed"));
      } else if (problem?.code === "file.name_invalid") {
        setError(t("files:errors.file.name_invalid"));
      } else if (nameErrors?.[0]) {
        setError(nameErrors[0]);
      } else {
        toastApiError(err);
      }
    }
  }

  return (
    <Modal opened onClose={onClose} title={t("files:rename.title")} centered>
      <form onSubmit={(event) => void submit(event)} noValidate>
        <Stack gap="md">
          <TextInput
            label={t("files:rename.label")}
            description={t("files:rename.hint")}
            value={name}
            maxLength={200}
            onChange={(event) => {
              setName(event.currentTarget.value);
              setError(undefined);
            }}
            error={error}
            // Focused on open (data-autofocus): select the base name so typing keeps the extension.
            onFocus={(event) => event.currentTarget.setSelectionRange(0, baseNameLength(file.name))}
            data-autofocus
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={rename.isPending}>
              {t("common:cancel")}
            </Button>
            <Button type="submit" loading={rename.isPending}>
              {t("common:save")}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
