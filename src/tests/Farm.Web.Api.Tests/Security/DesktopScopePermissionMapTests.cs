using System;
using System.Collections.Generic;
using System.Linq;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Guards the Desktop scope contract itself. These are the invariants that make the rest of the
/// least-privilege design safe: single-bit expansion, a validation mask that is not
/// <see cref="ApiKeyScope.All"/>, and exhaustive metadata for every defined bit.
/// </summary>
public class DesktopScopePermissionMapTests
{
    [Fact]
    public void Definitions_ContainOnlySingleBitFlags()
    {
        foreach (DesktopScopeDefinition definition in DesktopScopePermissionMap.Definitions)
        {
            int value = (int)definition.Scope;
            value.Should().BePositive($"{definition.Name} must be a real bit");
            (value & (value - 1)).Should().Be(0, $"{definition.Name} must be a single power-of-two bit, not a composite alias");
        }
    }

    /// <summary>
    /// Exhaustiveness: every bit in the mask must have metadata, and every enum member other than
    /// the two intentional aggregates must appear. Adding a flag without registering it here fails.
    /// </summary>
    [Fact]
    public void EveryDefinedEnumMember_ExceptAggregates_HasMetadata()
    {
        HashSet<ApiKeyScope> described = [.. DesktopScopePermissionMap.Definitions.Select(d => d.Scope)];

        List<ApiKeyScope> undescribed = [.. Enum.GetValues<ApiKeyScope>()
            .Where(v => v != ApiKeyScope.None && v != ApiKeyScope.All)
            .Where(v => !described.Contains(v))];

        undescribed.Should().BeEmpty("every non-aggregate ApiKeyScope member must be registered in DesktopScopePermissionMap.Definitions");
    }

    [Fact]
    public void KnownScopeMask_CoversEveryDefinedBitAndNothingElse()
    {
        int mask = (int)DesktopScopePermissionMap.KnownScopeMask;
        int union = DesktopScopePermissionMap.Definitions.Aggregate(0, (acc, d) => acc | (int)d.Scope);

        mask.Should().Be(union);
        DesktopScopePermissionMap.HasUndefinedBits(DesktopScopePermissionMap.KnownScopeMask).Should().BeFalse();
    }

    /// <summary>
    /// The single most important regression guard: widening the enum must never widen the meaning
    /// of a key already stored as 7.
    /// </summary>
    [Fact]
    public void All_IsFrozenAtTheThreeLegacyModelScopes()
    {
        ((int)ApiKeyScope.All).Should().Be(7);
        ApiKeyScope.All.Should().Be(ApiKeyScope.ModelRead | ApiKeyScope.ModelWrite | ApiKeyScope.LibrarySync);
        (ApiKeyScope.All & DesktopScopePermissionMap.PermissionBackedScopes).Should().Be(ApiKeyScope.None);
    }

    [Fact]
    public void ApiKeyScope_UnderlyingType_IsIntToMatchThePersistedColumn()
    {
        Enum.GetUnderlyingType(typeof(ApiKeyScope)).Should().Be(typeof(int));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void LegacyNumericValues_MapToZeroPermissions(int stored)
    {
        var scopes = (ApiKeyScope)stored;

        DesktopScopePermissionMap.HasUndefinedBits(scopes).Should().BeFalse();
        DesktopScopePermissionMap.GetPermissions(scopes).Should().BeEmpty();
        (scopes & DesktopScopePermissionMap.PermissionBackedScopes).Should().Be(ApiKeyScope.None);
    }

    /// <summary>
    /// A composite alias must never surface as a claim value. Iterating Enum.GetValues + HasFlag
    /// would emit a bogus "All" scope here.
    /// </summary>
    [Fact]
    public void GetScopeNames_ForStoredSeven_ExpandsToIndividualNamesNeverAll()
    {
        IReadOnlyList<string> names = DesktopScopePermissionMap.GetScopeNames((ApiKeyScope)7);

        names.Should().BeEquivalentTo(new[] { "ModelRead", "ModelWrite", "LibrarySync" });
        names.Should().NotContain("All");
        names.Should().NotContain("None");
    }

    [Theory]
    [InlineData(1 << 3)]
    [InlineData(1 << 7)]
    [InlineData(1 << 30)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void HasUndefinedBits_RejectsReservedNegativeAndUnknownBits(int raw)
    {
        DesktopScopePermissionMap.HasUndefinedBits((ApiKeyScope)raw).Should().BeTrue();
    }

    [Fact]
    public void ModelLibraryScopes_HaveNoMappedPermission()
    {
        foreach (ApiKeyScope scope in new[] { ApiKeyScope.ModelRead, ApiKeyScope.ModelWrite, ApiKeyScope.LibrarySync })
        {
            DesktopScopePermissionMap.PermissionByScope.ContainsKey(scope).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(ApiKeyScope.CalibrationRead, "calibration:read")]
    [InlineData(ApiKeyScope.CalibrationCreate, "calibration:create")]
    [InlineData(ApiKeyScope.CalibrationUpdate, "calibration:update")]
    [InlineData(ApiKeyScope.CalibrationDelete, "calibration:delete")]
    [InlineData(ApiKeyScope.CalibrationGenerate, "calibration:generate")]
    [InlineData(ApiKeyScope.CalibrationPublish, "calibration:publish")]
    [InlineData(ApiKeyScope.SlicingSubmit, "slicing:submit")]
    [InlineData(ApiKeyScope.SlicingReadArtifact, "slicing:read-artifact")]
    [InlineData(ApiKeyScope.QueueRead, "queue:read")]
    [InlineData(ApiKeyScope.QueueWrite, "queue:write")]
    [InlineData(ApiKeyScope.QueueStart, "queue:start")]
    [InlineData(ApiKeyScope.QueueCancel, "queue:cancel")]
    [InlineData(ApiKeyScope.QueueAcknowledgeBedClear, "queue:acknowledge-bed-clear")]
    public void EachPrivilegedScope_MapsToExactlyItsPermission(ApiKeyScope scope, string expected)
    {
        DesktopScopePermissionMap.GetPermissions(scope).Should().Equal(expected);
    }

    /// <summary>
    /// Explicitly excluded from the Desktop surface: no scope may grant these.
    /// </summary>
    [Theory]
    [InlineData("queue:reconcile")]
    [InlineData("slicing:promote")]
    [InlineData("dispatch-settings:manage")]
    [InlineData("obico:manage")]
    public void ExcludedPermissions_AreNotReachableByAnyScope(string permission)
    {
        DesktopScopePermissionMap.PermissionByScope.Values.Should().NotContain(permission);
    }

    [Fact]
    public void TryParseScopeName_RejectsCompositeAliasesAndUnknownNames()
    {
        DesktopScopePermissionMap.TryParseScopeName("All", out _).Should().BeFalse();
        DesktopScopePermissionMap.TryParseScopeName("None", out _).Should().BeFalse();
        DesktopScopePermissionMap.TryParseScopeName("NotAScope", out _).Should().BeFalse();
        DesktopScopePermissionMap.TryParseScopeName("", out _).Should().BeFalse();
        DesktopScopePermissionMap.TryParseScopeName(null, out _).Should().BeFalse();

        DesktopScopePermissionMap.TryParseScopeName("calibrationread", out ApiKeyScope parsed).Should().BeTrue();
        parsed.Should().Be(ApiKeyScope.CalibrationRead);
    }

    [Fact]
    public void GetUnsatisfiedDependencies_FlagsGenerationWithoutSlicing()
    {
        IReadOnlyList<(string Scope, string MissingPrerequisite)> unsatisfied =
            DesktopScopePermissionMap.GetUnsatisfiedDependencies(
                ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationGenerate);

        unsatisfied.Select(u => u.MissingPrerequisite)
            .Should().BeEquivalentTo(new[] { "SlicingSubmit", "SlicingReadArtifact" });
    }

    [Fact]
    public void GetUnsatisfiedDependencies_AcceptsCompleteGenerationSelection()
    {
        ApiKeyScope scopes = ApiKeyScope.CalibrationRead |
            ApiKeyScope.CalibrationGenerate |
            ApiKeyScope.SlicingSubmit |
            ApiKeyScope.SlicingReadArtifact;

        DesktopScopePermissionMap.GetUnsatisfiedDependencies(scopes).Should().BeEmpty();
    }

    #region ResolveEffectiveScopes (stored ∩ live)

    [Fact]
    public void ResolveEffectiveScopes_StoredAndLive_KeepsTheIntersection()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationDelete,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { PrintFarmerPermissions.Calibration.Read });

        result.Effective.Should().Be(ApiKeyScope.CalibrationRead);
        result.Dropped.Should().Be(ApiKeyScope.CalibrationDelete);
        result.WasDowngraded.Should().BeTrue();
    }

    /// <summary>
    /// Live-only: a permission the owner holds but never selected on the key must not appear.
    /// </summary>
    [Fact]
    public void ResolveEffectiveScopes_LiveOnly_DoesNotAddUnselectedScopes()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal)
            {
                PrintFarmerPermissions.Calibration.Read,
                PrintFarmerPermissions.Calibration.Delete,
                PrintFarmerPermissions.Queue.Start,
            });

        result.Effective.Should().Be(ApiKeyScope.CalibrationRead);
        DesktopScopePermissionMap.GetPermissions(result.Effective).Should().Equal(PrintFarmerPermissions.Calibration.Read);
    }

    /// <summary>
    /// Stored-only: nothing live means every privileged scope is dropped.
    /// </summary>
    [Fact]
    public void ResolveEffectiveScopes_StoredOnly_DropsEveryPrivilegedScope()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead | ApiKeyScope.QueueStart,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal));

        result.Effective.Should().Be(ApiKeyScope.None);
        result.Dropped.Should().Be(ApiKeyScope.CalibrationRead | ApiKeyScope.QueueStart);
    }

    /// <summary>
    /// Downgrade, not collapse: losing a calibration grant must not break model sync.
    /// </summary>
    [Fact]
    public void ResolveEffectiveScopes_RetainsModelScopesWhenPrivilegedScopesAreRevoked()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.ModelRead | ApiKeyScope.LibrarySync | ApiKeyScope.CalibrationRead,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal));

        result.Effective.Should().Be(ApiKeyScope.ModelRead | ApiKeyScope.LibrarySync);
        result.Dropped.Should().Be(ApiKeyScope.CalibrationRead);
    }

    [Fact]
    public void ResolveEffectiveScopes_FarmAdminOwner_AuthorizesOnlyWhatWasSelected()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead,
            isOwnerFarmAdmin: true,
            new HashSet<string>(StringComparer.Ordinal));

        result.Effective.Should().Be(ApiKeyScope.CalibrationRead);
        result.Dropped.Should().Be(ApiKeyScope.None);
        DesktopScopePermissionMap.GetPermissions(result.Effective)
            .Should().Equal(PrintFarmerPermissions.Calibration.Read);
    }

    /// <summary>
    /// The effective mask is the single source for both claim families - they can never disagree.
    /// </summary>
    [Fact]
    public void ScopeNamesAndPermissions_AreAlwaysDerivedFromTheSameEffectiveMask()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.ModelRead | ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationDelete,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { PrintFarmerPermissions.Calibration.Read });

        IReadOnlyList<string> scopeNames = DesktopScopePermissionMap.GetScopeNames(result.Effective);
        IReadOnlyList<string> permissions = DesktopScopePermissionMap.GetPermissions(result.Effective);

        scopeNames.Should().BeEquivalentTo(new[] { "ModelRead", "CalibrationRead" });
        permissions.Should().Equal(PrintFarmerPermissions.Calibration.Read);
        scopeNames.Should().NotContain("CalibrationDelete");
        permissions.Should().NotContain(PrintFarmerPermissions.Calibration.Delete);
    }

    #endregion
}
