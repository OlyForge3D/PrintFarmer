import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { Modal } from '@/common/components/modals/Modal';
import { SearchIcon, ExternalLinkIcon, FolderOpenIcon, CubeIcon, ChevronDownIcon, ChevronRightIcon } from '@/common/components/icons/MdiIcons';
import { Alert, Badge, Button, Input, Spinner, Tabs } from '@/common/components/ui';
import type { ApiError } from '@/types/api';
import type { PrintablesCollectionSummary, PrintablesModelSummary } from '@/types/models';
import {
  SEARCH_DEBOUNCE_MS,
  flattenInfiniteItems,
  usePrintablesCollectionModels,
  usePrintablesDownloadHistory,
  usePrintablesLikedModels,
  usePrintablesOAuthAuthorize,
  usePrintablesOAuthDisconnect,
  usePrintablesOAuthStatus,
  usePrintablesCollections,
  usePrintablesSearch,
  usePrintablesUserModels,
  usePrintablesUsername,
} from '@/features/models3d/hooks/usePrintablesBrowser';

interface PrintablesBrowserModalProps {
  isOpen: boolean;
  onClose: () => void;
  onImportUrl: (url: string) => void;
}

function getModelUrl(model: PrintablesModelSummary): string {
  if (model.sourceUrl?.trim()) {
    return model.sourceUrl;
  }

  const trimmedSlug = model.slug?.trim();
  return trimmedSlug
    ? `https://www.printables.com/model/${model.id}-${trimmedSlug}`
    : `https://www.printables.com/model/${model.id}`;
}

function getApiErrorStatusCode(error: unknown): number | null {
  if (!error || typeof error !== 'object') {
    return null;
  }

  const statusCode = (error as ApiError).statusCode;
  return typeof statusCode === 'number' ? statusCode : null;
}

function getApiErrorMessage(error: unknown, fallback: string): string {
  if (!error || typeof error !== 'object') {
    return fallback;
  }

  const apiError = error as ApiError;
  return apiError.details ?? apiError.message ?? fallback;
}

function PrintablesModelCard({
  model,
  onImport,
}: {
  model: PrintablesModelSummary;
  onImport: (model: PrintablesModelSummary) => void;
}) {
  const modelUrl = getModelUrl(model);

  return (
    <article className="rounded-xl border border-pf-border bg-pf-bg-1 p-3">
      <div className="flex gap-3">
        {model.thumbnailUrl ? (
          <img
            src={model.thumbnailUrl}
            alt=""
            className="h-18 w-18 shrink-0 rounded-md object-cover"
          />
        ) : (
          <div className="flex h-18 w-18 shrink-0 items-center justify-center rounded-md border border-pf-border bg-pf-bg-2">
            <CubeIcon className="h-8 w-8 text-pf-text-tertiary" ariaLabel="Model thumbnail unavailable" />
          </div>
        )}
        <div className="min-w-0 flex-1 space-y-2">
          <div>
            <h4 className="line-clamp-2 text-sm font-semibold text-pf-text-primary">{model.title}</h4>
            <p className="text-xs text-pf-text-secondary">by {model.author}</p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {typeof model.likesCount === 'number' && <Badge size="sm">❤️ {model.likesCount}</Badge>}
            {typeof model.downloadsCount === 'number' && <Badge size="sm">⬇️ {model.downloadsCount}</Badge>}
            {typeof model.fileCount === 'number' && <Badge size="sm">{model.fileCount} files</Badge>}
          </div>
          <div className="flex flex-wrap gap-2">
            <Button type="button" size="sm" variant="primary" onClick={() => onImport(model)}>
              Import
            </Button>
            <a
              href={modelUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1 text-xs text-pf-accent hover:underline"
            >
              View on Printables
              <ExternalLinkIcon className="h-3 w-3" />
            </a>
          </div>
        </div>
      </div>
    </article>
  );
}

function PrintablesCollectionCard({
  collection,
  isExpanded,
  onToggle,
  onImport,
}: {
  collection: PrintablesCollectionSummary;
  isExpanded: boolean;
  onToggle: () => void;
  onImport: (model: PrintablesModelSummary) => void;
}) {
  const collectionModelsQuery = usePrintablesCollectionModels(collection.id, isExpanded);
  const fetchedCollectionModels = useMemo(
    () => flattenInfiniteItems(collectionModelsQuery.data),
    [collectionModelsQuery.data],
  );
  const collectionModels = fetchedCollectionModels.length > 0 ? fetchedCollectionModels : (collection.models ?? []);
  const isLoadingCollectionModels = isExpanded && collectionModelsQuery.isFetching && collectionModels.length === 0;

  return (
    <section className="rounded-xl border border-pf-border bg-pf-bg-1 p-3">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h4 className="text-sm font-semibold text-pf-text-primary">{collection.name}</h4>
          <p className="text-xs text-pf-text-secondary">{collection.modelCount} models</p>
        </div>
        <Button
          type="button"
          variant="secondary"
          size="sm"
          onClick={onToggle}
          iconLeft={isExpanded ? <ChevronDownIcon className="h-4 w-4" /> : <ChevronRightIcon className="h-4 w-4" />}
        >
          {isExpanded ? 'Hide models' : 'Show models'}
        </Button>
      </div>
      {isExpanded && (
        <div className="mt-3 space-y-3">
          {isLoadingCollectionModels && (
            <div className="flex items-center gap-2 text-sm text-pf-text-secondary">
              <Spinner size="sm" />
              Loading collection models…
            </div>
          )}
          {!isLoadingCollectionModels && collectionModels.length > 0 ? (
            collectionModels.map((model) => (
              <PrintablesModelCard key={`${collection.id}-${model.id}`} model={model} onImport={onImport} />
            ))
          ) : null}
          {!isLoadingCollectionModels && collectionModels.length === 0 ? (
            <p className="text-sm text-pf-text-secondary">No models available in this collection.</p>
          ) : null}
          {collectionModelsQuery.hasNextPage && (
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={() => void collectionModelsQuery.fetchNextPage()}
              loading={collectionModelsQuery.isFetchingNextPage}
            >
              Load more collection models
            </Button>
          )}
        </div>
      )}
    </section>
  );
}

export function PrintablesBrowserModal({ isOpen, onClose, onImportUrl }: PrintablesBrowserModalProps) {
  const navigate = useNavigate();
  const { username, isLoading: isLoadingUsername } = usePrintablesUsername();
  const normalizedUsername = username.trim().replace(/^@+/, '');
  const [searchInput, setSearchInput] = useState('');
  const [debouncedSearchInput, setDebouncedSearchInput] = useState('');
  const [activeTab, setActiveTab] = useState<'browse' | 'search' | 'url'>('browse');
  const [manualUrl, setManualUrl] = useState('');
  const [expandedCollectionIds, setExpandedCollectionIds] = useState<Record<string, boolean>>({});
  const [oauthActionError, setOauthActionError] = useState<string | null>(null);

  const collectionsQuery = usePrintablesCollections(normalizedUsername);
  const userModelsQuery = usePrintablesUserModels(normalizedUsername);
  const searchQuery = usePrintablesSearch(debouncedSearchInput);
  const oauthStatusQuery = usePrintablesOAuthStatus();
  const oauthAuthorizeMutation = usePrintablesOAuthAuthorize();
  const oauthDisconnectMutation = usePrintablesOAuthDisconnect();
  const likedModelsQuery = usePrintablesLikedModels(oauthStatusQuery.data?.isLinked ?? false);
  const downloadHistoryQuery = usePrintablesDownloadHistory(oauthStatusQuery.data?.isLinked ?? false);

  const collections = useMemo(() => flattenInfiniteItems(collectionsQuery.data), [collectionsQuery.data]);
  const userModels = useMemo(() => flattenInfiniteItems(userModelsQuery.data), [userModelsQuery.data]);
  const searchResults = useMemo(() => flattenInfiniteItems(searchQuery.data), [searchQuery.data]);
  const likedModels = useMemo(() => flattenInfiniteItems(likedModelsQuery.data), [likedModelsQuery.data]);
  const downloadHistoryItems = useMemo(() => flattenInfiniteItems(downloadHistoryQuery.data), [downloadHistoryQuery.data]);

  useEffect(() => {
    const timeout = window.setTimeout(() => {
      setDebouncedSearchInput(searchInput);
    }, SEARCH_DEBOUNCE_MS);

    return () => window.clearTimeout(timeout);
  }, [searchInput]);

  const handleImport = (model: PrintablesModelSummary) => {
    onImportUrl(getModelUrl(model));
  };

  const handleClose = () => {
    setSearchInput('');
    setDebouncedSearchInput('');
    setManualUrl('');
    setExpandedCollectionIds({});
    setOauthActionError(null);
    setActiveTab('browse');
    onClose();
  };

  const handleImportByUrl = () => {
    const normalized = manualUrl.trim();
    if (!normalized) {
      return;
    }
    onImportUrl(normalized);
  };

  const hasUsername = normalizedUsername.length > 0;
  const isConnectedToPrusaAccount = oauthStatusQuery.data?.isLinked ?? false;
  const oauthLinkedDate = oauthStatusQuery.data?.linkedAtUtc
    ? new Date(oauthStatusQuery.data.linkedAtUtc).toLocaleString()
    : null;

  const likedModelsErrorStatus = getApiErrorStatusCode(likedModelsQuery.error);
  const downloadHistoryErrorStatus = getApiErrorStatusCode(downloadHistoryQuery.error);
  const privateDataNotLinked = likedModelsErrorStatus === 409 || downloadHistoryErrorStatus === 409;
  const privateDataNotSupported = likedModelsErrorStatus === 501 || downloadHistoryErrorStatus === 501;
  const privateDataUnavailable =
    likedModelsErrorStatus === 503
    || downloadHistoryErrorStatus === 503
    || (likedModelsQuery.isError && !privateDataNotLinked && !privateDataNotSupported)
    || (downloadHistoryQuery.isError && !privateDataNotLinked && !privateDataNotSupported);

  const handleConnectPrusaAccount = async () => {
    setOauthActionError(null);
    try {
      const result = await oauthAuthorizeMutation.mutateAsync();
      window.location.assign(result.authorizationUrl);
    } catch (error) {
      const message = getApiErrorMessage(error, 'Failed to start OAuth flow.');
      setOauthActionError(message);
    }
  };

  const handleDisconnectPrusaAccount = async () => {
    setOauthActionError(null);
    try {
      await oauthDisconnectMutation.mutateAsync();
      await Promise.all([
        oauthStatusQuery.refetch(),
        likedModelsQuery.refetch(),
        downloadHistoryQuery.refetch(),
      ]);
    } catch (error) {
      const message = getApiErrorMessage(error, 'Failed to disconnect account.');
      setOauthActionError(message);
    }
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Import from Printables"
      size="xl"
      footer={(
        <div className="flex justify-end">
          <Button type="button" variant="secondary" onClick={handleClose}>Close</Button>
        </div>
      )}
    >
      <Tabs activeTab={activeTab} onTabChange={(tabId) => setActiveTab(tabId as 'browse' | 'search' | 'url')}>
        <Tabs.List aria-label="Printables import options">
          <Tabs.Tab id="browse" icon={<FolderOpenIcon className="h-4 w-4" />}>Browse my models</Tabs.Tab>
          <Tabs.Tab id="search" icon={<SearchIcon className="h-4 w-4" />}>Search</Tabs.Tab>
          <Tabs.Tab id="url" icon={<ExternalLinkIcon className="h-4 w-4" />}>Import by URL</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panels className="max-h-[70vh] space-y-4 overflow-y-auto">
          <Tabs.Panel id="browse">
            {!hasUsername && !isLoadingUsername && (
              <Alert variant="warning" title="Printables username required">
                <div className="space-y-2">
                  <p className="text-sm">
                    Add your Printables username in settings to browse your collections and uploaded models.
                  </p>
                  <Button type="button" size="sm" onClick={() => navigate('/settings?tab=profile&sub=preferences')}>
                    Open settings
                  </Button>
                </div>
              </Alert>
            )}

            {isLoadingUsername && (
              <div className="flex items-center gap-2 text-sm text-pf-text-secondary">
                <Spinner size="sm" />
                Loading user settings…
              </div>
            )}

            {hasUsername && (
              <div className="space-y-5">
                <section className="space-y-3 rounded-xl border border-pf-border bg-pf-bg-1 p-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="space-y-1">
                      <div className="flex items-center gap-2">
                        <h3 className="text-sm font-semibold text-pf-text-primary">Prusa Account</h3>
                        <Badge size="sm">Experimental Beta</Badge>
                      </div>
                      <p className="text-xs text-pf-text-secondary">
                        Opt-in OAuth2 access for liked models and download history.
                      </p>
                    </div>
                    <div className="flex items-center gap-2">
                      {isConnectedToPrusaAccount ? (
                        <Button
                          type="button"
                          variant="secondary"
                          size="sm"
                          onClick={() => void handleDisconnectPrusaAccount()}
                          loading={oauthDisconnectMutation.isPending}
                        >
                          Disconnect
                        </Button>
                      ) : (
                        <Button
                          type="button"
                          size="sm"
                          onClick={() => void handleConnectPrusaAccount()}
                          loading={oauthAuthorizeMutation.isPending}
                        >
                          Connect Prusa Account
                        </Button>
                      )}
                    </div>
                  </div>

                  {!isConnectedToPrusaAccount && !oauthStatusQuery.isLoading && (
                    <Alert variant="warning" title="Private Printables data unavailable">
                      Connect your account to enable liked models and download history.
                    </Alert>
                  )}

                  {oauthStatusQuery.isError && (
                    <Alert variant="warning" title="Could not load Printables OAuth state">
                      OAuth is currently unavailable on this server. Public collections and search still work.
                    </Alert>
                  )}

                  {oauthActionError && (
                    <Alert variant="error" title="Printables OAuth action failed">
                      {oauthActionError}
                    </Alert>
                  )}

                  {isConnectedToPrusaAccount && oauthLinkedDate && (
                    <p className="text-xs text-pf-text-secondary">
                      Linked on {oauthLinkedDate}
                    </p>
                  )}

                  {isConnectedToPrusaAccount && privateDataNotLinked && (
                    <Alert variant="warning" title="Prusa Account link is no longer valid">
                      Reconnect your account to restore liked models and download history.
                    </Alert>
                  )}

                  {isConnectedToPrusaAccount && privateDataNotSupported && (
                    <Alert variant="info" title="Private data endpoints are not supported yet">
                      This server has authenticated liked/download endpoints disabled while beta integration is finalized.
                    </Alert>
                  )}

                  {isConnectedToPrusaAccount && privateDataUnavailable && (
                    <Alert variant="warning" title="Private Printables data is temporarily unavailable">
                      Try again later. Public collections and search are still available.
                    </Alert>
                  )}
                </section>

                <section className="space-y-3">
                  <div className="flex items-center justify-between">
                    <h3 className="text-sm font-semibold text-pf-text-primary">Collections</h3>
                    {collectionsQuery.isFetching && <Spinner size="sm" />}
                  </div>
                  {collections.length === 0 && !collectionsQuery.isLoading ? (
                    <p className="text-sm text-pf-text-secondary">No public collections found for @{normalizedUsername}.</p>
                  ) : (
                    <div className="space-y-3">
                      {collections.map((collection) => {
                        const isExpanded = Boolean(expandedCollectionIds[collection.id]);

                        return (
                          <PrintablesCollectionCard
                            key={collection.id}
                            collection={collection}
                            isExpanded={isExpanded}
                            onToggle={() => {
                              setExpandedCollectionIds((current) => ({
                                ...current,
                                [collection.id]: !isExpanded,
                              }));
                            }}
                            onImport={handleImport}
                          />
                        );
                      })}
                    </div>
                  )}
                  {collectionsQuery.hasNextPage && (
                    <Button
                      type="button"
                      variant="secondary"
                      size="sm"
                      onClick={() => void collectionsQuery.fetchNextPage()}
                      loading={collectionsQuery.isFetchingNextPage}
                    >
                      Load more collections
                    </Button>
                  )}
                </section>

                <section className="space-y-3">
                  <div className="flex items-center justify-between">
                    <h3 className="text-sm font-semibold text-pf-text-primary">Uploaded models</h3>
                    {userModelsQuery.isFetching && <Spinner size="sm" />}
                  </div>
                  {userModels.length === 0 && !userModelsQuery.isLoading ? (
                    <p className="text-sm text-pf-text-secondary">No uploaded models found for @{normalizedUsername}.</p>
                  ) : (
                    <div className="grid gap-3 md:grid-cols-2">
                      {userModels.map((model) => (
                        <PrintablesModelCard key={`user-model-${model.id}`} model={model} onImport={handleImport} />
                      ))}
                    </div>
                  )}
                  {userModelsQuery.hasNextPage && (
                    <Button
                      type="button"
                      variant="secondary"
                      size="sm"
                      onClick={() => void userModelsQuery.fetchNextPage()}
                      loading={userModelsQuery.isFetchingNextPage}
                    >
                      Load more models
                    </Button>
                  )}
                </section>

                {isConnectedToPrusaAccount && (
                  <section className="space-y-3">
                    <div className="flex items-center justify-between">
                      <h3 className="text-sm font-semibold text-pf-text-primary">Liked models</h3>
                      {likedModelsQuery.isFetching && <Spinner size="sm" />}
                    </div>
                    {privateDataNotSupported ? (
                      <p className="text-sm text-pf-text-secondary">
                        Liked models are not supported on this backend yet.
                      </p>
                    ) : privateDataNotLinked ? (
                      <p className="text-sm text-pf-text-secondary">
                        Reconnect your Prusa Account to load liked models.
                      </p>
                    ) : privateDataUnavailable ? (
                      <p className="text-sm text-pf-text-secondary">
                        Liked models are temporarily unavailable.
                      </p>
                    ) : likedModels.length === 0 && !likedModelsQuery.isLoading ? (
                      <p className="text-sm text-pf-text-secondary">No liked models returned from your account yet.</p>
                    ) : (
                      <div className="grid gap-3 md:grid-cols-2">
                        {likedModels.map((model) => (
                          <PrintablesModelCard key={`liked-model-${model.id}`} model={model} onImport={handleImport} />
                        ))}
                      </div>
                    )}
                    {likedModelsQuery.hasNextPage && (
                      <Button
                        type="button"
                        variant="secondary"
                        size="sm"
                        onClick={() => void likedModelsQuery.fetchNextPage()}
                        loading={likedModelsQuery.isFetchingNextPage}
                      >
                        Load more liked models
                      </Button>
                    )}
                  </section>
                )}

                {isConnectedToPrusaAccount && (
                  <section className="space-y-3">
                    <div className="flex items-center justify-between">
                      <h3 className="text-sm font-semibold text-pf-text-primary">Download history</h3>
                      {downloadHistoryQuery.isFetching && <Spinner size="sm" />}
                    </div>
                    {privateDataNotSupported ? (
                      <p className="text-sm text-pf-text-secondary">
                        Download history is not supported on this backend yet.
                      </p>
                    ) : privateDataNotLinked ? (
                      <p className="text-sm text-pf-text-secondary">
                        Reconnect your Prusa Account to load download history.
                      </p>
                    ) : privateDataUnavailable ? (
                      <p className="text-sm text-pf-text-secondary">
                        Download history is temporarily unavailable.
                      </p>
                    ) : downloadHistoryItems.length === 0 && !downloadHistoryQuery.isLoading ? (
                      <p className="text-sm text-pf-text-secondary">No download history returned from your account yet.</p>
                    ) : (
                      <div className="grid gap-3 md:grid-cols-2">
                        {downloadHistoryItems.map((model) => (
                          <PrintablesModelCard key={`download-history-${model.id}`} model={model} onImport={handleImport} />
                        ))}
                      </div>
                    )}
                    {downloadHistoryQuery.hasNextPage && (
                      <Button
                        type="button"
                        variant="secondary"
                        size="sm"
                        onClick={() => void downloadHistoryQuery.fetchNextPage()}
                        loading={downloadHistoryQuery.isFetchingNextPage}
                      >
                        Load more history
                      </Button>
                    )}
                  </section>
                )}
              </div>
            )}
          </Tabs.Panel>

          <Tabs.Panel id="search">
            <div className="space-y-3">
              <Input
                value={searchInput}
                onChange={(event) => setSearchInput(event.target.value)}
                placeholder="Search Printables models…"
                aria-label="Search Printables models"
              />
              {searchQuery.isFetching && (
                <div className="flex items-center gap-2 text-sm text-pf-text-secondary">
                  <Spinner size="sm" />
                  Searching…
                </div>
              )}
              {debouncedSearchInput.trim().length === 0 ? (
                <p className="text-sm text-pf-text-secondary">Type at least one keyword to search Printables.</p>
              ) : searchResults.length === 0 && !searchQuery.isLoading ? (
                <p className="text-sm text-pf-text-secondary">No search results for "{debouncedSearchInput}".</p>
              ) : (
                <div className="grid gap-3 md:grid-cols-2">
                  {searchResults.map((model) => (
                    <PrintablesModelCard key={`search-model-${model.id}`} model={model} onImport={handleImport} />
                  ))}
                </div>
              )}
              {searchQuery.hasNextPage && (
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  onClick={() => void searchQuery.fetchNextPage()}
                  loading={searchQuery.isFetchingNextPage}
                >
                  Load more results
                </Button>
              )}
            </div>
          </Tabs.Panel>

          <Tabs.Panel id="url">
            <div className="space-y-3">
              <p className="text-sm text-pf-text-secondary">
                Paste a Printables model URL and continue to the file selection flow.
              </p>
              <Input
                value={manualUrl}
                onChange={(event) => setManualUrl(event.target.value)}
                placeholder="https://www.printables.com/model/123456-model-name"
                aria-label="Printables model URL"
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    handleImportByUrl();
                  }
                }}
              />
              <div className="flex justify-end">
                <Button type="button" onClick={handleImportByUrl} disabled={!manualUrl.trim()}>
                  Continue to import
                </Button>
              </div>
            </div>
          </Tabs.Panel>
        </Tabs.Panels>
      </Tabs>
    </Modal>
  );
}
