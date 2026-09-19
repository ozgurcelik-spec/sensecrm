import * as React from "react";
import { useLocation } from "react-router";
import { useTranslation } from "react-i18next";
import { Button, Center, Loader, Stack, Text } from "@mantine/core";

/**
 * Route pages are code-split (see App.tsx): shows a fallback while a page chunk loads, and an
 * error screen (with reload) instead of a frozen page when a chunk or render fails. The error
 * clears on the next navigation.
 */
function RouteFallback() {
  const { t } = useTranslation(["common"]);
  return (
    <Center mih={240} p="xl">
      <Stack align="center" gap="xs">
        <Loader size="sm" />
        <Text size="sm" c="dimmed">
          {t("common:loading")}
        </Text>
      </Stack>
    </Center>
  );
}

function RouteLoadError() {
  const { t } = useTranslation(["common"]);
  return (
    <Center mih={240} p="xl">
      <Stack align="center" gap="sm">
        <Text size="sm" c="dimmed">
          {t("common:errors.unknown")}
        </Text>
        <Button variant="default" onClick={() => window.location.reload()}>
          {t("common:retry")}
        </Button>
      </Stack>
    </Center>
  );
}

interface BoundaryProps {
  children: React.ReactNode;
  resetKey: string;
}

interface BoundaryState {
  failed: boolean;
  shownFor: string;
}

class RouteErrorBoundary extends React.Component<BoundaryProps, BoundaryState> {
  state: BoundaryState = { failed: false, shownFor: this.props.resetKey };

  static getDerivedStateFromProps(props: BoundaryProps, state: BoundaryState) {
    return props.resetKey === state.shownFor ? null : { failed: false, shownFor: props.resetKey };
  }

  static getDerivedStateFromError() {
    return { failed: true };
  }

  render() {
    return this.state.failed ? <RouteLoadError /> : this.props.children;
  }
}

export default function RouteBoundary({ children }: { children: React.ReactNode }) {
  const { pathname } = useLocation();
  return (
    <RouteErrorBoundary resetKey={pathname}>
      <React.Suspense fallback={<RouteFallback />}>{children}</React.Suspense>
    </RouteErrorBoundary>
  );
}
