namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Describes a custom profile that could not be loaded because an inherited
/// profile was unavailable.
/// </summary>
/// <param name="BundleName">Custom manufacturer bundle containing the profile.</param>
/// <param name="FamilyName">PrintFarmer family associated with the profile.</param>
/// <param name="ProfileName">Profile whose inheritance chain is incomplete.</param>
/// <param name="MissingParent">Inherited profile name that could not be resolved.</param>
public sealed record CustomProfileLoadFailure(
    string BundleName,
    string FamilyName,
    string ProfileName,
    string MissingParent);
