import { mdiSortAscending, mdiSortDescending, mdiArrowUp, mdiArrowDown } from '@mdi/js';

const SORT_ICON_SETS = {
  sort: { asc: mdiSortAscending, desc: mdiSortDescending },
  arrow: { asc: mdiArrowUp, desc: mdiArrowDown },
};

// Provisional D3: these glyphs describe the current direction, not the next action.
export const SORT_DIRECTION_ICONS = SORT_ICON_SETS.sort;
