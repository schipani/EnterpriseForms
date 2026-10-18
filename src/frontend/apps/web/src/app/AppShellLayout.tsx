import type { PropsWithChildren } from "react";

/** Minimal accessible shell: skip link, landmark header with organization logo slot (top-left per ADR-PB-014), main landmark. */
export function AppShellLayout({ children }: PropsWithChildren) {
  return (
    <div className="min-h-screen bg-page text-body">
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded focus:bg-surface focus:px-3 focus:py-2"
      >
        Skip to main content
      </a>
      <header className="flex items-center gap-3 border-b border-line bg-surface px-4 py-3">
        <div aria-hidden="true" className="h-8 w-8 rounded bg-primary" />
        <span className="text-lg font-semibold">Enterprise Forms</span>
      </header>
      <main id="main-content" tabIndex={-1} className="mx-auto w-full max-w-5xl px-4 py-6">
        {children}
      </main>
    </div>
  );
}
