import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import { OfflineTroubleshootingGuide } from '../OfflineTroubleshootingGuide';

describe('OfflineTroubleshootingGuide', () => {
  it('renders common troubleshooting steps for any backend', () => {
    render(<OfflineTroubleshootingGuide printerBackend="Moonraker" />);

    expect(screen.getByText('Printer Offline — Troubleshooting Steps')).toBeInTheDocument();
    expect(screen.getByText('Check that the printer is powered on and the display is active')).toBeInTheDocument();
    expect(screen.getByText('Verify the network cable is connected or WiFi is associated')).toBeInTheDocument();
  });

  it('renders Moonraker-specific steps', () => {
    render(<OfflineTroubleshootingGuide printerBackend="Moonraker" />);

    expect(screen.getByText('Check if the Moonraker service is running')).toBeInTheDocument();
    expect(screen.getByText('systemctl status moonraker')).toBeInTheDocument();
    expect(screen.getByText('systemctl status klipper')).toBeInTheDocument();
    expect(screen.getByText('Moonraker Docs')).toBeInTheDocument();
  });

  it('renders PrusaLink-specific steps', () => {
    render(<OfflineTroubleshootingGuide printerBackend="PrusaLink" />);

    expect(screen.getByText('Verify PrusaLink is enabled in the printer Settings menu')).toBeInTheDocument();
    expect(screen.getByText('Check that the firmware version supports PrusaLink')).toBeInTheDocument();
    expect(screen.getByText('PrusaLink Docs')).toBeInTheDocument();
  });

  it('renders OctoPrint-specific steps', () => {
    render(<OfflineTroubleshootingGuide printerBackend="OctoPrint" />);

    expect(screen.getByText('Check if the OctoPrint service is running')).toBeInTheDocument();
    expect(screen.getByText('systemctl status octoprint')).toBeInTheDocument();
    expect(screen.getByText('OctoPrint Docs')).toBeInTheDocument();
  });

  it('renders generic steps for unknown backend', () => {
    render(<OfflineTroubleshootingGuide printerBackend="SomeUnknown" />);

    expect(screen.getByText('Verify the printer firmware supports the configured backend protocol')).toBeInTheDocument();
    expect(screen.getByText('Power-cycle the printer and wait 30 seconds before reconnecting')).toBeInTheDocument();
  });

  it('shows printer IP when available', () => {
    render(<OfflineTroubleshootingGuide printerBackend="Moonraker" printerIp="192.168.1.100" />);

    expect(screen.getByText('Ping the printer to check network connectivity')).toBeInTheDocument();
    expect(screen.getByText('ping 192.168.1.100')).toBeInTheDocument();
  });

  it('shows server URL link when available', () => {
    render(<OfflineTroubleshootingGuide printerBackend="Moonraker" frontendUrl="http://192.168.1.100" />);

    expect(screen.getByText("Check if the printer's web interface is accessible")).toBeInTheDocument();
    const link = screen.getByText('http://192.168.1.100');
    expect(link).toBeInTheDocument();
    expect(link.closest('a')).toHaveAttribute('href', 'http://192.168.1.100');
  });

  it('dismiss callback works', async () => {
    const user = userEvent.setup();
    const onDismiss = vi.fn();
    render(<OfflineTroubleshootingGuide printerBackend="Moonraker" onDismiss={onDismiss} />);

    await user.click(screen.getByLabelText('Dismiss troubleshooting guide'));
    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it('starts collapsed and expands on click in compact variant', async () => {
    const user = userEvent.setup();
    render(<OfflineTroubleshootingGuide printerBackend="Moonraker" variant="compact" />);

    expect(screen.getByText('Troubleshoot')).toBeInTheDocument();
    expect(screen.queryByText('Common Checks')).not.toBeInTheDocument();

    await user.click(screen.getByLabelText('Toggle offline troubleshooting guide'));
    expect(screen.getByText('Common Checks')).toBeInTheDocument();
  });

  it('renders FlashForge/SDCP steps with power-cycle suggestion', () => {
    render(<OfflineTroubleshootingGuide printerBackend="FlashForge" />);

    expect(screen.getByText('Power-cycle the printer and wait 30 seconds before reconnecting')).toBeInTheDocument();
  });

  it('renders popover variant with dialog role', () => {
    render(<OfflineTroubleshootingGuide printerBackend="OctoPrint" variant="popover" />);

    expect(screen.getByRole('dialog', { name: 'Offline troubleshooting guide' })).toBeInTheDocument();
    expect(screen.getByText('Offline Troubleshooting')).toBeInTheDocument();
  });
});
