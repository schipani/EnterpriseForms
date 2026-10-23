import { Archive, ArchiveRestore, Download, History, Pencil, Plus, RotateCcw, Save, Search, Send, Settings, Trash2, Undo2, type LucideIcon } from "lucide-react";

export type ActionIconComponent = LucideIcon;

/**
 * The one icon per action used on labeled buttons across the application (icon left, text right). Add a new entry here
 * rather than choosing an icon at a call site. Simple Cancel, Yes/No and informational controls get no icon.
 */
export const ActionIcons = {
  add: Plus,
  search: Search,
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
  /** Restore an earlier version of a record as a new version (Phase C6 M3). */
  restoreVersion: History,
} as const satisfies Record<string, ActionIconComponent>;
