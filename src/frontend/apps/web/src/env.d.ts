/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** "true" registers the internal PG-069 component gallery route in a non-development build (never in production). */
  readonly VITE_ENABLE_COMPONENT_GALLERY?: string;
}
