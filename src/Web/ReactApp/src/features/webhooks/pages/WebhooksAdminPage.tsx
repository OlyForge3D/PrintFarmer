import React, { useState } from 'react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Input, FormField, Alert, Toggle, Checkbox, EmptyState } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { PlusIcon, DeleteIcon, EditIcon, RefreshIcon, LoadingIcon, CheckIcon, CloseIcon, ExternalLinkIcon, HistoryIcon } from '@/common/components/icons/MdiIcons';
import { useWebhooks, useWebhookEventTypes, useWebhookDeliveries, useCreateWebhook, useUpdateWebhook, useDeleteWebhook, useTestWebhook } from '../hooks/useWebhooks';
import { toast } from 'sonner';
import type { WebhookSubscription, CreateWebhookDto, UpdateWebhookDto } from '@/types/api';

export function WebhooksAdminPage() {
 const { data: webhooks, isLoading, error } = useWebhooks();
 const { data: eventTypes } = useWebhookEventTypes();
 const createMutation = useCreateWebhook();
 const updateMutation = useUpdateWebhook();
 const deleteMutation = useDeleteWebhook();
 const testMutation = useTestWebhook();

 const [showCreateModal, setShowCreateModal] = useState(false);
 const [editingWebhook, setEditingWebhook] = useState<WebhookSubscription | null>(null);
 const [viewingDeliveries, setViewingDeliveries] = useState<string | null>(null);
 const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null);
 const [testingId, setTestingId] = useState<string | null>(null);

 const handleTest = async (id: string) => {
 setTestingId(id);
 try {
 await testMutation.mutateAsync(id);
 toast.success('Test event sent');
 } catch {
 toast.error('Failed to send test event');
 } finally {
 setTestingId(null);
 }
 };

 const handleDelete = async (id: string) => {
 try {
 await deleteMutation.mutateAsync(id);
 toast.success('Webhook deleted');
 setDeleteConfirm(null);
 } catch {
 toast.error('Failed to delete webhook');
 }
 };

 const handleToggleActive = async (webhook: WebhookSubscription) => {
 try {
 await updateMutation.mutateAsync({
 id: webhook.id,
 dto: { isActive: !webhook.isActive },
 });
 toast.success(webhook.isActive ? 'Webhook disabled' : 'Webhook enabled');
 } catch {
 toast.error('Failed to update webhook');
 }
 };

 if (isLoading) {
 return (
 <PageTemplate title="Webhooks" icon={ExternalLinkIcon}>
 <div className="flex items-center justify-center py-12" role="status" aria-label="Loading webhooks">
 <LoadingIcon className="w-6 h-6 pf-animate-spin text-pf-accent" />
 </div>
 </PageTemplate>
 );
 }

 if (error) {
 return (
 <PageTemplate title="Webhooks" icon={ExternalLinkIcon}>
 <Alert variant="error">Failed to load webhooks</Alert>
 </PageTemplate>
 );
 }

 return (
 <PageTemplate
 title="Webhooks"
 icon={ExternalLinkIcon}
 actions={
 <Button variant="primary" onClick={() => setShowCreateModal(true)} iconLeft={<PlusIcon className="w-4 h-4" />}>
   Add Webhook
 </Button>
 }
 >
 {webhooks && webhooks.length === 0 ? (
 <EmptyState
 icon={<ExternalLinkIcon className="w-12 h-12" />}
 title="No webhooks configured"
 description="Add a webhook to receive event notifications via HTTP POST."
 action={
 <Button variant="primary" onClick={() => setShowCreateModal(true)} iconLeft={<PlusIcon className="w-4 h-4" />}>
   Add Webhook
 </Button>
 }
 />
 ) : (
 <div className="space-y-3">
 {webhooks?.map((wh) => (
 <WebhookCard
 key={wh.id}
 webhook={wh}
 onEdit={() => setEditingWebhook(wh)}
 onDelete={() => setDeleteConfirm(wh.id)}
 onTest={() => handleTest(wh.id)}
 onToggleActive={() => handleToggleActive(wh)}
 onViewDeliveries={() => setViewingDeliveries(wh.id)}
 isTesting={testingId === wh.id}
 />
 ))}
 </div>
 )}

 {/* Create Modal */}
 {showCreateModal && (
 <WebhookFormModal
 eventTypes={eventTypes ?? []}
 onClose={() => setShowCreateModal(false)}
 onSubmit={async (dto) => {
 await createMutation.mutateAsync(dto);
 toast.success('Webhook created');
 setShowCreateModal(false);
 }}
 isSubmitting={createMutation.isPending}
 />
 )}

 {/* Edit Modal */}
 {editingWebhook && (
 <WebhookFormModal
 webhook={editingWebhook}
 eventTypes={eventTypes ?? []}
 onClose={() => setEditingWebhook(null)}
 onSubmit={async (dto) => {
 await updateMutation.mutateAsync({ id: editingWebhook.id, dto });
 toast.success('Webhook updated');
 setEditingWebhook(null);
 }}
 isSubmitting={updateMutation.isPending}
 />
 )}

 {/* Deliveries Modal */}
 {viewingDeliveries && (
 <DeliveriesModal
 webhookId={viewingDeliveries}
 onClose={() => setViewingDeliveries(null)}
 />
 )}

 {/* Delete Confirmation */}
 {deleteConfirm && (
 <Modal isOpen onClose={() => setDeleteConfirm(null)} title="Delete Webhook" size="sm">
 <p className="text-pf-text-secondary mb-4">Are you sure you want to delete this webhook? All delivery logs will be removed.</p>
 <div className="flex justify-end gap-2">
 <Button variant="secondary" onClick={() => setDeleteConfirm(null)}>Cancel</Button>
 <Button variant="danger" onClick={() => handleDelete(deleteConfirm)} loading={deleteMutation.isPending}>
   Delete
 </Button>
 </div>
 </Modal>
 )}
 </PageTemplate>
 );
}

// ── Webhook Card ──────────────────────────────────────────────

interface WebhookCardProps {
 webhook: WebhookSubscription;
 onEdit: () => void;
 onDelete: () => void;
 onTest: () => void;
 onToggleActive: () => void;
 onViewDeliveries: () => void;
 isTesting: boolean;
}

function WebhookCard({ webhook, onEdit, onDelete, onTest, onToggleActive, onViewDeliveries, isTesting }: WebhookCardProps) {
 const eventList = webhook.eventTypes === '*' ? 'All events' : webhook.eventTypes.split(',').join(', ');
 const hasFailures = webhook.consecutiveFailures > 0;

 return (
 <div className="bg-pf-bg-1 rounded-lg border border-pf-border p-4">
 <div className="flex items-start justify-between gap-4">
 <div className="min-w-0 flex-1">
 <div className="flex items-center gap-2">
 <h3 className="font-medium text-pf-text-primary truncate">{webhook.name}</h3>
 <span
 className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${
 webhook.isActive
 ? 'bg-pf-success/10 text-pf-success'
 : 'bg-pf-bg-1 text-pf-text-secondary'
 }`}
 >
 {webhook.isActive ? 'Active' : 'Inactive'}
 </span>
 {hasFailures && (
 <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-pf-error/10 text-pf-error">
 {webhook.consecutiveFailures}/{webhook.maxConsecutiveFailures} failures
 </span>
 )}
 </div>
 <p className="text-sm text-pf-text-secondary mt-1 truncate" title={webhook.url}>
 {webhook.url}
 </p>
 <div className="flex flex-wrap gap-x-4 gap-y-1 mt-2 text-xs text-pf-text-secondary">
 <span title="Event types">{eventList}</span>
 {webhook.hasSecret && <span>Secret configured</span>}
 {webhook.lastDeliveryAt && (
 <span>Last delivery: {new Date(webhook.lastDeliveryAt).toLocaleString()}</span>
 )}
 </div>
 </div>
 <div className="flex items-center gap-2 shrink-0">
 <Toggle
 checked={webhook.isActive}
 onChange={onToggleActive}
 size="sm"
 aria-label={`Toggle webhook ${webhook.name}`}
 />
 <Button variant="ghost" size="sm" onClick={onTest} disabled={isTesting} aria-label="Send test event">
 {isTesting ? <LoadingIcon className="w-4 h-4 pf-animate-spin" /> : <RefreshIcon className="w-4 h-4" />}
 </Button>
 <Button variant="ghost" size="sm" onClick={onViewDeliveries} aria-label="View deliveries">
 <HistoryIcon className="w-4 h-4" />
 </Button>
 <Button variant="ghost" size="sm" onClick={onEdit} aria-label="Edit webhook">
 <EditIcon className="w-4 h-4" />
 </Button>
 <Button variant="ghost" size="sm" onClick={onDelete} aria-label="Delete webhook">
 <DeleteIcon className="w-4 h-4" />
 </Button>
 </div>
 </div>
 </div>
 );
}

// ── Webhook Form Modal ──────────────────────────────────────

interface WebhookFormModalProps {
 webhook?: WebhookSubscription;
 eventTypes: string[];
 onClose: () => void;
 onSubmit: (dto: CreateWebhookDto | UpdateWebhookDto) => Promise<void>;
 isSubmitting: boolean;
}

function WebhookFormModal({ webhook, eventTypes, onClose, onSubmit, isSubmitting }: WebhookFormModalProps) {
 const isEdit = !!webhook;
 const [name, setName] = useState(webhook?.name ?? '');
 const [url, setUrl] = useState(webhook?.url ?? '');
 const [secret, setSecret] = useState('');
 const [selectedEvents, setSelectedEvents] = useState<string[]>(
 webhook?.eventTypes === '*' || !webhook?.eventTypes
 ? ['*']
 : webhook.eventTypes.split(',').map((s) => s.trim())
 );
 const [maxFailures, setMaxFailures] = useState(webhook?.maxConsecutiveFailures ?? 10);
 const [formError, setFormError] = useState('');

 const allSelected = selectedEvents.includes('*');

 const toggleEvent = (event: string) => {
 if (event === '*') {
 setSelectedEvents(['*']);
 return;
 }
 const next = selectedEvents.filter((e) => e !== '*');
 if (next.includes(event)) {
 const filtered = next.filter((e) => e !== event);
 setSelectedEvents(filtered.length === 0 ? ['*'] : filtered);
 } else {
 setSelectedEvents([...next, event]);
 }
 };

 const handleSubmit = async (e: React.FormEvent) => {
 e.preventDefault();
 setFormError('');
 if (!name.trim()) { setFormError('Name is required'); return; }
 if (!url.trim()) { setFormError('URL is required'); return; }
 try {
 new URL(url);
 } catch {
 setFormError('URL must be a valid absolute URL');
 return;
 }

 if (!Number.isFinite(maxFailures) || maxFailures < 1 || maxFailures > 100) {
 setFormError('Max failures must be between 1 and 100');
 return;
 }

 const dto: CreateWebhookDto | UpdateWebhookDto = {
 name: name.trim(),
 url: url.trim(),
 ...(secret ? { secret: secret.trim() } : {}),
 eventTypes: allSelected ? '*' : selectedEvents.join(','),
 maxConsecutiveFailures: maxFailures,
 };

 try {
 await onSubmit(dto);
 } catch {
 setFormError('Failed to save webhook');
 }
 };

 return (
 <Modal isOpen onClose={onClose} title={isEdit ? 'Edit Webhook' : 'Create Webhook'} size="md">
 <form onSubmit={handleSubmit} className="space-y-4">
 {formError && <Alert variant="error">{formError}</Alert>}

 <FormField label="Name" required>
 <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="My webhook" />
 </FormField>

 <FormField label="URL" required>
 <Input type="url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://example.com/webhook" />
 </FormField>

 <FormField label="Secret" hint="Optional. Used to sign payloads with HMAC-SHA256.">
 <Input value={secret} onChange={(e) => setSecret(e.target.value)} placeholder={isEdit ? '(unchanged)' : 'Optional secret'} />
 </FormField>

 <FormField label="Max Consecutive Failures" hint="Webhook is auto-disabled after this many failures.">
 <Input type="number" min={1} max={100} value={maxFailures} onChange={(e) => setMaxFailures(Number(e.target.value))} />
 </FormField>

 <fieldset>
 <legend className="text-sm font-medium text-pf-text-primary mb-2">Event Types</legend>
 <div className="grid grid-cols-2 gap-1 max-h-48 overflow-y-auto">
 <div className="p-1 hover:bg-pf-bg-2 rounded">
 <Checkbox
 checked={allSelected}
 onChange={() => toggleEvent('*')}
 label="All events"
 className="font-medium"
 />
 </div>
 {eventTypes.map((evt) => (
 <div key={evt} className="p-1 hover:bg-pf-bg-2 rounded">
 <Checkbox
 checked={allSelected || selectedEvents.includes(evt)}
 onChange={() => toggleEvent(evt)}
 disabled={allSelected}
 label={evt}
 />
 </div>
 ))}
 </div>
 </fieldset>

 <div className="flex justify-end gap-2 pt-2">
 <Button variant="secondary" type="button" onClick={onClose}>Cancel</Button>
 <Button variant="primary" type="submit" loading={isSubmitting}>
   {isEdit ? 'Save' : 'Create'}
 </Button>
 </div>
 </form>
 </Modal>
 );
}

// ── Deliveries Modal ──────────────────────────────────────────

function DeliveriesModal({ webhookId, onClose }: { webhookId: string; onClose: () => void }) {
 const { data: deliveries, isLoading } = useWebhookDeliveries(webhookId);

 return (
 <Modal isOpen onClose={onClose} title="Recent Deliveries" size="lg">
 {isLoading ? (
 <div className="flex justify-center py-8" role="status" aria-label="Loading deliveries">
 <LoadingIcon className="w-6 h-6 pf-animate-spin text-pf-accent" />
 </div>
 ) : deliveries && deliveries.length === 0 ? (
 <p className="text-center py-8 text-pf-text-secondary">No deliveries yet</p>
 ) : (
 <div className="overflow-x-auto">
 <table className="w-full text-sm" role="table">
 <thead>
 <tr className="border-b border-pf-border text-left">
 <th className="py-2 px-2 font-medium">Event</th>
 <th className="py-2 px-2 font-medium">Status</th>
 <th className="py-2 px-2 font-medium">Code</th>
 <th className="py-2 px-2 font-medium">Duration</th>
 <th className="py-2 px-2 font-medium">Time</th>
 </tr>
 </thead>
 <tbody>
 {deliveries?.map((d) => (
 <tr key={d.id} className="border-b border-pf-border/50">
 <td className="py-2 px-2">{d.eventType}</td>
 <td className="py-2 px-2">
 {d.success ? (
 <>
 <CheckIcon className="w-4 h-4 text-pf-success" />
 <span className="sr-only">Success</span>
 </>
 ) : (
 <span className="text-pf-error text-xs" title={d.errorMessage ?? undefined}>
 <CloseIcon className="w-4 h-4 inline" /> {d.errorMessage?.slice(0, 40)}
 </span>
 )}
 </td>
 <td className="py-2 px-2">{d.statusCode ?? '—'}</td>
 <td className="py-2 px-2">{d.durationMs ? `${d.durationMs}ms` : '—'}</td>
 <td className="py-2 px-2 text-xs text-pf-text-secondary">{new Date(d.createdAt).toLocaleString()}</td>
 </tr>
 ))}
 </tbody>
 </table>
 </div>
 )}
 <div className="flex justify-end pt-4">
 <Button variant="secondary" onClick={onClose}>Close</Button>
 </div>
 </Modal>
 );
}
