import '@testing-library/jest-dom';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { InfiniteData } from '@tanstack/react-query';
import type { PrintablesPagedResponse } from '@/types/models';
import { PrintablesBrowserModal } from '@/features/models3d/components/PrintablesBrowserModal';

const {
  mockNavigate,
  mockUsePrintablesUsername,
  mockUsePrintablesCollections,
  mockUsePrintablesCollectionModels,
  mockUsePrintablesUserModels,
  mockUsePrintablesSearch,
  mockUsePrintablesOAuthStatus,
  mockUsePrintablesOAuthAuthorize,
  mockUsePrintablesOAuthDisconnect,
  mockUsePrintablesLikedModels,
  mockUsePrintablesDownloadHistory,
} = vi.hoisted(() => ({
  mockNavigate: vi.fn(),
  mockUsePrintablesUsername: vi.fn(),
  mockUsePrintablesCollections: vi.fn(),
  mockUsePrintablesCollectionModels: vi.fn(),
  mockUsePrintablesUserModels: vi.fn(),
  mockUsePrintablesSearch: vi.fn(),
  mockUsePrintablesOAuthStatus: vi.fn(),
  mockUsePrintablesOAuthAuthorize: vi.fn(),
  mockUsePrintablesOAuthDisconnect: vi.fn(),
  mockUsePrintablesLikedModels: vi.fn(),
  mockUsePrintablesDownloadHistory: vi.fn(),
}));

vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

vi.mock('@/features/models3d/hooks/usePrintablesBrowser', () => ({
  SEARCH_DEBOUNCE_MS: 300,
  flattenInfiniteItems: (data: InfiniteData<PrintablesPagedResponse<unknown>, unknown> | undefined) =>
    data?.pages.flatMap((page) => page.items) ?? [],
  usePrintablesUsername: () => mockUsePrintablesUsername(),
  usePrintablesCollections: (username: string) => mockUsePrintablesCollections(username),
  usePrintablesCollectionModels: (collectionId: string, enabled: boolean) => mockUsePrintablesCollectionModels(collectionId, enabled),
  usePrintablesUserModels: (username: string) => mockUsePrintablesUserModels(username),
  usePrintablesSearch: (query: string) => mockUsePrintablesSearch(query),
  usePrintablesOAuthStatus: () => mockUsePrintablesOAuthStatus(),
  usePrintablesOAuthAuthorize: () => mockUsePrintablesOAuthAuthorize(),
  usePrintablesOAuthDisconnect: () => mockUsePrintablesOAuthDisconnect(),
  usePrintablesLikedModels: (enabled: boolean) => mockUsePrintablesLikedModels(enabled),
  usePrintablesDownloadHistory: (enabled: boolean) => mockUsePrintablesDownloadHistory(enabled),
}));

function createInfiniteData<T>(items: T[]) {
  return {
    pages: [{ items, nextCursor: null }],
    pageParams: [undefined],
  } as InfiniteData<PrintablesPagedResponse<T>, unknown>;
}

function createInfiniteQueryResult<T>(items: T[]) {
  return {
    data: createInfiniteData(items),
    isLoading: false,
    isError: false,
    error: null,
    isFetching: false,
    isFetchingNextPage: false,
    hasNextPage: false,
    fetchNextPage: vi.fn(),
    refetch: vi.fn(),
  };
}

function renderModal(onImportUrl = vi.fn()) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <PrintablesBrowserModal isOpen onClose={vi.fn()} onImportUrl={onImportUrl} />
    </QueryClientProvider>,
  );
}

describe('PrintablesBrowserModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUsePrintablesUsername.mockReturnValue({ username: '', isLoading: false, error: null });
    mockUsePrintablesCollections.mockReturnValue(createInfiniteQueryResult([]));
    mockUsePrintablesUserModels.mockReturnValue(createInfiniteQueryResult([]));
    mockUsePrintablesCollectionModels.mockReturnValue(createInfiniteQueryResult([]));
    mockUsePrintablesSearch.mockReturnValue(createInfiniteQueryResult([]));
    mockUsePrintablesOAuthStatus.mockReturnValue({
      data: { isLinked: false, hasRefreshToken: false, scope: null, linkedAtUtc: null, accessTokenExpiresAtUtc: null },
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    });
    mockUsePrintablesOAuthAuthorize.mockReturnValue({
      mutateAsync: vi.fn(),
      isPending: false,
    });
    mockUsePrintablesOAuthDisconnect.mockReturnValue({
      mutateAsync: vi.fn(),
      isPending: false,
    });
    mockUsePrintablesLikedModels.mockReturnValue(createInfiniteQueryResult([]));
    mockUsePrintablesDownloadHistory.mockReturnValue(createInfiniteQueryResult([]));
  });

  it('shows settings callout when Printables username is not configured', async () => {
    renderModal();

    expect(screen.getByText('Printables username required')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Open settings' }));

    await waitFor(() => {
      expect(mockNavigate).toHaveBeenCalledWith('/settings?tab=profile&sub=preferences');
    });
  });

  it('uses inferred model URL when importing from search results', async () => {
    const onImportUrl = vi.fn();
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    mockUsePrintablesSearch.mockReturnValue(
      createInfiniteQueryResult([
        {
          id: '12345',
          title: 'Voron tool holder',
          slug: 'voron-tool-holder',
          author: 'print-friend',
          likesCount: 10,
          downloadsCount: 25,
        },
      ]),
    );

    renderModal(onImportUrl);
    fireEvent.click(screen.getByRole('tab', { name: /Search/ }));

    const searchInput = screen.getByLabelText('Search Printables models');
    fireEvent.change(searchInput, { target: { value: 'voron' } });

    const importButtons = await screen.findAllByRole('button', { name: 'Import' });
    fireEvent.click(importButtons[0]);

    await waitFor(() => {
      expect(onImportUrl).toHaveBeenCalledWith('https://www.printables.com/model/12345-voron-tool-holder');
    });
  });

  it('expands collection models and prefers sourceUrl when importing from browse view', async () => {
    const onImportUrl = vi.fn();
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    mockUsePrintablesCollections.mockReturnValue(
      createInfiniteQueryResult([
        {
          id: 'collection-1',
          name: 'Favorites',
          modelCount: 1,
          likesCount: 4,
        },
      ]),
    );
    mockUsePrintablesCollectionModels.mockImplementation((_collectionId: string, enabled: boolean) => (
      enabled
        ? createInfiniteQueryResult([
            {
              id: '777',
              title: 'Direct Source Model',
              slug: 'direct-source-model',
              author: 'maker',
              sourceUrl: 'https://www.printables.com/model/777-from-source',
            },
          ])
        : createInfiniteQueryResult([])
    ));

    renderModal(onImportUrl);

    fireEvent.click(screen.getByRole('button', { name: 'Show models' }));
    await waitFor(() => {
      expect(mockUsePrintablesCollectionModels).toHaveBeenCalledWith('collection-1', true);
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Import' }));

    await waitFor(() => {
      expect(onImportUrl).toHaveBeenCalledWith('https://www.printables.com/model/777-from-source');
    });
  });

  it('renders @handle usernames without duplicating the @ prefix in empty-state copy', () => {
    mockUsePrintablesUsername.mockReturnValue({ username: '@ripley', isLoading: false, error: null });

    renderModal();

    expect(screen.getByText('No public collections found for @ripley.')).toBeInTheDocument();
    expect(screen.getByText('No uploaded models found for @ripley.')).toBeInTheDocument();
  });

  it('loads next search page when results indicate more data', async () => {
    const fetchNextPage = vi.fn();
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    mockUsePrintablesSearch.mockReturnValue({
      ...createInfiniteQueryResult([
        {
          id: '12345',
          title: 'Voron tool holder',
          slug: 'voron-tool-holder',
          author: 'print-friend',
        },
      ]),
      hasNextPage: true,
      fetchNextPage,
    });

    renderModal();
    fireEvent.click(screen.getByRole('tab', { name: /Search/ }));
    fireEvent.change(screen.getByLabelText('Search Printables models'), { target: { value: 'voron' } });

    fireEvent.click(await screen.findByRole('button', { name: 'Load more results' }));

    await waitFor(() => {
      expect(fetchNextPage).toHaveBeenCalledTimes(1);
    });
  });

  it('trims manual URL import input before continuing', async () => {
    const onImportUrl = vi.fn();
    renderModal(onImportUrl);

    fireEvent.click(screen.getByRole('tab', { name: /Import by URL/ }));
    const urlInput = screen.getByLabelText('Printables model URL');
    const continueButton = screen.getByRole('button', { name: 'Continue to import' });

    expect(continueButton).toBeDisabled();

    fireEvent.change(urlInput, { target: { value: '  https://www.printables.com/model/999-trimmed  ' } });
    expect(continueButton).toBeEnabled();
    fireEvent.click(continueButton);

    await waitFor(() => {
      expect(onImportUrl).toHaveBeenCalledWith('https://www.printables.com/model/999-trimmed');
    });
  });

  it('hides oauth-only sections and actions', () => {
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    renderModal();

    expect(screen.queryByText('Prusa Account')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Connect Prusa Account' })).not.toBeInTheDocument();
    expect(screen.queryByText('Liked models')).not.toBeInTheDocument();
    expect(screen.queryByText('Download history')).not.toBeInTheDocument();
  });
});
