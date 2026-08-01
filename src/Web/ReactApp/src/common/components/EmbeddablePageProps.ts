/** Props for a page that can be mounted standalone at a route or inside a shell. */
export interface EmbeddablePageProps {
  /**
   * Suppress this page's own header, because a shell above it already renders
   * page chrome. Forward it to `PageTemplate` rather than branching on it.
   */
  embedded?: boolean;
}
