export type {
  AdminDestination,
  AdminDestinationAccess,
  AdminDestinationGroup,
  AdminDestinationIcon,
  AdminDestinationPermission,
} from './adminDestinations';
export {
  ADMIN_DESTINATIONS,
  ADMIN_DESTINATION_GROUPS,
  filterDestinationsByAccess,
  getDestinationById,
  getDestinationsByGroup,
  getHubGroupedDestinations,
} from './adminDestinations';

export type { LegacyRedirect } from './legacyRedirects';
export { LEGACY_REDIRECTS, getLegacyRedirect } from './legacyRedirects';
