/** Well-known MMU protocol type identifiers matching the backend MmuProtocol class. */
export const MmuProtocol = {
  HappyHare: 'HappyHare',
  Qidibox: 'Qidibox',
  Afc: 'AFC',
  Unknown: 'Unknown',
} as const;

export type MmuProtocolType = (typeof MmuProtocol)[keyof typeof MmuProtocol];
