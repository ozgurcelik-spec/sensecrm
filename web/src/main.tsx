import React from "react";
import ReactDOM from "react-dom/client";
import { QueryClientProvider } from "@tanstack/react-query";
import App from "./App";
import { queryClient } from "./lib/query-client";
import { registerSessionRefreshHandlers } from "./lib/http-interceptors";
import { MantineRoot } from "@/components/mantine-root";
import "@fontsource-variable/inter";
import "./styles/index.css";
import "./i18n";

registerSessionRefreshHandlers();

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <MantineRoot>
        <App />
      </MantineRoot>
    </QueryClientProvider>
  </React.StrictMode>
);
