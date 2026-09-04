/**
 * Locally bundled 3D viewer environment map assets.
 *
 * These are served from `public/assets/hdri/` by the web client's own origin
 * so viewers that render a reflective/lit environment (the slicer bed and
 * model viewers) do not depend on any third-party runtime host (see #2405,
 * which removed a runtime fetch to a third-party CDN mirror of the same
 * upstream HDRI asset).
 */

/** Studio-lighting HDR environment map used for reflections in 3D viewers. */
export const STUDIO_HDRI_URL = '/assets/hdri/studio_small_03_1k.hdr';
