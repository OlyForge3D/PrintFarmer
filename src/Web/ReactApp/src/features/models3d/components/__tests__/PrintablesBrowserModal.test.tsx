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
          models: [
            {
              id: '777',
              title: 'Direct Source Model',
              slug: 'direct-source-model',
              author: 'maker',
              sourceUrl: 'https://www.printables.com/model/777-from-source',
            },
          ],
        },
      ]),
    );

    renderModal(onImportUrl);

    fireEvent.click(screen.getByRole('button', { name: 'Show models' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Import' }));

    await waitFor(() => {
      expect(onImportUrl).toHaveBeenCalledWith('https://www.printables.com/model/777-from-source');
    });
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

  it('shows experimental oauth section with connect action when private data is not connected', () => {
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    renderModal();

    expect(screen.getByText('Prusa Account')).toBeInTheDocument();
    expect(screen.getByText('Experimental Beta')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Connect Prusa Account' })).toBeInTheDocument();
    expect(screen.getByText('Private Printables data unavailable')).toBeInTheDocument();
  });

  it('renders liked models and download history sections when oauth is connected', async () => {
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    mockUsePrintablesOAuthStatus.mockReturnValue({
      data: { isLinked: true, hasRefreshToken: true, scope: 'public profile', linkedAtUtc: '2026-06-15T00:00:00Z' },
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    });
    mockUsePrintablesLikedModels.mockReturnValue(createInfiniteQueryResult([
      { id: 'liked-1', title: 'Liked model', slug: 'liked-model', author: 'maker' },
    ]));
    mockUsePrintablesDownloadHistory.mockReturnValue(createInfiniteQueryResult([
      { id: 'history-1', title: 'History model', slug: 'history-model', author: 'maker' },
    ]));

    renderModal();

    expect(screen.getByText('Liked models')).toBeInTheDocument();
    expect(screen.getByText('Download history')).toBeInTheDocument();
    expect(screen.getByText(/Linked on/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Disconnect' })).toBeInTheDocument();
    expect(await screen.findByText('Liked model')).toBeInTheDocument();
    expect(await screen.findByText('History model')).toBeInTheDocument();
  });

it('shows not-supported messaging when authenticated endpoints return 501', () => {
  mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
  mockUsePrintablesOAuthStatus.mockReturnValue({
    data: { isLinked: true, hasRefreshToken: true, scope: 'public', linkedAtUtc: null },
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    });
    mockUsePrintablesLikedModels.mockReturnValue({
      ...createInfiniteQueryResult([]),
      isError: true,
      error: { statusCode: 501, details: 'Not supported' },
    });
    mockUsePrintablesDownloadHistory.mockReturnValue({
      ...createInfiniteQueryResult([]),
      isError: true,
      error: { statusCode: 501, details: 'Not supported' },
    });

    renderModal();

    expect(screen.getByText('Private data endpoints are not supported yet')).toBeInTheDocument();
    expect(screen.getByText('Liked models are not supported on this backend yet.')).toBeInTheDocument();
    expect(screen.getByText('Download history is not supported on this backend yet.')).toBeInTheDocument();
  });

  it('shows oauth availability warning when status query fails', () => {
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    mockUsePrintablesOAuthStatus.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: true,
      refetch: vi.fn(),
    });

    renderModal();

    expect(screen.getByText('Could not load Printables OAuth state')).toBeInTheDocument();
    expect(screen.getByText(/OAuth is currently unavailable on this server/i)).toBeInTheDocument();
  });

  it('shows oauth action error when connect fails', async () => {
    const connectError = new Error('OAuth connect failed');
    const mutateAsync = vi.fn().mockRejectedValue(connectError);
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    mockUsePrintablesOAuthAuthorize.mockReturnValue({
      mutateAsync,
      isPending: false,
    });

    renderModal();
    fireEvent.click(screen.getByRole('button', { name: 'Connect Prusa Account' }));

    expect(await screen.findByText('Printables OAuth action failed')).toBeInTheDocument();
    expect(screen.getByText('OAuth connect failed')).toBeInTheDocument();
  });

  it('requests next pages for liked models and download history', async () => {
    const fetchNextLiked = vi.fn();
    const fetchNextHistory = vi.fn();
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    mockUsePrintablesOAuthStatus.mockReturnValue({
      data: { isLinked: true, hasRefreshToken: true, scope: 'public', linkedAtUtc: null },
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    });
    mockUsePrintablesLikedModels.mockReturnValue({
      ...createInfiniteQueryResult([
        { id: 'liked-1', title: 'Liked model', slug: 'liked-model', author: 'maker' },
      ]),
      hasNextPage: true,
      fetchNextPage: fetchNextLiked,
    });
    mockUsePrintablesDownloadHistory.mockReturnValue({
      ...createInfiniteQueryResult([
        { id: 'history-1', title: 'History model', slug: 'history-model', author: 'maker' },
      ]),
      hasNextPage: true,
      fetchNextPage: fetchNextHistory,
    });

    renderModal();
    fireEvent.click(screen.getByRole('button', { name: 'Load more liked models' }));
    fireEvent.click(screen.getByRole('button', { name: 'Load more history' }));

    await waitFor(() => {
      expect(fetchNextLiked).toHaveBeenCalledTimes(1);
      expect(fetchNextHistory).toHaveBeenCalledTimes(1);
    });
  });

  it('shows reconnect messaging when private endpoints report not-linked (409)', () => {
    mockUsePrintablesUsername.mockReturnValue({ username: 'ripley', isLoading: false, error: null });
    mockUsePrintablesOAuthStatus.mockReturnValue({
      data: { isLinked: true, hasRefreshToken: true, scope: 'public', linkedAtUtc: null },
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    });
    mockUsePrintablesLikedModels.mockReturnValue({
      ...createInfiniteQueryResult([]),
      isError: true,
      error: { statusCode: 409, details: 'Printables account is not linked.' },
    });

    renderModal();

    expect(screen.getByText('Prusa Account link is no longer valid')).toBeInTheDocument();
    expect(screen.getByText('Reconnect your Prusa Account to load liked models.')).toBeInTheDocument();
    expect(screen.getByText('Reconnect your Prusa Account to load download history.')).toBeInTheDocument();
  });
});
