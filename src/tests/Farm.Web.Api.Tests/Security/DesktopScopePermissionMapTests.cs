using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
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
    /// <summary>An owner with no explicit denies — the ordinary case.</summary>
    private static readonly IReadOnlySet<string> NoDenies = new HashSet<string>(StringComparer.Ordinal);
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
        Enum.GetUnderlyingType(typeof(ApiKeyScope)).Should().Be<int>();
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
            .Should().BeEquivalentTo(new[] { "SlicingSubmit" });
    }

    /// <summary>
    /// Least privilege: generation submits a slice job and polls calibration orchestration, so it
    /// must NOT drag in artifact-download authority. <see cref="ApiKeyScope.SlicingReadArtifact"/>
    /// stays independently selectable for clients that genuinely download bytes.
    /// </summary>
    [Fact]
    public void CalibrationGenerate_DoesNotRequireSlicingReadArtifact()
    {
        DesktopScopeDefinition generate = DesktopScopePermissionMap.Definitions
            .Single(d => d.Scope == ApiKeyScope.CalibrationGenerate);

        generate.Requires.Should().NotContain(ApiKeyScope.SlicingReadArtifact);
        generate.Requires.Should().BeEquivalentTo(
            new[] { ApiKeyScope.CalibrationRead, ApiKeyScope.SlicingSubmit });

        DesktopScopePermissionMap.GetUnsatisfiedDependencies(
            ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationGenerate | ApiKeyScope.SlicingSubmit)
            .Should().BeEmpty("generation needs only calibration:read, calibration:generate and slicing:submit");

        DesktopScopePermissionMap.GetPermissions(
            ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationGenerate | ApiKeyScope.SlicingSubmit)
            .Should().NotContain(PrintFarmerPermissions.Slicing.ReadArtifact);
    }

    [Fact]
    public void GetUnsatisfiedDependencies_AcceptsCompleteGenerationSelection()
    {
        ApiKeyScope scopes = ApiKeyScope.CalibrationRead |
            ApiKeyScope.CalibrationGenerate |
            ApiKeyScope.SlicingSubmit;

        DesktopScopePermissionMap.GetUnsatisfiedDependencies(scopes).Should().BeEmpty();
    }

    /// <summary>
    /// Round-3 review fix (Bishop B8, issue #2180): completing a calibration project promotes its
    /// draft profile via a slicer-module endpoint class-gated by slicing:submit in addition to its
    /// own method-level calibration:update requirement. Before this fix, calibration:read +
    /// calibration:update was a validly-issuable combination that would nonetheless dead-end at
    /// project completion.
    /// </summary>
    [Fact]
    public void GetUnsatisfiedDependencies_FlagsCalibrationUpdateWithoutSlicing()
    {
        IReadOnlyList<(string Scope, string MissingPrerequisite)> unsatisfied =
            DesktopScopePermissionMap.GetUnsatisfiedDependencies(
                ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationUpdate);

        unsatisfied.Select(u => u.MissingPrerequisite)
            .Should().BeEquivalentTo(new[] { "SlicingSubmit" });
    }

    [Fact]
    public void GetUnsatisfiedDependencies_AcceptsCompleteCalibrationUpdateSelection()
    {
        ApiKeyScope scopes = ApiKeyScope.CalibrationRead |
            ApiKeyScope.CalibrationUpdate |
            ApiKeyScope.SlicingSubmit;

        DesktopScopePermissionMap.GetUnsatisfiedDependencies(scopes).Should().BeEmpty();
    }

    #region ResolveEffectiveScopes (stored ∩ live)

    [Fact]
    public void ResolveEffectiveScopes_StoredAndLive_KeepsTheIntersection()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationDelete,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { PrintFarmerPermissions.Calibration.Read },
            NoDenies);

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
            },
            NoDenies);

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
            new HashSet<string>(StringComparer.Ordinal),
            NoDenies);

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
            new HashSet<string>(StringComparer.Ordinal),
            NoDenies);

        result.Effective.Should().Be(ApiKeyScope.ModelRead | ApiKeyScope.LibrarySync);
        result.Dropped.Should().Be(ApiKeyScope.CalibrationRead);
    }

    [Fact]
    public void ResolveEffectiveScopes_FarmAdminOwner_AuthorizesOnlyWhatWasSelected()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead,
            isOwnerFarmAdmin: true,
            new HashSet<string>(StringComparer.Ordinal),
            NoDenies);

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
            new HashSet<string>(StringComparer.Ordinal) { PrintFarmerPermissions.Calibration.Read },
            NoDenies);

        IReadOnlyList<string> scopeNames = DesktopScopePermissionMap.GetScopeNames(result.Effective);
        IReadOnlyList<string> permissions = DesktopScopePermissionMap.GetPermissions(result.Effective);

        scopeNames.Should().BeEquivalentTo(new[] { "ModelRead", "CalibrationRead" });
        permissions.Should().Equal(PrintFarmerPermissions.Calibration.Read);
        scopeNames.Should().NotContain("CalibrationDelete");
        permissions.Should().NotContain(PrintFarmerPermissions.Calibration.Delete);
    }

    #endregion

    #region Resource-admin implication (issue #1447 / PR #1463 semantics)

    /// <summary>
    /// A grant of <c>{resource}:admin</c> authorizes every finer-grained action on that resource at
    /// the enforcement points, so the owner-authority intersection must honour it too. Otherwise an
    /// owner holding <c>calibration:admin</c> would lose calibration scopes here while PrintFarmer
    /// still authorizes those actions for them.
    /// </summary>
    [Theory]
    [InlineData(ApiKeyScope.CalibrationRead)]
    [InlineData(ApiKeyScope.CalibrationCreate)]
    [InlineData(ApiKeyScope.CalibrationUpdate)]
    [InlineData(ApiKeyScope.CalibrationDelete)]
    [InlineData(ApiKeyScope.CalibrationGenerate)]
    [InlineData(ApiKeyScope.CalibrationPublish)]
    public void ResolveEffectiveScopes_CalibrationAdmin_AuthorizesEverySelectedCalibrationScope(ApiKeyScope scope)
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            scope,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { "calibration:admin" },
            NoDenies);

        result.Effective.Should().Be(scope);
        result.Dropped.Should().Be(ApiKeyScope.None);
    }

    /// <summary>
    /// Only what the key selected: a resource-admin grant is authority, not selection.
    /// </summary>
    [Fact]
    public void ResolveEffectiveScopes_CalibrationAdmin_DoesNotAddUnselectedCalibrationScopes()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { "calibration:admin" },
            NoDenies);

        result.Effective.Should().Be(ApiKeyScope.CalibrationRead);
        DesktopScopePermissionMap.GetPermissions(result.Effective)
            .Should().Equal(PrintFarmerPermissions.Calibration.Read);
    }

    [Theory]
    [InlineData(ApiKeyScope.QueueRead)]
    [InlineData(ApiKeyScope.QueueWrite)]
    [InlineData(ApiKeyScope.QueueStart)]
    [InlineData(ApiKeyScope.QueueCancel)]
    [InlineData(ApiKeyScope.QueueAcknowledgeBedClear)]
    public void ResolveEffectiveScopes_QueueAdmin_AuthorizesEverySelectedQueueScope(ApiKeyScope scope)
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            scope,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { "queue:admin" },
            NoDenies);

        result.Effective.Should().Be(scope);
    }

    [Theory]
    [InlineData(ApiKeyScope.SlicingSubmit)]
    [InlineData(ApiKeyScope.SlicingReadArtifact)]
    public void ResolveEffectiveScopes_SlicingAdmin_AuthorizesEverySelectedSlicingScope(ApiKeyScope scope)
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            scope,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { "slicing:admin" },
            NoDenies);

        result.Effective.Should().Be(scope);
    }

    /// <summary>
    /// The implication is same-resource only. A calibration admin gets no queue or slicing
    /// authority, and vice versa — this is the escalation the rule must not permit.
    /// </summary>
    [Theory]
    [InlineData("calibration:admin")]
    [InlineData("queue:admin")]
    [InlineData("slicing:admin")]
    public void ResolveEffectiveScopes_ResourceAdmin_NeverCrossesResources(string adminGrant)
    {
        ApiKeyScope everyPrivilegedScope = DesktopScopePermissionMap.PermissionBackedScopes;

        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            everyPrivilegedScope,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { adminGrant },
            NoDenies);

        string resource = adminGrant.Split(':')[0];
        foreach (DesktopScopeDefinition definition in DesktopScopePermissionMap.Definitions
            .Where(d => d.Permission is not null))
        {
            bool sameResource = definition.Permission!.StartsWith($"{resource}:", StringComparison.Ordinal);
            bool survived = (result.Effective & definition.Scope) == definition.Scope;

            survived.Should().Be(
                sameResource,
                $"{definition.Name} maps to {definition.Permission} and the grant was {adminGrant}");
        }
    }

    /// <summary>
    /// A wildcard-looking grant must not be invented: `*:admin` is not a resource.
    /// </summary>
    [Theory]
    [InlineData("*:admin")]
    [InlineData("admin")]
    [InlineData("calibration:administrator")]
    [InlineData("Calibration:Admin")]
    public void ResolveEffectiveScopes_NonCanonicalAdminGrants_ConferNothing(string grant)
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { grant },
            NoDenies);

        result.Effective.Should().Be(ApiKeyScope.None);
        result.Dropped.Should().Be(ApiKeyScope.CalibrationRead);
    }

    [Fact]
    public void ResolveEffectiveScopes_ExactPermissionStillWorksAlongsideTheImplication()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead | ApiKeyScope.QueueRead,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal)
            {
                PrintFarmerPermissions.Calibration.Read,
                "queue:admin",
            },
            NoDenies);

        result.Effective.Should().Be(ApiKeyScope.CalibrationRead | ApiKeyScope.QueueRead);
        result.Dropped.Should().Be(ApiKeyScope.None);
    }

    [Fact]
    public void ResolveEffectiveScopes_OwnerWithNeitherExactNorAdminGrant_DropsTheScope()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { "queue:admin", PrintFarmerPermissions.Slicing.Submit },
            NoDenies);

        result.Effective.Should().Be(ApiKeyScope.None);
        result.Dropped.Should().Be(ApiKeyScope.CalibrationRead);
    }

    /// <summary>
    /// The set-based overload must agree with the canonical principal-based rule, since both
    /// delegate to the same core — this is the guard against the two drifting apart.
    /// </summary>
    [Theory]
    [InlineData("calibration", "read", true)]
    [InlineData("calibration", "publish", true)]
    [InlineData("calibration", "admin", false)]
    [InlineData("queue", "read", false)]
    public void SetBasedImplication_MatchesThePrincipalBasedRule(string resource, string action, bool expected)
    {
        HashSet<string> permissions = new(StringComparer.Ordinal) { "calibration:admin" };
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim(PrintFarmerPermissions.ClaimType, "calibration:admin")],
            "TestAuth"));

        PrintFarmerPermissions.ImpliesViaResourceAdmin(permissions, NoDenies, resource, action).Should().Be(expected);
        PrintFarmerPermissions.ImpliesViaResourceAdmin(principal, resource, action).Should().Be(expected);
    }

    /// <summary>
    /// Explicit deny wins over the same-resource admin implication, on the set-based path exactly
    /// as it does on the claims-based path (#1472 / docs/ROLE_PERMISSION_PRECEDENCE.md). Without
    /// this, a Desktop key could be provisioned with an action its owner was explicitly denied.
    /// </summary>
    [Fact]
    public void ResolveEffectiveScopes_ExplicitDeny_SuppressesTheResourceAdminImplication()
    {
        HashSet<string> denies = new(StringComparer.Ordinal) { PrintFarmerPermissions.Calibration.Delete };

        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationDelete,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { "calibration:admin" },
            denies);

        result.Effective.Should().Be(ApiKeyScope.CalibrationRead, "the deny removes only the denied action");
        result.Dropped.Should().Be(ApiKeyScope.CalibrationDelete);
    }

    /// <summary>An explicit deny also beats an exact grant, matching the resolved-permission rule.</summary>
    [Fact]
    public void ResolveEffectiveScopes_ExplicitDeny_BeatsAnExactGrant()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { PrintFarmerPermissions.Calibration.Read },
            new HashSet<string>(StringComparer.Ordinal) { PrintFarmerPermissions.Calibration.Read });

        result.Effective.Should().Be(ApiKeyScope.None);
        result.Dropped.Should().Be(ApiKeyScope.CalibrationRead);
    }

    /// <summary>A deny on one resource must not suppress another resource's admin implication.</summary>
    [Fact]
    public void ResolveEffectiveScopes_ExplicitDeny_DoesNotCrossResources()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead | ApiKeyScope.QueueRead,
            isOwnerFarmAdmin: false,
            new HashSet<string>(StringComparer.Ordinal) { "calibration:admin", "queue:admin" },
            new HashSet<string>(StringComparer.Ordinal) { PrintFarmerPermissions.Calibration.Read });

        result.Effective.Should().Be(ApiKeyScope.QueueRead);
        result.Dropped.Should().Be(ApiKeyScope.CalibrationRead);
    }

    /// <summary>
    /// The <c>farm_admin</c> role bypass is deliberately left untouched by #1472, so it still
    /// authorizes even against an explicit deny. Pinned so the behaviour is a decision, not a
    /// surprise.
    /// </summary>
    [Fact]
    public void ResolveEffectiveScopes_FarmAdminOwner_IsUnaffectedByExplicitDeny()
    {
        EffectiveDesktopScopes result = DesktopScopePermissionMap.ResolveEffectiveScopes(
            ApiKeyScope.CalibrationRead,
            isOwnerFarmAdmin: true,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { PrintFarmerPermissions.Calibration.Read });

        result.Effective.Should().Be(ApiKeyScope.CalibrationRead);
    }

    #endregion
}
