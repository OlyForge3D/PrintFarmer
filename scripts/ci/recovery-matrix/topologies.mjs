const monolithServices = Object.freeze(['monolith']);
const manifestServices = Object.freeze(['api', 'frontend', 'slicer-host', 'printer-discovery', 'orcaslicer-worker', 'monolith']);
const splitManagedServices = Object.freeze(['api', 'frontend', 'slicer-host', 'printer-discovery', 'orcaslicer-worker']);
const splitNoWorkerServices = Object.freeze(['api', 'frontend', 'slicer-host', 'printer-discovery']);

export const topologyCatalog = Object.freeze({
  monolith: Object.freeze({
    id: 'monolith',
    deploymentMode: 'monolith',
    serviceIds() {
      return monolithServices;
    },
    activeServiceIds() {
      return monolithServices;
    },
    healthServiceId: 'monolith',
    healthComposeService: 'printfarmer',
    infrastructureIds: Object.freeze([]),
    composeServiceName() {
      return 'printfarmer';
    },
    imageEnvironmentVariable() {
      return 'PRINTFARMER_IMAGE';
    },
  }),
  split: Object.freeze({
    id: 'split',
    deploymentMode: 'split',
    serviceIds(workerMode = 'managed') {
      return workerMode === 'none' ? splitNoWorkerServices : splitManagedServices;
    },
    activeServiceIds(workerMode = 'managed') {
      return this.serviceIds(workerMode);
    },
    healthServiceId: 'api',
    healthComposeService: 'api',
    infrastructureIds: Object.freeze(['nginx']),
    composeServiceName(serviceId) {
      return serviceId === 'monolith' ? 'printfarmer' : serviceId;
    },
    imageEnvironmentVariable(serviceId) {
      return serviceId === 'monolith' ? 'PRINTFARMER_IMAGE' : `PRINTFARMER_${envPrefix(serviceId)}_IMAGE`;
    },
  }),
});

export function topologyFor(id) {
  const topology = topologyCatalog[id];
  if (!topology) {
    throw new Error(`unknown topology '${id}'`);
  }
  return topology;
}

export function serviceMappingsFor(cell) {
  const topology = topologyFor(cell.topology);
  return manifestServices.map((serviceId) => ({
    serviceId,
    composeServiceName: topology.composeServiceName(serviceId),
    imageEnvironmentVariable: topology.imageEnvironmentVariable(serviceId),
    imageRepository: `ghcr.io/olyforge3d/printfarmer-${serviceId}`,
  }));
}

export function requiredInfrastructureIds(cell) {
  const topology = topologyFor(cell.topology);
  return [...new Set([...topology.infrastructureIds, cell.provider === 'sqlserver' ? 'mssql' : 'postgres'])];
}

function envPrefix(serviceId) {
  return serviceId.toUpperCase().replaceAll('-', '_');
}
