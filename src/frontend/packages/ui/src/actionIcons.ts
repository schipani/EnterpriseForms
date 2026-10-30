import { Archive, ArchiveRestore, Download, History, Pencil, Plus, RefreshCw, RotateCcw, Save, Search, Send, Settings, SlidersHorizontal, Trash2, Undo2, type LucideIcon } from "lucide-react";

export type ActionIconComponent = LucideIcon;

/**
 * The one icon per action used on labeled buttons across the application (icon left, text right). Add a new entry here
 * rather than choosing an icon at a call site. Simple Cancel, Yes/No and informational controls get no icon.
 */
export const ActionIcons = {
  add: Plus,
  search: Search,
  /** Advanced Search panel heading (Phase C7 visual styling). */
  filters: SlidersHorizontal,
  reset: RotateCcw,
  edit: Pencil,
  save: Save,
  submit: Send,
  archive: Archive,
  unarchive: ArchiveRestore,
  reopen: Undo2,
  configure: Settings,
  delete: Trash2,
  download: Download,
  /** Independent refresh of one card or panel (PB-07; Phase C8 M1 administration pages). */
  refresh: RefreshCw,
  /** Restore an earlier version of a record as a new version (Phase C6 M3). */
  restoreVersion: History,
} as const satisfies Record<string, ActionIconComponent>;
