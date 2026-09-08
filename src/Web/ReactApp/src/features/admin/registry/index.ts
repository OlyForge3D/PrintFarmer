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
  ADMIN_HUB_PARENT,
  canAccessDestination,
  canAccessSettingsTab,
  filterDestinationsByAccess,
  getDestinationById,
  getDestinationsByGroup,
  getHubGroupedDestinations,
  getStandaloneConfigurationDestinations,
  hasAccessibleDestinationWithPrefix,
  isPathWithin,
  resolveDestinationPath,
} from './adminDestinations';
