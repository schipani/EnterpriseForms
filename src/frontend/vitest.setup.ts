import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

// Testing Library only auto-cleans with vitest globals; register it explicitly so renders never leak between tests.
afterEach(() => cleanup());
