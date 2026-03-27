using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[AppSetting(SectionName)]
[SettingGroup("Operations", DisplayName = "Operations", Description = "Operational settings and cost tracking", Icon = "pf-icon-operations", Order = 3)]
[SettingDisplay(Name = "Cost Tracking", Description = "Configure cost calculation for print jobs.", Icon = "pf-icon-cost", Group = "Operations", Order = 1)]
public class CostTrackingSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "CostTracking";

    public static string SectionKey => SectionName;

    [SettingDisplay(
        Name = "Enable Automatic Calculation",
        Description = "Automatically calculate costs when jobs complete.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enableAutomaticCostCalculation")]
    public bool EnableAutomaticCostCalculation { get; set; } = true;

    [SettingDisplay(
        Name = "Electricity Rate (per kWh)",
        Description = "Cost of electricity per kilowatt-hour (e.g., 0.12 for $0.12/kWh).",
        InputType = SettingInputType.Number,
        MinValue = 0,
        MaxValue = 10)]
    [JsonPropertyName("electricityRatePerKwh")]
    public decimal ElectricityRatePerKwh { get; set; } = 0.12m;

    [SettingDisplay(
        Name = "Default Machine Hourly Rate",
        Description = "Default hourly rate for machine time (e.g., 0.50 for $0.50/hour).",
        InputType = SettingInputType.Number,
        MinValue = 0,
        MaxValue = 100)]
    [JsonPropertyName("defaultMachineHourlyRate")]
    public decimal DefaultMachineHourlyRate { get; set; } = 0.50m;

    [SettingDisplay(
        Name = "Labor Markup Percent",
        Description = "Labor cost as percentage of material+energy+machine (e.g., 0 for no markup, 20 for 20%).",
        InputType = SettingInputType.Number,
        MinValue = 0,
        MaxValue = 200)]
    [JsonPropertyName("laborMarkupPercent")]
    public decimal LaborMarkupPercent { get; set; } = 0m;

    [SettingDisplay(
        Name = "Profit Margin Target Percent",
        Description = "Target profit margin for pricing calculations (e.g., 30 for 30%).",
        InputType = SettingInputType.Number,
        MinValue = 0,
        MaxValue = 500)]
    [JsonPropertyName("profitMarginTargetPercent")]
    public decimal ProfitMarginTargetPercent { get; set; } = 30m;

    [SettingDisplay(
        Name = "Average Printer Wattage",
        Description = "Average power consumption of printers in watts (used if printer-specific data unavailable).",
        InputType = SettingInputType.Number,
        MinValue = 0,
        MaxValue = 5000)]
    [JsonPropertyName("averagePrinterWattage")]
    public decimal AveragePrinterWattage { get; set; } = 250m;

    /// <summary>
    /// Global fallback price per kilogram when no Spoolman or material-specific price is available.
    /// </summary>
    [SettingDisplay(
        Name = "Default Filament Price (per kg)",
        Description = "Fallback filament price per kilogram when Spoolman pricing and material defaults are unavailable.",
        InputType = SettingInputType.Number,
        MinValue = 0,
        MaxValue = 500)]
    [JsonPropertyName("defaultFilamentPricePerKg")]
    public decimal DefaultFilamentPricePerKg { get; set; } = 25m;

    /// <summary>
    /// Per-material-type default prices ($/kg). Keys are material names (e.g., "PLA", "PETG").
    /// Used when Spoolman spool/filament has no price set.
    /// </summary>
    [SettingDisplay(
        Name = "Material Price Defaults",
        Description = "Default price per kilogram for each material type. Used when Spoolman pricing is unavailable.",
        InputType = SettingInputType.Custom)]
    [JsonPropertyName("materialPriceDefaults")]
    public Dictionary<string, decimal> MaterialPriceDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLA"] = 20m,
        ["PETG"] = 25m,
        ["ABS"] = 22m,
        ["TPU"] = 30m,
        ["ASA"] = 28m,
        ["Nylon"] = 35m,
        ["PC"] = 40m,
        ["PVA"] = 45m,
    };

    public void Validate()
    {
        if (ElectricityRatePerKwh < 0 || ElectricityRatePerKwh > 10)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Electricity rate must be between 0 and 10.");
        }

        if (DefaultMachineHourlyRate < 0 || DefaultMachineHourlyRate > 100)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Default machine hourly rate must be between 0 and 100.");
        }

        if (LaborMarkupPercent < 0 || LaborMarkupPercent > 200)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Labor markup percent must be between 0 and 200.");
        }

        if (ProfitMarginTargetPercent < 0 || ProfitMarginTargetPercent > 500)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Profit margin target percent must be between 0 and 500.");
        }

        if (AveragePrinterWattage < 0 || AveragePrinterWattage > 5000)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Average printer wattage must be between 0 and 5000.");
        }

        if (DefaultFilamentPricePerKg < 0 || DefaultFilamentPricePerKg > 500)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Default filament price must be between 0 and 500.");
        }

        foreach (KeyValuePair<string, decimal> entry in MaterialPriceDefaults)
        {
            if (entry.Value < 0 || entry.Value > 500)
            {
                throw new System.ComponentModel.DataAnnotations.ValidationException(
                    $"Material price for '{entry.Key}' must be between 0 and 500.");
            }
        }
    }
}
