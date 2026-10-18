import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { useState } from "react";
import { createAppRouter } from "./router";

/**
 * App shell: providers and router composition only (ADR-ARCH-001 §4U.4). Feature routes are lazy-loaded from
 * each feature's public entrypoint once their backend slices exist; Phase 0 registers the shell root only.
 */
export function App() {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 30_000 },
        },
      }),
  );
  const [router] = useState(() => createAppRouter());

  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}
