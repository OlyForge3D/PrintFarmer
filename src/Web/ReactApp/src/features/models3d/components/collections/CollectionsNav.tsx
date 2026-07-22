import { useMemo, useRef, useState } from 'react';
import type { MouseEvent } from 'react';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { Button } from '@/common/components/ui';
import {
  FolderIcon,
  FolderOpenIcon,
  FolderPlusIcon,
  EarthIcon,
  AccountMultipleIcon,
  MoreVerticalIcon,
  EditIcon,
  DeleteIcon,
  ShareIcon,
  LockIcon,
} from '@/common/components/icons/MdiIcons';
import { ContextMenu, type ContextMenuItem } from '@/common/components/ContextMenu';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { CollectionFormModal } from './CollectionFormModal';
import {
  useModelCollections,
  useCreateModelCollection,
  useUpdateModelCollection,
  useDeleteModelCollection,
  useShareModelCollection,
  useUnshareModelCollection,
} from '@/features/models3d/hooks/useCollections';
import type { ModelCollection } from '@/types/models';

export interface CollectionsNavProps {
  /** The currently selected collection id, or null for "All Models". */
  selectedCollectionId: string | null;
  onSelectCollection: (id: string | null) => void;
}

interface OpenMenuState {
  collection: ModelCollection;
  x: number;
  y: number;
}

/**
 * Personal/shared collection navigation sidebar (#846). Reuses ContextMenu, Modal-based
 * forms, ConfirmationModal, and the shared UI kit rather than introducing new patterns.
 */
export function CollectionsNav({ selectedCollectionId, onSelectCollection }: CollectionsNavProps) {
  const { user, hasRole } = useAuth();
  const { data: collections = [], isLoading, isError } = useModelCollections();
  const createCollection = useCreateModelCollection();
  const updateCollection = useUpdateModelCollection();
  const deleteCollection = useDeleteModelCollection();
  const shareCollection = useShareModelCollection();
  const unshareCollection = useUnshareModelCollection();

  const [formState, setFormState] = useState<{ mode: 'create' | 'rename'; collection: ModelCollection | null } | null>(null);
  const [menuState, setMenuState] = useState<OpenMenuState | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<ModelCollection | null>(null);
  const menuTriggerRefs = useRef<Map<string, HTMLButtonElement>>(new Map());

  const isAdmin = hasRole('farm_admin');

  const { personal, shared } = useMemo(() => {
    const personalCollections: ModelCollection[] = [];
    const sharedCollections: ModelCollection[] = [];
    for (const collection of collections) {
      const isOwner = user?.id === collection.ownerUserId;
      if (isOwner) {
        personalCollections.push(collection);
      } else if (collection.isShared) {
        sharedCollections.push(collection);
      }
    }
    // Owner's own shared collections still show under "Personal" (they own it), but also
    // surface them to everyone else under "Shared" via the loop above.
    return { personal: personalCollections, shared: sharedCollections };
  }, [collections, user?.id]);

  const canManage = (collection: ModelCollection) => isAdmin || user?.id === collection.ownerUserId;

  const closeMenu = () => {
    const id = menuState?.collection.id;
    setMenuState(null);
    if (id) {
      menuTriggerRefs.current.get(id)?.focus();
    }
  };

  const openMenuFor = (collection: ModelCollection, event: MouseEvent<HTMLButtonElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    setMenuState({ collection, x: rect.left, y: rect.bottom + 4 });
  };

  const buildMenuItems = (collection: ModelCollection): ContextMenuItem[] => {
    const items: ContextMenuItem[] = [
      {
        label: 'Rename',
        icon: EditIcon,
        onClick: () => setFormState({ mode: 'rename', collection }),
      },
    ];
    if (canManage(collection)) {
      items.push(
        collection.isShared
          ? { label: 'Unshare', icon: LockIcon, onClick: () => unshareCollection.mutate(collection.id) }
          : { label: 'Share with everyone', icon: ShareIcon, onClick: () => shareCollection.mutate(collection.id) }
      );
      items.push({ divider: true });
      items.push({
        label: 'Delete',
        icon: DeleteIcon,
        variant: 'danger',
        onClick: () => setDeleteTarget(collection),
      });
    }
    return items;
  };

  const handleFormSubmit = (values: { name: string; description?: string }) => {
    if (formState?.mode === 'rename' && formState.collection) {
      updateCollection.mutate(
        { id: formState.collection.id, dto: values },
        { onSuccess: () => setFormState(null) }
      );
    } else {
      createCollection.mutate(values, { onSuccess: () => setFormState(null) });
    }
  };

  const handleDeleteConfirm = () => {
    if (!deleteTarget) return;
    deleteCollection.mutate(deleteTarget.id, {
      onSuccess: () => {
        if (selectedCollectionId === deleteTarget.id) {
          onSelectCollection(null);
        }
        setDeleteTarget(null);
      },
    });
  };

  const renderCollectionRow = (collection: ModelCollection) => {
    const isSelected = selectedCollectionId === collection.id;
    return (
      <li key={collection.id} className="flex items-center group">
        <Button
          type="button"
          variant="unstyled"
          onClick={() => onSelectCollection(collection.id)}
          aria-current={isSelected ? 'true' : undefined}
          className={`flex-1 flex items-center gap-2 min-w-0 px-3 py-2 rounded-lg text-sm text-left transition-colors focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent ${
            isSelected ? 'bg-pf-accent-bg/15 text-pf-accent font-medium' : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
          }`}
        >
          {isSelected ? <FolderOpenIcon className="w-4 h-4 shrink-0" /> : <FolderIcon className="w-4 h-4 shrink-0" />}
          <span className="truncate">{collection.name}</span>
          {collection.isShared && <EarthIcon className="w-3.5 h-3.5 shrink-0 text-pf-text-tertiary" ariaLabel="Shared" />}
          <span className="ml-auto shrink-0 text-xs text-pf-text-tertiary">{collection.memberCount}</span>
        </Button>
        <Button
          ref={(el) => {
            if (el) menuTriggerRefs.current.set(collection.id, el);
            else menuTriggerRefs.current.delete(collection.id);
          }}
          type="button"
          variant="subtle"
          size="sm"
          className="opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 focus-visible:opacity-100 shrink-0 !px-1.5"
          aria-label={`Actions for ${collection.name}`}
          aria-haspopup="menu"
          aria-expanded={menuState?.collection.id === collection.id}
          onClick={(event) => openMenuFor(collection, event)}
        >
          <MoreVerticalIcon className="w-4 h-4" />
        </Button>
      </li>
    );
  };

  return (
    <nav aria-label="Model collections" className="flex flex-col h-full min-w-0">
      <div className="flex items-center justify-between px-1 pb-2">
        <h2 className="text-sm font-semibold text-pf-text-primary">Collections</h2>
        <Button
          type="button"
          variant="subtle"
          size="sm"
          aria-label="New collection"
          title="New collection"
          onClick={() => setFormState({ mode: 'create', collection: null })}
        >
          <FolderPlusIcon className="w-4 h-4" />
        </Button>
      </div>

      <ul className="space-y-0.5">
        <li>
          <Button
            type="button"
            variant="unstyled"
            onClick={() => onSelectCollection(null)}
            aria-current={selectedCollectionId === null ? 'true' : undefined}
            className={`w-full flex items-center gap-2 px-3 py-2 rounded-lg text-sm text-left transition-colors focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent ${
              selectedCollectionId === null ? 'bg-pf-accent-bg/15 text-pf-accent font-medium' : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary'
            }`}
          >
            <FolderOpenIcon className="w-4 h-4 shrink-0" />
            All Models
          </Button>
        </li>
      </ul>

      {isLoading && (
        <p className="px-3 py-2 text-xs text-pf-text-tertiary" role="status">
          Loading collections…
        </p>
      )}
      {isError && (
        <p className="px-3 py-2 text-xs text-pf-error-text" role="alert">
          Failed to load collections.
        </p>
      )}

      {!isLoading && !isError && (
        <>
          <div className="mt-3">
            <h3 className="px-3 text-xs font-semibold uppercase tracking-wide text-pf-text-tertiary mb-1">Personal</h3>
            {personal.length === 0 ? (
              <p className="px-3 text-xs text-pf-text-tertiary">No personal collections yet.</p>
            ) : (
              <ul className="space-y-0.5">{personal.map(renderCollectionRow)}</ul>
            )}
          </div>

          <div className="mt-3">
            <h3 className="px-3 text-xs font-semibold uppercase tracking-wide text-pf-text-tertiary mb-1 flex items-center gap-1">
              <AccountMultipleIcon className="w-3.5 h-3.5" />
              Shared
            </h3>
            {shared.length === 0 ? (
              <p className="px-3 text-xs text-pf-text-tertiary">No shared collections yet.</p>
            ) : (
              <ul className="space-y-0.5">{shared.map(renderCollectionRow)}</ul>
            )}
          </div>
        </>
      )}

      {menuState && (
        <ContextMenu
          x={menuState.x}
          y={menuState.y}
          items={buildMenuItems(menuState.collection)}
          onClose={closeMenu}
        />
      )}

      <CollectionFormModal
        key={formState ? `open-${formState.mode}-${formState.collection?.id ?? 'new'}` : 'closed'}
        isOpen={!!formState}
        collection={formState?.mode === 'rename' ? formState.collection : null}
        isSaving={createCollection.isPending || updateCollection.isPending}
        onSubmit={handleFormSubmit}
        onClose={() => setFormState(null)}
      />

      <ConfirmationModal
        isOpen={!!deleteTarget}
        title="Delete Collection"
        message={
          deleteTarget
            ? `Are you sure you want to delete "${deleteTarget.name}"? This removes the collection and its memberships, but does not delete any models.`
            : ''
        }
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous
        isConfirming={deleteCollection.isPending}
        onConfirm={handleDeleteConfirm}
        onCancel={() => setDeleteTarget(null)}
      />
    </nav>
  );
}
