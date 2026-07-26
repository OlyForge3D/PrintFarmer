/**
 * Client-side classification of "essential" settings — the small set a farm
 * operator plausibly changes in the first month (feature toggles, connection
 * URLs, retention windows, cost inputs). Everything else is advanced and hidden
 * behind the Everything toggle unless the user searches for it explicitly.
 *
 * ## Why a client manifest instead of a backend attribute
 *
 * The issue offers both options. This ships as a client manifest because:
 *
 * 1. Classification is a UX judgement about how the *page* should default, not
 *    a property of the setting itself. Tuning it later ("actually,
 *    ScanIntervalSeconds should be essential too") is a one-line UI change
 *    rather than a coordinated backend attribute + metadata pipeline + client
 *    change that also has to survive a container rebuild.
 * 2. Keeps the change surface small — no `SettingDisplayAttribute` extension,
 *    no `SettingPropertyDisplayMetadata` mapping change, no touch to 15+
 *    settings class files across `src/infra/Settings/`. That keeps the PR
 *    contained to frontend + shell wiring and avoids a `dotnet build`
 *    dependency in the validation path.
 * 3. Advanced settings stay reachable through the Everything toggle AND
 *    through search, so nothing is hidden from the API or from the search
 *    index. The classification only affects default page density.
 *
 * If a future need emerges to sort settings by importance server-side (e.g.
 * export tooling, telemetry), promote this to a `SettingDisplayAttribute.IsEssential`
 * flag and delete this file.
 *
 * Keys match `AppSetting(SectionName)` on the backend. Property names match
 * `JsonPropertyName` (which is what the metadata API exposes as `property.name`).
 */
const ESSENTIAL_SETTINGS_MAP: Readonly<Record<string, ReadonlySet<string>>> = {
  // System / logging — retention window and the on/off toggle are asked about
  // early because they impact disk usage and audit visibility.
  SystemLog: new Set(['enabled', 'retentionDays']),

  // Networking / discovery — turning discovery on/off and pointing it at the
  // right subnet is the single biggest onboarding step for a new farm.
  NetworkDiscovery: new Set([
    'enableDiscovery',
    'discoverySubnets',
    'backgroundScanEnabled',
  ]),

  // Files — which upload types are accepted is a routine policy decision.
  GcodeUpload: new Set(['allowedExtensions']),

  // Catalog — the feature flag; auto-apply is intentionally left as advanced
  // because it changes printer config without user confirmation.
  CatalogUpdates: new Set(['enabled']),

  // Cost tracking — enabling it, plus the two rates most operators tune
  // (electricity, machine hourly). Material defaults have their own bespoke UI
  // (materialPriceDefaults extension) and stay accessible via the extension.
  CostTracking: new Set([
    'enableAutomaticCostCalculation',
    'electricityRatePerKwh',
    'defaultMachineHourlyRate',
  ]),

  // Auto-tagging — the individual tag-type toggles are the whole feature.
  // NozzleTagEnabled is intentionally advanced; material + color cover the
  // common case, and nozzle-tagging is more niche.
  AutoTagging: new Set(['materialTagEnabled', 'colorTagEnabled']),

  // Obico — turn detection on/off, and control whether it can pause the print.
  // Timing/threshold tuning stays advanced.
  Obico: new Set(['enabled', 'autoPauseOnFailure']),

  // Maintenance — the master feature toggle.
  MaintenanceAlerts: new Set(['enabled']),

  // Integrations — connection URL / feature flag / channel identifier.
  // Encrypted tokens (Telegram bot token, HA token) are not in the essential
  // set because they are only touched during initial setup AND because
  // password-typed fields render masked either way.
  Spoolman: new Set(['baseUrl']),
  HomeAssistant: new Set(['enabled', 'baseUrl']),
  Telegram: new Set(['enabled', 'chatId']),

  // Slicer — which UI mode users see and which modes are available.
  SlicerSettings: new Set(['slicerMode', 'enabledModes']),
};

export function isEssentialProperty(sectionKey: string, propertyName: string): boolean {
  return ESSENTIAL_SETTINGS_MAP[sectionKey]?.has(propertyName) ?? false;
}

export function sectionHasEssential(sectionKey: string): boolean {
  return (ESSENTIAL_SETTINGS_MAP[sectionKey]?.size ?? 0) > 0;
}

/**
 * Total count of essential property markers across every section. Exposed for
 * tests / diagnostics; the roughly-20 target in the issue is enforced by the
 * corresponding unit test rather than a runtime check.
 */
export function essentialPropertyCount(): number {
  let total = 0;
  for (const set of Object.values(ESSENTIAL_SETTINGS_MAP)) {
    total += set.size;
  }
  return total;
}
