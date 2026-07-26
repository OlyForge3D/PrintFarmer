import * as signalR from '@microsoft/signalr';
import { registerAuthenticatedSignalRTransport } from '@/common/auth/authenticatedSignalRSession';
import { getHubUrl, getSignalRAccessToken } from '@/common/utils/apiUrlHelpers';

export interface RegisteredSlicerRegistryConnection {
  connection: signalR.HubConnection;
  dispose: () => Promise<void>;
}

export function createSlicerRegistryConnection(
  registrationName: string,
): RegisteredSlicerRegistryConnection {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(getHubUrl('/hubs/slicer-registry'), {
      accessTokenFactory: getSignalRAccessToken,
    })
    .withAutomaticReconnect()
    .build();
  const unregister = registerAuthenticatedSignalRTransport(
    registrationName,
    () => connection.stop(),
  );

  return {
    connection,
    dispose: async () => {
      unregister();
      await connection.stop();
    },
  };
}
