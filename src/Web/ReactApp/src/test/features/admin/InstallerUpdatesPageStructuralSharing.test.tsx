import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { inventory } from "@/test/features/system/serviceInventoryFixture";
import type { UpdateChannelSettings } from "@/types/api";

// This suite deliberately does NOT mock InstallerUpdatesExperience: the bug
// it guards against only reproduces with the real component wired to a real
// QueryClient, where TanStack Query structural sharing can hand back the
// exact same cached object reference for a deep-equal refetch result.
const {
  getSystemInfo,
  getUpdateChannelSettings,
  updateUpdateChannelSettings,
  setGetUpdateChannelSettingsImpl,
  setUpdateUpdateChannelSettingsImpl,
} = vi.hoisted(() => {
  let getUpdateChannelSettingsImpl: () => Promise<unknown> = () => Promise.resolve(undefined);
  let updateUpdateChannelSettingsImpl: (settings: unknown) => Promise<unknown> = () => Promise.resolve(undefined);

  return {
    getSystemInfo: vi.fn(() => Promise.resolve({ inventory: inventory() })),
    getUpdateChannelSettings: vi.fn(() => getUpdateChannelSettingsImpl()),
    updateUpdateChannelSettings: vi.fn((settings: unknown) => updateUpdateChannelSettingsImpl(settings)),
    setGetUpdateChannelSettingsImpl: (impl: () => Promise<unknown>) => { getUpdateChannelSettingsImpl = impl; },
    setUpdateUpdateChannelSettingsImpl: (impl: (settings: unknown) => Promise<unknown>) => { updateUpdateChannelSettingsImpl = impl; },
  };
});

vi.mock("@/features/auth/hooks/useAuth", () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));
vi.mock("@/services/api", () => ({
  apiClient: { getSystemInfo, getUpdateChannelSettings, updateUpdateChannelSettings },
}));

const stableSettings: UpdateChannelSettings = { channel: "stable", insiderAcknowledged: false };

async function renderPage() {
  const { InstallerUpdatesPage } = await import("@/features/admin/pages/InstallerUpdatesPage");
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } },
  });
  const view = render(
    <QueryClientProvider client={queryClient}>
      <InstallerUpdatesPage />
    </QueryClientProvider>,
  );
  return { queryClient, ...view };
}

describe("InstallerUpdatesPage structural-sharing reconciliation (real InstallerUpdatesExperience)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setGetUpdateChannelSettingsImpl(() => Promise.resolve({ ...stableSettings }));
    setUpdateUpdateChannelSettingsImpl(() => Promise.resolve(undefined));
  });

  it("unlocks controls from an unknown save outcome via GET-only retry even when structural sharing preserves the cached object reference", async () => {
    const user = userEvent.setup();
    const { queryClient } = await renderPage();

    await screen.findByRole("combobox", { name: "Release channel" });
    const queryKey = ["settings", "UpdateChannel"];
    const cachedBeforeSave = queryClient.getQueryData<UpdateChannelSettings>(queryKey);
    expect(cachedBeforeSave).toEqual(stableSettings);

    // An uncertain save: the POST rejects and the confirmation GET that
    // follows it also rejects, so the outcome is genuinely unknown.
    setUpdateUpdateChannelSettingsImpl(() => Promise.reject(new Error("network unavailable")));
    setGetUpdateChannelSettingsImpl(() => Promise.reject(new Error("confirmation unavailable")));

    await user.click(screen.getByRole("button", { name: "Save update channel" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent(/outcome is unknown/));
    expect(screen.getByRole("combobox", { name: "Release channel" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Save update channel" })).toBeDisabled();

    // The failed confirmation GET must not have clobbered the previously
    // cached authoritative object.
    expect(queryClient.getQueryData(queryKey)).toBe(cachedBeforeSave);

    // The GET-only retry succeeds with a deep-equal (but distinct literal)
    // settings object. TanStack Query structural sharing therefore keeps
    // returning the exact same cached reference as before -- this is the
    // condition the fix must not depend on a changed prop identity for.
    setGetUpdateChannelSettingsImpl(() => Promise.resolve({ ...stableSettings }));
    await user.click(screen.getByRole("button", { name: "Retry UpdateChannel settings" }));

    await waitFor(() => expect(screen.getByRole("combobox", { name: "Release channel" })).not.toBeDisabled());
    expect(screen.getByRole("button", { name: "Save update channel" })).not.toBeDisabled();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();

    // Confirm structural sharing really did preserve the same cached
    // reference across the retry -- proving the unlock above did not
    // (and could not have) depended on a new object arriving.
    expect(queryClient.getQueryData(queryKey)).toBe(cachedBeforeSave);
  });

  it("preserves the truthful not-saved message and synced selector when the confirmation GET disagrees with the request, even as the query cache produces a new object reference", async () => {
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole("combobox", { name: "Release channel" });

    // The POST is uncertain (rejects), but the authoritative confirmation GET
    // succeeds and disagrees with the request -- a confirmed rejection, not
    // an unknown outcome. Its content also differs from what was previously
    // cached, so the query cache produces a brand-new object reference at
    // (approximately) the same time the component reconciles locally -- the
    // exact race the props-sync effect must not lose to.
    setUpdateUpdateChannelSettingsImpl(() => Promise.reject(new Error("network unavailable")));
    setGetUpdateChannelSettingsImpl(() => Promise.resolve({ channel: "insider", insiderAcknowledged: false }));

    await user.selectOptions(screen.getByRole("combobox", { name: "Release channel" }), "insider");
    await user.click(screen.getByRole("button", { name: "Save update channel" }));
    await user.click(screen.getByRole("checkbox", { name: /accept the prerelease risk/i }));
    await user.click(screen.getByRole("button", { name: "Acknowledge and save" }));

    await waitFor(() => expect(screen.getByRole("combobox", { name: "Release channel" })).toHaveValue("insider"));
    expect(await screen.findByRole("alert")).toHaveTextContent(/server did not record the Insider acknowledgement/);
    // The outcome is conclusively known (a confirmed rejection): controls
    // re-enable rather than staying locked as if the outcome were unknown.
    expect(screen.getByRole("combobox", { name: "Release channel" })).not.toBeDisabled();
    expect(screen.getByRole("button", { name: "Save update channel" })).not.toBeDisabled();
  });
});
