import { createApiClient, type ApiClient } from "@enterpriseforms/api-client";
import { UploadManager } from "@enterpriseforms/upload-manager";
import { RequestCoordinator, type IRequestCoordinator } from "@enterpriseforms/work-queue";
import { createContext, useContext, type ReactNode } from "react";

export interface AppRuntime {
  coordinator: IRequestCoordinator;
  api: ApiClient;
  uploads: UploadManager;
}

/** One runtime per tab, created before the authenticated bootstrap and never re-created on navigation (ADR-PB-007). */
export function createRuntime(options: { createWorker?: () => Worker; coordinator?: IRequestCoordinator } = {}): AppRuntime {
  const coordinator = options.coordinator ?? new RequestCoordinator(options.createWorker ? { createWorker: options.createWorker } : {});
  const api = createApiClient(coordinator);
  return { coordinator, api, uploads: new UploadManager(api) };
}

const RuntimeContext = createContext<AppRuntime | null>(null);

export function RuntimeProvider({ runtime, children }: { runtime: AppRuntime; children: ReactNode }) {
  return <RuntimeContext.Provider value={runtime}>{children}</RuntimeContext.Provider>;
}

export function useRuntime(): AppRuntime {
  const runtime = useContext(RuntimeContext);
  if (!runtime) {
    throw new Error("RuntimeProvider is missing above this component");
  }
  return runtime;
}

export function useApi(): ApiClient {
  return useRuntime().api;
}
