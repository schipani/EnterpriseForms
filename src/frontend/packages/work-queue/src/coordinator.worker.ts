/// <reference lib="webworker" />
import type { OperationCommand, OperationEvent } from "./contracts";
import { OperationExecutor } from "./executor";

/**
 * Dedicated Web Worker entry: executes the queue off the main thread (CLAUDE-DESIGN.MD §5.2). Messages use a versioned
 * envelope; the worker holds no credentials beyond the same-origin cookie the browser attaches and the CSRF token
 * supplied by the app shell.
 */
declare const self: DedicatedWorkerGlobalScope;

const executor = new OperationExecutor(
  (event: OperationEvent) => self.postMessage({ v: 1, event }),
  (input, init) => fetch(input, init),
);

self.onmessage = (message: MessageEvent<{ v: number; command: OperationCommand }>) => {
  if (message.data?.v !== 1) {
    return;
  }
  executor.handle(message.data.command);
};
