import { RuntimeProvider, SessionProvider, createRuntime, presentError } from "@enterpriseforms/app-runtime";
import CoordinatorWorker from "@enterpriseforms/work-queue/worker?worker";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { useState } from "react";
import { createAppRouter } from "./router";

/**
 * App shell: one runtime per tab (coordinator worker, typed API client, upload manager), TanStack Query for server
 * state, BFF session provider, router. Features are composed through their public entrypoints only (ADR-ARCH-001).
 */
export function App() {
  const [runtime] = useState(() => createRuntime({ createWorker: () => new CoordinatorWorker({ name: "request-coordinator" }) }));
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          // Reads are gated (503) for a moment after a commit until the authority's archive receipt lands; retry only that.
          queries: { retry: (count, error) => count < 6 && presentError(error).kind === "unavailable", retryDelay: 500, refetchOnWindowFocus: false, staleTime: 15_000 },
          mutations: { retry: false },
        },
      }),
  );
  const [router] = useState(() => createAppRouter());

  return (
    <RuntimeProvider runtime={runtime}>
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <RouterProvider router={router} />
        </SessionProvider>
      </QueryClientProvider>
    </RuntimeProvider>
  );
}
