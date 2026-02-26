/**
 * PartsInventoryTab Component
 *
 * Full CRUD for maintenance components (physical parts inventory).
 * Backed by /api/maintenance/components endpoints.
 * Supports category filtering, low-stock badges, and inline stock editing.
 */

import React, { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Badge, Button } from '@/common/components/ui';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Textarea } from '@/common/components/ui/Textarea';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import {
  PlusIcon,
  EditIcon,
  DeleteIcon,
  SearchIcon,
  AlertIcon,
  GearIcon,
  ExternalLinkIcon,
} from '@/common/components/icons/MdiIcons';
import {
  useMaintenanceComponents,
  useComponentCategories,
  useCreateComponent,
  useUpdateComponent,
  useDeleteComponent,
} from '../hooks/useMaintenanceComponents';
import type {
  MaintenanceComponentDto,
  CreateMaintenanceComponentDto,
  UpdateMaintenanceComponentDto,
} from '@/types/maintenance';

// ──────────────────────── Component Form Modal ────────────────────────

interface ComponentFormModalProps {
  isOpen: boolean;
  component?: MaintenanceComponentDto | null;
  categories: string[];
  onClose: () => void;
}

function ComponentFormModal({ isOpen, component, categories, onClose }: ComponentFormModalProps) {
  const isEdit = !!component;
  const createComponent = useCreateComponent();
  const updateComponent = useUpdateComponent();

  const [name, setName] = useState('');
  const [category, setCategory] = useState('');
  const [customCategory, setCustomCategory] = useState('');
  const [sku, setSku] = useState('');
  const [description, setDescription] = useState('');
  const [unitCost, setUnitCost] = useState('');
  const [supplier, setSupplier] = useState('');
  const [url, setUrl] = useState('');
  const [inStock, setInStock] = useState('0');
  const [minimumStock, setMinimumStock] = useState('0');
  const [isSubmitting, setIsSubmitting] = useState(false);

  React.useEffect(() => {
    if (isOpen) {
      setName(component?.name ?? '');
      const cat = component?.category ?? '';
      if (cat && !categories.includes(cat)) {
        setCategory('__custom__');
        setCustomCategory(cat);
      } else {
        setCategory(cat);
        setCustomCategory('');
      }
      setSku(component?.sku ?? '');
      setDescription(component?.description ?? '');
      setUnitCost(component?.unitCost?.toString() ?? '');
      setSupplier(component?.supplier ?? '');
      setUrl(component?.url ?? '');
      setInStock((component?.inStock ?? 0).toString());
      setMinimumStock((component?.minimumStock ?? 0).toString());
    }
  }, [isOpen, component, categories]);

  const resolvedCategory = category === '__custom__' ? customCategory.trim() : category;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim() || !resolvedCategory) return;
    setIsSubmitting(true);
    try {
      const data = {
        name: name.trim(),
        category: resolvedCategory,
        sku: sku.trim() || null,
        description: description.trim() || null,
        unitCost: unitCost ? Number(unitCost) : null,
        supplier: supplier.trim() || null,
        url: url.trim() || null,
        inStock: Number(inStock) || 0,
        minimumStock: Number(minimumStock) || 0,
      };
      if (isEdit && component) {
        await updateComponent.mutateAsync({ id: component.id, data: data as UpdateMaintenanceComponentDto });
        toast.success('Part updated');
      } else {
        await createComponent.mutateAsync(data as CreateMaintenanceComponentDto);
        toast.success('Part added to inventory');
      }
      onClose();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to save part');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={isEdit ? 'Edit Part' : 'Add Part'} size="lg">
      <form onSubmit={handleSubmit} className="space-y-4">
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label htmlFor="comp-name" className="block text-sm font-medium text-pf-text-secondary mb-1">
              Name <span className="text-red-400">*</span>
            </label>
            <Input id="comp-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="LM8UU Linear Bearing" required maxLength={200} />
          </div>
          <div>
            <label htmlFor="comp-cat" className="block text-sm font-medium text-pf-text-secondary mb-1">
              Category <span className="text-red-400">*</span>
            </label>
            <Select id="comp-cat" value={category} onChange={(e) => setCategory(e.target.value)} required>
              <option value="">Select category...</option>
              {categories.map((c) => (
                <option key={c} value={c}>{c}</option>
              ))}
              <option value="__custom__">+ New category</option>
            </Select>
            {category === '__custom__' && (
              <Input value={customCategory} onChange={(e) => setCustomCategory(e.target.value)} placeholder="New category name" className="mt-1.5" required maxLength={100} />
            )}
          </div>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label htmlFor="comp-sku" className="block text-sm font-medium text-pf-text-secondary mb-1">SKU / Part Number</label>
            <Input id="comp-sku" value={sku} onChange={(e) => setSku(e.target.value)} placeholder="LM8UU-01" maxLength={100} />
          </div>
          <div>
            <label htmlFor="comp-supplier" className="block text-sm font-medium text-pf-text-secondary mb-1">Supplier</label>
            <Input id="comp-supplier" value={supplier} onChange={(e) => setSupplier(e.target.value)} placeholder="Amazon, Prusa, etc." maxLength={200} />
          </div>
        </div>

        <div>
          <label htmlFor="comp-desc" className="block text-sm font-medium text-pf-text-secondary mb-1">Description</label>
          <Textarea id="comp-desc" value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Notes about this part..." rows={2} maxLength={1000} />
        </div>

        <div className="grid grid-cols-3 gap-3">
          <div>
            <label htmlFor="comp-cost" className="block text-sm font-medium text-pf-text-secondary mb-1">Unit Cost</label>
            <Input id="comp-cost" type="number" min="0" step="0.01" value={unitCost} onChange={(e) => setUnitCost(e.target.value)} placeholder="2.50" />
          </div>
          <div>
            <label htmlFor="comp-stock" className="block text-sm font-medium text-pf-text-secondary mb-1">In Stock</label>
            <Input id="comp-stock" type="number" min="0" value={inStock} onChange={(e) => setInStock(e.target.value)} />
          </div>
          <div>
            <label htmlFor="comp-min" className="block text-sm font-medium text-pf-text-secondary mb-1">Min. Stock</label>
            <Input id="comp-min" type="number" min="0" value={minimumStock} onChange={(e) => setMinimumStock(e.target.value)} />
          </div>
        </div>

        <div>
          <label htmlFor="comp-url" className="block text-sm font-medium text-pf-text-secondary mb-1">Purchase URL</label>
          <Input id="comp-url" type="url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://..." maxLength={500} />
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="secondary" size="sm" onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" size="sm" disabled={isSubmitting || !name.trim() || !resolvedCategory}>
            {isSubmitting ? 'Saving…' : isEdit ? 'Save Changes' : 'Add Part'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

// ──────────────────────── Main Component ────────────────────────

export function PartsInventoryTab() {
  const { data: components = [], isLoading, error } = useMaintenanceComponents();
  const { data: categories = [] } = useComponentCategories();
  const deleteComponent = useDeleteComponent();

  const [search, setSearch] = useState('');
  const [categoryFilter, setCategoryFilter] = useState('');
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [editingComponent, setEditingComponent] = useState<MaintenanceComponentDto | null>(null);
  const [deletingComponent, setDeletingComponent] = useState<MaintenanceComponentDto | null>(null);

  const filtered = useMemo(() => {
    let result = components;
    if (search) {
      const q = search.toLowerCase();
      result = result.filter(
        (c) =>
          c.name.toLowerCase().includes(q) ||
          (c.sku?.toLowerCase().includes(q) ?? false) ||
          (c.supplier?.toLowerCase().includes(q) ?? false) ||
          c.category.toLowerCase().includes(q)
      );
    }
    if (categoryFilter) {
      result = result.filter((c) => c.category === categoryFilter);
    }
    return [...result].sort((a, b) => a.category.localeCompare(b.category) || a.name.localeCompare(b.name));
  }, [components, search, categoryFilter]);

  const lowStockCount = useMemo(
    () => components.filter((c) => c.inStock < c.minimumStock).length,
    [components]
  );

  // Inventory value summary
  const inventorySummary = useMemo(() => {
    let totalValue = 0;
    let totalParts = 0;
    const categoriesSet = new Set<string>();
    for (const c of components) {
      totalParts += c.inStock;
      if (c.unitCost != null) totalValue += c.inStock * c.unitCost;
      categoriesSet.add(c.category);
    }
    return { totalValue, totalParts, categoryCount: categoriesSet.size, itemCount: components.length };
  }, [components]);

  const handleDeleteConfirm = async () => {
    if (!deletingComponent) return;
    try {
      await deleteComponent.mutateAsync(deletingComponent.id);
      toast.success(`"${deletingComponent.name}" removed from inventory`);
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Failed to delete part';
      if (msg.includes('referenced')) {
        toast.error('Cannot delete: this part is used by one or more maintenance tasks. Remove it from tasks first.');
      } else {
        toast.error(msg);
      }
    }
    setDeletingComponent(null);
  };

  if (isLoading) {
    return (
      <div className="space-y-3">
        {Array.from({ length: 6 }).map((_, i) => (
          <div key={i} className="h-16 bg-pf-border/50 rounded-lg animate-pulse" />
        ))}
      </div>
    );
  }

  if (error) {
    return (
      <div className="text-center py-12">
        <AlertIcon className="h-10 w-10 text-red-400 mx-auto mb-3" />
        <p className="text-pf-text-secondary">Failed to load parts inventory</p>
        <p className="text-xs text-pf-text-tertiary mt-1">{(error as Error).message}</p>
      </div>
    );
  }

  return (
    <>
      {/* Toolbar */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center gap-3 mb-5">
        <div className="relative flex-1 w-full sm:max-w-sm">
          <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-pf-text-tertiary" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search parts..."
            className="pl-9 w-full"
            aria-label="Search parts inventory"
          />
        </div>
        <Select
          value={categoryFilter}
          onChange={(e) => setCategoryFilter(e.target.value)}
          containerClassName="w-full sm:w-48"
        >
          <option value="">All Categories</option>
          {categories.map((c) => (
            <option key={c} value={c}>{c}</option>
          ))}
        </Select>
        <Button
          variant="primary"
          size="sm"
          onClick={() => { setEditingComponent(null); setIsFormOpen(true); }}
          className="gap-1.5 shrink-0"
        >
          <PlusIcon className="h-4 w-4" />
          Add Part
        </Button>
      </div>

      {/* Summary */}
      <p className="text-sm text-pf-text-tertiary mb-4">
        {filtered.length} part{filtered.length !== 1 ? 's' : ''}
        {categoryFilter ? ` in ${categoryFilter}` : ''}
        {search ? ` matching "${search}"` : ''}
        {lowStockCount > 0 && (
          <span className="text-amber-400"> • {lowStockCount} low stock</span>
        )}
      </p>

      {/* Inventory Summary Bar */}
      {components.length > 0 && (
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 mb-5">
          <div className="bg-pf-bg-2 border border-pf-border rounded-lg px-4 py-3 text-center">
            <p className="text-2xl font-bold text-pf-text-primary">{inventorySummary.itemCount}</p>
            <p className="text-xs text-pf-text-tertiary">Unique Parts</p>
          </div>
          <div className="bg-pf-bg-2 border border-pf-border rounded-lg px-4 py-3 text-center">
            <p className="text-2xl font-bold text-pf-text-primary">{inventorySummary.totalParts}</p>
            <p className="text-xs text-pf-text-tertiary">Total In Stock</p>
          </div>
          <div className="bg-pf-bg-2 border border-pf-border rounded-lg px-4 py-3 text-center">
            <p className="text-2xl font-bold text-pf-text-primary">{inventorySummary.categoryCount}</p>
            <p className="text-xs text-pf-text-tertiary">Categories</p>
          </div>
          <div className="bg-pf-bg-2 border border-pf-border rounded-lg px-4 py-3 text-center">
            <p className="text-2xl font-bold text-pf-text-primary">${inventorySummary.totalValue.toFixed(2)}</p>
            <p className="text-xs text-pf-text-tertiary">Inventory Value</p>
          </div>
        </div>
      )}

      {/* Parts List */}
      {filtered.length === 0 ? (
        <div className="text-center py-12">
          <GearIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-3" />
          <h3 className="font-medium text-pf-text-primary">No Parts in Inventory</h3>
          <p className="text-sm text-pf-text-tertiary mt-1">
            {search || categoryFilter ? 'No parts match your filters' : 'Add parts to track your maintenance inventory'}
          </p>
        </div>
      ) : (
        <div className="space-y-2">
          {filtered.map((comp) => {
            const isLow = comp.inStock < comp.minimumStock;
            return (
              <div
                key={comp.id}
                className={`flex items-center gap-4 p-4 rounded-lg border transition-colors bg-pf-bg-2 ${
                  isLow ? 'border-amber-500/40' : 'border-pf-border hover:border-pf-accent/40'
                }`}
              >
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2 flex-wrap">
                    <h4 className="font-medium text-pf-text-primary truncate">{comp.name}</h4>
                    <Badge variant="default" className="text-xs">{comp.category}</Badge>
                    {isLow && <Badge variant="warning" className="text-xs">Low Stock</Badge>}
                  </div>
                  {comp.description && (
                    <p className="text-sm text-pf-text-tertiary mt-0.5 line-clamp-1">{comp.description}</p>
                  )}
                  <div className="flex items-center gap-4 mt-1.5 text-xs text-pf-text-tertiary flex-wrap">
                    {comp.sku && <span>SKU: {comp.sku}</span>}
                    {comp.supplier && <span>{comp.supplier}</span>}
                    {comp.unitCost != null && <span>${comp.unitCost.toFixed(2)}/ea</span>}
                    <span className={isLow ? 'text-amber-400 font-medium' : ''}>
                      {comp.inStock} in stock (min: {comp.minimumStock})
                    </span>
                    {comp.url?.startsWith('http') && (
                      <a
                        href={comp.url}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="flex items-center gap-0.5 text-pf-accent hover:underline"
                        onClick={(e) => e.stopPropagation()}
                      >
                        <ExternalLinkIcon className="h-3 w-3" />
                        Buy
                      </a>
                    )}
                  </div>
                </div>
                <div className="flex items-center gap-1.5 shrink-0">
                  <Button variant="subtle" size="sm" onClick={() => { setEditingComponent(comp); setIsFormOpen(true); }} aria-label={`Edit ${comp.name}`}>
                    <EditIcon className="h-4 w-4" />
                  </Button>
                  <Button variant="subtle" size="sm" onClick={() => setDeletingComponent(comp)} aria-label={`Delete ${comp.name}`} className="hover:text-red-400">
                    <DeleteIcon className="h-4 w-4" />
                  </Button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* Form Modal */}
      <ComponentFormModal
        isOpen={isFormOpen}
        component={editingComponent}
        categories={categories}
        onClose={() => setIsFormOpen(false)}
      />

      {/* Delete Confirmation */}
      <ConfirmationModal
        isOpen={!!deletingComponent}
        title="Remove Part"
        message={`Remove "${deletingComponent?.name}" from inventory? This cannot be undone.`}
        confirmButtonText="Remove"
        isDangerous
        onConfirm={handleDeleteConfirm}
        onCancel={() => setDeletingComponent(null)}
      />
    </>
  );
}

