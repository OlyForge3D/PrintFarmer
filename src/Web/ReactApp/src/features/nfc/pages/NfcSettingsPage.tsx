import { PageTemplate } from '@/common/components/PageTemplate';
import type { EmbeddablePageProps } from '@/common/components/EmbeddablePageProps';
import { NfcBindingsPage } from '@/features/nfc/pages/NfcBindingsPage';
import { NfcDevicesPage } from '@/features/nfc/pages/NfcDevicesPage';

export function NfcSettingsPage({ embedded = false }: EmbeddablePageProps) {
  return (
    <PageTemplate
      title="NFC Settings"
      subtitle="Manage NFC readers and tag bindings"
      embedded={embedded}
    >
      <div className="space-y-8">
        <section aria-labelledby="nfc-readers-heading">
          <h2 id="nfc-readers-heading" className="mb-4 text-xl font-semibold text-pf-text-primary">
            NFC Readers
          </h2>
          <NfcDevicesPage embedded />
        </section>

        <section aria-labelledby="nfc-bindings-heading">
          <h2 id="nfc-bindings-heading" className="mb-4 text-xl font-semibold text-pf-text-primary">
            NFC Tag Bindings
          </h2>
          <NfcBindingsPage embedded />
        </section>
      </div>
    </PageTemplate>
  );
}
