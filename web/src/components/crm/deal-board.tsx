import { useMemo, useState } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import {
  DndContext,
  DragOverlay,
  KeyboardSensor,
  MouseSensor,
  TouchSensor,
  closestCorners,
  useDraggable,
  useDroppable,
  useSensor,
  useSensors,
  type Announcements,
  type DragEndEvent,
  type DragStartEvent,
  type KeyboardCoordinateGetter,
} from "@dnd-kit/core";
import {
  ActionIcon,
  Anchor,
  Badge,
  Card,
  Group,
  Menu,
  ScrollArea,
  Stack,
  Text,
} from "@mantine/core";
import { GripVertical, MoveRight } from "lucide-react";
import { useMoveDealStage } from "@/hooks/use-deals";
import { toast, toastApiError } from "@/hooks/use-toast";
import { formatCalendarDate, formatMoney } from "@/lib/format";
import { stageColor } from "@/lib/stage";
import type { BoardStage, DealBoard, DealSummary } from "@/types";
import type { DealBoardQuery } from "@/services/deals.service";
import { LostReasonDialog } from "./lost-reason-dialog";

/** Keyboard drag: left/right arrows jump to the neighbouring column (default nudges by 25px). */
const columnCoordinateGetter: KeyboardCoordinateGetter = (
  event,
  { context: { active, droppableRects, droppableContainers, collisionRect } }
) => {
  if (event.code !== "ArrowRight" && event.code !== "ArrowLeft") return undefined;
  event.preventDefault();
  if (!active || !collisionRect) return undefined;

  const columns = droppableContainers
    .getEnabled()
    .map((container) => ({ id: container.id, rect: droppableRects.get(container.id) }))
    .filter((c): c is { id: typeof c.id; rect: NonNullable<typeof c.rect> } => !!c.rect)
    .sort((a, b) => a.rect.left - b.rect.left);
  if (columns.length === 0) return undefined;

  const centre = collisionRect.left + collisionRect.width / 2;
  let currentIndex = columns.findIndex((c) => centre >= c.rect.left && centre <= c.rect.right);
  if (currentIndex === -1) currentIndex = 0;
  const nextIndex = Math.min(
    columns.length - 1,
    Math.max(0, currentIndex + (event.code === "ArrowRight" ? 1 : -1))
  );
  const target = columns[nextIndex]?.rect;
  if (!target) return undefined;
  return { x: target.left + 12, y: target.top + 12 };
};

interface CardProps {
  deal: DealSummary;
  stages: BoardStage[];
  currentStageId: string;
  canMove: boolean;
  onMove: (dealId: string, stageId: string) => void;
  overlay?: boolean;
}

function DealCardBody({
  deal,
  stages,
  currentStageId,
  canMove,
  onMove,
  handle,
}: CardProps & { handle?: React.ReactNode }) {
  const { t } = useTranslation(["crm"]);
  return (
    <Card withBorder padding="xs" radius="md" shadow="xs">
      <Group justify="space-between" wrap="nowrap" align="flex-start" gap={4}>
        <Anchor
          component={Link}
          to={`/app/deals/${deal.id}`}
          size="sm"
          fw={600}
          style={{ wordBreak: "break-word" }}
        >
          {deal.name}
        </Anchor>
        <Group gap={2} wrap="nowrap">
          {handle}
          {canMove && (
            <Menu position="bottom-end" withinPortal>
              <Menu.Target>
                <ActionIcon
                  size="sm"
                  variant="subtle"
                  aria-label={t("crm:deals.board.moveTo", { name: deal.name })}
                >
                  <MoveRight size={14} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Label>{t("crm:deals.board.moveToStage")}</Menu.Label>
                {stages
                  .filter((s) => s.id !== currentStageId)
                  .map((s) => (
                    <Menu.Item key={s.id} onClick={() => onMove(deal.id, s.id)}>
                      {s.name}
                    </Menu.Item>
                  ))}
              </Menu.Dropdown>
            </Menu>
          )}
        </Group>
      </Group>
      <Text size="xs" c="dimmed" mt={2}>
        {deal.accountName}
      </Text>
      <Group justify="space-between" mt={6} wrap="nowrap">
        <Text size="sm" fw={500}>
          {deal.amount !== undefined ? formatMoney(deal.amount, deal.currency) : "-"}
        </Text>
        {deal.closingDate && (
          <Text size="xs" c="dimmed">
            {formatCalendarDate(deal.closingDate)}
          </Text>
        )}
      </Group>
      {deal.ownerName && (
        <Text size="xs" c="dimmed" mt={2}>
          {deal.ownerName}
        </Text>
      )}
    </Card>
  );
}

function DraggableDeal(props: CardProps) {
  const { t } = useTranslation(["crm"]);
  const { deal, currentStageId, canMove } = props;
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, isDragging } = useDraggable({
    id: deal.id,
    data: { stageId: currentStageId, name: deal.name },
    disabled: !canMove,
  });

  const handle = canMove ? (
    <ActionIcon
      size="sm"
      variant="subtle"
      ref={setActivatorNodeRef}
      aria-label={t("crm:deals.board.drag", { name: deal.name })}
      style={{ cursor: "grab", touchAction: "none" }}
      {...attributes}
    >
      <GripVertical size={14} />
    </ActionIcon>
  ) : undefined;

  return (
    // Pointer/touch drags start anywhere on the card; the keyboard drag starts on the handle.
    <div
      ref={setNodeRef}
      {...(canMove ? listeners : {})}
      style={{ opacity: isDragging ? 0.4 : 1, touchAction: "manipulation" }}
      data-testid={`deal-card-${deal.id}`}
    >
      <DealCardBody {...props} handle={handle} />
    </div>
  );
}

function StageColumn({
  stage,
  stages,
  canMove,
  onMove,
}: {
  stage: BoardStage;
  stages: BoardStage[];
  canMove: boolean;
  onMove: (dealId: string, stageId: string) => void;
}) {
  const { t } = useTranslation(["crm"]);
  const { setNodeRef, isOver } = useDroppable({ id: stage.id });
  const hidden = Math.max(0, stage.count - stage.deals.length);

  return (
    <Stack
      ref={setNodeRef}
      gap="xs"
      p="xs"
      w={290}
      miw={290}
      role="group"
      aria-label={stage.name}
      data-testid={`stage-${stage.id}`}
      style={{
        borderRadius: "var(--mantine-radius-md)",
        background: isOver
          ? "var(--mantine-color-brand-light)"
          : "var(--mantine-color-default-hover)",
        outline: isOver ? "2px dashed var(--mantine-color-brand-filled)" : undefined,
        alignSelf: "stretch",
      }}
    >
      <Group justify="space-between" wrap="nowrap">
        <Group gap={6} wrap="nowrap">
          <Text fw={600} size="sm">
            {stage.name}
          </Text>
          <Badge
            size="sm"
            variant="light"
            color={stageColor(stage.kind)}
            aria-label={t("crm:deals.board.count", { count: stage.count })}
          >
            {stage.count}
          </Badge>
        </Group>
        <Text size="xs" c="dimmed">
          {stage.probability}%
        </Text>
      </Group>
      <Text size="xs" c="dimmed" data-testid={`stage-total-${stage.id}`}>
        {formatMoney(stage.totalAmount)}
      </Text>
      {stage.deals.map((deal) => (
        <DraggableDeal
          key={deal.id}
          deal={deal}
          stages={stages}
          currentStageId={stage.id}
          canMove={canMove}
          onMove={onMove}
        />
      ))}
      {stage.deals.length === 0 && (
        <Text size="xs" c="dimmed" ta="center" py="md">
          {t("crm:deals.board.emptyColumn")}
        </Text>
      )}
      {hidden > 0 && (
        <Anchor
          component={Link}
          to={`/app/deals?view=list&stageId=${encodeURIComponent(stage.id)}`}
          size="xs"
          ta="center"
        >
          {t("crm:deals.board.more", { count: hidden })}
        </Anchor>
      )}
    </Stack>
  );
}

interface DealBoardViewProps {
  board: DealBoard;
  boardQuery: DealBoardQuery;
  canMove: boolean;
}

/**
 * Kanban board: one column per pipeline stage. Cards move by drag and drop (mouse, touch, or keyboard:
 * Space on the handle, arrows to change column, Space to drop) or through each card's "move to" menu.
 * The move is optimistic and rolled back if the server rejects it; dropping on a "lost" stage asks for
 * the lost reason first.
 */
export function DealBoardView({ board, boardQuery, canMove }: DealBoardViewProps) {
  const { t } = useTranslation(["crm", "common", "auth"]);
  const move = useMoveDealStage(boardQuery);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [pendingLost, setPendingLost] = useState<{
    dealId: string;
    dealName: string;
    stageId: string;
  } | null>(null);

  const sensors = useSensors(
    useSensor(MouseSensor, { activationConstraint: { distance: 6 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 8 } }),
    useSensor(KeyboardSensor, { coordinateGetter: columnCoordinateGetter })
  );

  const dealsById = useMemo(() => {
    const map = new Map<string, { deal: DealSummary; stage: BoardStage }>();
    for (const stage of board.stages)
      for (const deal of stage.deals) map.set(deal.id, { deal, stage });
    return map;
  }, [board]);

  function requestMove(dealId: string, stageId: string) {
    const from = dealsById.get(dealId);
    const target = board.stages.find((s) => s.id === stageId);
    if (!from || !target || from.stage.id === target.id) return;

    if (target.kind === "lost") {
      setPendingLost({ dealId, dealName: from.deal.name, stageId });
      return;
    }
    move.mutate(
      { dealId, stageId },
      {
        onSuccess: () =>
          toast({
            variant: "success",
            description: t("crm:deals.board.moved", { name: from.deal.name, stage: target.name }),
          }),
        onError: (error) => toastApiError(error),
      }
    );
  }

  function confirmLost(reason: string) {
    if (!pendingLost) return;
    const { dealId, stageId, dealName } = pendingLost;
    move.mutate(
      { dealId, stageId, lostReason: reason },
      {
        onSuccess: () => {
          setPendingLost(null);
          toast({
            variant: "success",
            description: t("crm:deals.board.moved", {
              name: dealName,
              stage: board.stages.find((s) => s.id === stageId)?.name ?? "",
            }),
          });
        },
        onError: (error) => {
          setPendingLost(null);
          toastApiError(error);
        },
      }
    );
  }

  function handleDragStart(event: DragStartEvent) {
    setActiveId(String(event.active.id));
  }

  function handleDragEnd(event: DragEndEvent) {
    setActiveId(null);
    if (event.over) requestMove(String(event.active.id), String(event.over.id));
  }

  const stageName = (id: string | number) =>
    board.stages.find((s) => s.id === String(id))?.name ?? "";
  const dealName = (id: string | number) => dealsById.get(String(id))?.deal.name ?? "";

  const announcements: Announcements = {
    onDragStart: ({ active }) => t("crm:deals.board.a11y.pickedUp", { name: dealName(active.id) }),
    onDragOver: ({ active, over }) =>
      over
        ? t("crm:deals.board.a11y.over", { name: dealName(active.id), stage: stageName(over.id) })
        : undefined,
    onDragEnd: ({ active, over }) =>
      over
        ? t("crm:deals.board.a11y.dropped", {
            name: dealName(active.id),
            stage: stageName(over.id),
          })
        : t("crm:deals.board.a11y.cancelled", { name: dealName(active.id) }),
    onDragCancel: ({ active }) =>
      t("crm:deals.board.a11y.cancelled", { name: dealName(active.id) }),
  };

  const active = activeId ? dealsById.get(activeId) : undefined;

  return (
    <>
      <DndContext
        sensors={sensors}
        collisionDetection={closestCorners}
        onDragStart={handleDragStart}
        onDragEnd={handleDragEnd}
        onDragCancel={() => setActiveId(null)}
        accessibility={{
          announcements,
          screenReaderInstructions: { draggable: t("crm:deals.board.a11y.instructions") },
        }}
      >
        <ScrollArea type="auto" offsetScrollbars>
          <Group align="flex-start" gap="sm" wrap="nowrap" pb="sm">
            {board.stages.map((stage) => (
              <StageColumn
                key={stage.id}
                stage={stage}
                stages={board.stages}
                canMove={canMove}
                onMove={requestMove}
              />
            ))}
          </Group>
        </ScrollArea>
        <DragOverlay>
          {active ? (
            <DealCardBody
              deal={active.deal}
              stages={board.stages}
              currentStageId={active.stage.id}
              canMove={false}
              onMove={requestMove}
            />
          ) : null}
        </DragOverlay>
      </DndContext>

      {pendingLost && (
        <LostReasonDialog
          dealName={pendingLost.dealName}
          loading={move.isPending}
          onConfirm={confirmLost}
          onClose={() => setPendingLost(null)}
        />
      )}
    </>
  );
}
