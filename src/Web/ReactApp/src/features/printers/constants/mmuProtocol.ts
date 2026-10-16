/** Well-known MMU protocol type identifiers matching the backend MmuProtocol class. */
export const MmuProtocol = {
  HappyHare: 'HappyHare',
  Qidibox: 'Qidibox',
  Afc: 'AFC',
  SnapmakerU1: 'SnapmakerU1',
  /** Flashforge AD5X Intelligent Filament Switch via ZMOD. Reports gate selection, not a confirmed load. */
  ZmodIfs: 'ZmodIfs',
  Unknown: 'Unknown',
} as const;

export type MmuProtocolType = (typeof MmuProtocol)[keyof typeof MmuProtocol];
