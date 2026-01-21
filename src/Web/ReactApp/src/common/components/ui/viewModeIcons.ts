/**
 * MDI icon paths for common view modes
 * 
 * These paths are extracted from Material Design Icons (@mdi/js) for use
 * with the ViewToggle component without requiring the full MDI dependency.
 */
export const viewModeIcons = {
  /** Grid of cards (mdiViewGrid) */
  grid: 'M3,11H11V3H3M3,21H11V13H3M13,21H21V13H13M13,3V11H21V3',
  /** Table/list view (mdiViewList) */
  table: 'M9,5V9H21V5M9,19H21V15H9M9,14H21V10H9M4,9H8V5H4M4,19H8V15H4M4,14H8V10H4V14Z',
  /** List view (mdiViewList) - alias for table */
  list: 'M9,5V9H21V5M9,19H21V15H9M9,14H21V10H9M4,9H8V5H4M4,19H8V15H4M4,14H8V10H4V14Z',
  /** Compact cards (mdiViewGrid) - alias for grid */
  compact: 'M3,11H11V3H3M3,21H11V13H3M13,21H21V13H13M13,3V11H21V3',
  /** Collapsed view (mdiViewList) */
  collapsed: 'M9,5V9H21V5M9,19H21V15H9M9,14H21V10H9M4,9H8V5H4M4,19H8V15H4M4,14H8V10H4V14Z',
  /** Expandable view (mdiViewComfy) */
  expandable: 'M2,4V20H22V4H2M4,6H8V10H4V6M4,18V12H8V18H4M20,18H10V14H20V18M20,12H10V6H20V12Z',
  /** Quilt/tile view (mdiViewQuilt) */
  quilt: 'M10,5V11H21V5M16,18H21V12H16M10,18H15V12H10M3,5V18H9V5H3Z',
  /** Glass effect (mdiBlur) */
  glass: 'M14,8.5A1.5,1.5 0 0,0 12.5,10A1.5,1.5 0 0,0 14,11.5A1.5,1.5 0 0,0 15.5,10A1.5,1.5 0 0,0 14,8.5M14,12.5A1.5,1.5 0 0,0 12.5,14A1.5,1.5 0 0,0 14,15.5A1.5,1.5 0 0,0 15.5,14A1.5,1.5 0 0,0 14,12.5M10,17A1,1 0 0,0 9,18A1,1 0 0,0 10,19A1,1 0 0,0 11,18A1,1 0 0,0 10,17M10,8.5A1.5,1.5 0 0,0 8.5,10A1.5,1.5 0 0,0 10,11.5A1.5,1.5 0 0,0 11.5,10A1.5,1.5 0 0,0 10,8.5M14,4.5A1.5,1.5 0 0,0 12.5,6A1.5,1.5 0 0,0 14,7.5A1.5,1.5 0 0,0 15.5,6A1.5,1.5 0 0,0 14,4.5M10,4.5A1.5,1.5 0 0,0 8.5,6A1.5,1.5 0 0,0 10,7.5A1.5,1.5 0 0,0 11.5,6A1.5,1.5 0 0,0 10,4.5M18,13A1,1 0 0,0 17,14A1,1 0 0,0 18,15A1,1 0 0,0 19,14A1,1 0 0,0 18,13M18,5A1,1 0 0,0 17,6A1,1 0 0,0 18,7A1,1 0 0,0 19,6A1,1 0 0,0 18,5M18,9A1,1 0 0,0 17,10A1,1 0 0,0 18,11A1,1 0 0,0 19,10A1,1 0 0,0 18,9M18,17A1,1 0 0,0 17,18A1,1 0 0,0 18,19A1,1 0 0,0 19,18A1,1 0 0,0 18,17M6,17A1,1 0 0,0 5,18A1,1 0 0,0 6,19A1,1 0 0,0 7,18A1,1 0 0,0 6,17M6,5A1,1 0 0,0 5,6A1,1 0 0,0 6,7A1,1 0 0,0 7,6A1,1 0 0,0 6,5M10,12.5A1.5,1.5 0 0,0 8.5,14A1.5,1.5 0 0,0 10,15.5A1.5,1.5 0 0,0 11.5,14A1.5,1.5 0 0,0 10,12.5M6,9A1,1 0 0,0 5,10A1,1 0 0,0 6,11A1,1 0 0,0 7,10A1,1 0 0,0 6,9M6,13A1,1 0 0,0 5,14A1,1 0 0,0 6,15A1,1 0 0,0 7,14A1,1 0 0,0 6,13M14,17A1,1 0 0,0 13,18A1,1 0 0,0 14,19A1,1 0 0,0 15,18A1,1 0 0,0 14,17Z',
  /** Segmented sections (mdiViewSequential) */
  segmented: 'M3,5V21H9V5H3M10,5V21H14V5H10M15,5V21H21V5H15Z',
  /** Status glow (mdiLightbulbOn) */
  statusGlow: 'M12,6A6,6 0 0,1 18,12C18,14.22 16.79,16.16 15,17.2V19A1,1 0 0,1 14,20H10A1,1 0 0,1 9,19V17.2C7.21,16.16 6,14.22 6,12A6,6 0 0,1 12,6M14,21V22A1,1 0 0,1 13,23H11A1,1 0 0,1 10,22V21H14M20,11H23V13H20V11M1,11H4V13H1V11M13,1V4H11V1H13M4.92,3.5L7.05,5.64L5.63,7.05L3.5,4.93L4.92,3.5M16.95,5.63L19.07,3.5L20.5,4.93L18.37,7.05L16.95,5.63Z',
  /** Dashboard gauges (mdiGauge) */
  dashboard: 'M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,4A8,8 0 0,1 20,12C20,14.4 19,16.5 17.3,18C15.9,16.7 14,16 12,16C10,16 8.2,16.7 6.7,18C5,16.5 4,14.4 4,12A8,8 0 0,1 12,4M14,5.89C13.62,5.9 13.26,6.15 13.1,6.54L11.81,9.77L11.71,10C11,10.13 10.41,10.6 10.14,11.26C9.73,12.29 10.23,13.45 11.26,13.86C12.29,14.27 13.45,13.77 13.86,12.74C14.12,12.08 14,11.32 13.57,10.76L13.67,10.5L14.96,7.29L14.97,7.26C15.17,6.75 14.92,6.17 14.41,5.96C14.28,5.91 14.15,5.89 14,5.89M10,6A1,1 0 0,0 9,7A1,1 0 0,0 10,8A1,1 0 0,0 11,7A1,1 0 0,0 10,6M7,9A1,1 0 0,0 6,10A1,1 0 0,0 7,11A1,1 0 0,0 8,10A1,1 0 0,0 7,9M17,9A1,1 0 0,0 16,10A1,1 0 0,0 17,11A1,1 0 0,0 18,10A1,1 0 0,0 17,9Z',
  /** Flip card (mdiFlipToBack) */
  flip: 'M15,17H17V15H15M15,5H17V3H15M5,7H3V19A2,2 0 0,0 5,21H17V19H5M19,17A2,2 0 0,0 21,15V3A2,2 0 0,0 19,1H9A2,2 0 0,0 7,3V15A2,2 0 0,0 9,17H19M9,15V3H19V15H9M11,5H13V3H11M11,17H13V15H11V17Z',
  /** Drawer/expandable (mdiArrowExpandDown) */
  drawer: 'M22,4V2H2V4H11V18.17L5.5,12.67L4.08,14.08L12,22L19.92,14.08L18.5,12.67L13,18.17V4H22Z',
} as const;

/**
 * Type for built-in view mode icon names
 */
export type ViewModeIconName = keyof typeof viewModeIcons;

/**
 * A single view mode option
 */
export interface ViewModeOption<T extends string = string> {
  /** The mode value */
  mode: T;
  /** MDI icon path string, or a key from viewModeIcons */
  icon: string | ViewModeIconName;
  /** Tooltip/title for the button */
  title: string;
}

/**
 * Pre-configured options for grid/table toggle (catalog pages)
 */
export const gridTableOptions: ViewModeOption<'grid' | 'table'>[] = [
  { mode: 'grid', icon: 'grid', title: 'Grid view' },
  { mode: 'table', icon: 'table', title: 'Table view' },
];

/**
 * Pre-configured options for grid/list toggle
 */
export const gridListOptions: ViewModeOption<'grid' | 'list'>[] = [
  { mode: 'grid', icon: 'grid', title: 'Grid view' },
  { mode: 'list', icon: 'list', title: 'List view' },
];
