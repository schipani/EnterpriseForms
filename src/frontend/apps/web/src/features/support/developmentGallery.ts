// PG-069 component gallery: a separate entry so production bundles never include it (the route exists only in development
// builds or with VITE_ENABLE_COMPONENT_GALLERY=true; see docs/ui/UI_CONSISTENCY_STANDARD.md section 4).
export { GalleryPage } from "./pages/GalleryPage";
