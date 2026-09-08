/** Build a native event using the in-memory Storage installed by test/setup.ts. */
export function createStorageEvent(init: StorageEventInit, storageArea: Storage = localStorage): StorageEvent {
  const event = new StorageEvent('storage', init);
  // jsdom's constructor only accepts its own Storage, not our global test double.
  Object.defineProperty(event, 'storageArea', { value: storageArea });
  return event;
}
