using System;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>Unit tests for the single profile visibility rule introduced for issue #3174.</summary>
public class ProfileViewerTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void NonOwner_SeesOnlySystemOrPublic(bool isSystem, bool isPublic, bool expected)
    {
        ProfileViewer viewer = new(Other, IsAdmin: false);

        Assert.Equal(expected, viewer.CanView(isSystem, isPublic, Owner));
    }

    [Fact]
    public void Owner_SeesOwnPrivateProfile()
    {
        Assert.True(new ProfileViewer(Owner, IsAdmin: false).CanView(isSystem: false, isPublic: false, Owner));
    }

    [Fact]
    public void Admin_SeesAnyPrivateProfile()
    {
        Assert.True(new ProfileViewer(Other, IsAdmin: true).CanView(isSystem: false, isPublic: false, Owner));
        Assert.True(ProfileViewer.Administrator.CanView(isSystem: false, isPublic: false, ownerUserId: null));
    }

    [Fact]
    public void ViewerWithoutIdentity_NeverMatchesOwnerlessPrivateProfile()
    {
        ProfileViewer anonymous = new(null, IsAdmin: false);

        Assert.False(anonymous.CanView(isSystem: false, isPublic: false, ownerUserId: null));
        Assert.False(anonymous.CanView(isSystem: false, isPublic: false, Owner));
        Assert.True(anonymous.CanView(isSystem: false, isPublic: true, Owner));
    }

    [Fact]
    public void CanView_Entity_UsesEntityVisibilityFields()
    {
        ProcessProfile privateProfile = new() { Name = "p", IsPublic = false, IsSystem = false, CreatedByUserId = Owner };
        ProfileViewer other = new(Other, IsAdmin: false);

        Assert.False(other.CanView(privateProfile));
        Assert.True(new ProfileViewer(Owner, IsAdmin: false).CanView(privateProfile));
        _ = Assert.Throws<ArgumentNullException>(() => other.CanView((ProcessProfile)null!));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void CanView_FilamentEntity_AppliesSameRuleAsProcess(bool isSystem, bool isPublic, bool expectedForOther)
    {
        FilamentProfile profile = new() { Name = "f", IsSystem = isSystem, IsPublic = isPublic, CreatedByUserId = Owner };

        Assert.Equal(expectedForOther, new ProfileViewer(Other, IsAdmin: false).CanView(profile));
        Assert.Equal(expectedForOther, new ProfileViewer(null, IsAdmin: false).CanView(profile));
        Assert.True(new ProfileViewer(Owner, IsAdmin: false).CanView(profile));
        Assert.True(new ProfileViewer(Other, IsAdmin: true).CanView(profile));
        _ = Assert.Throws<ArgumentNullException>(() => ProfileViewer.Administrator.CanView((FilamentProfile)null!));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void CanView_MachineEntity_AppliesSameRuleAsProcess(bool isSystem, bool isPublic, bool expectedForOther)
    {
        MachineProfile profile = new() { Name = "m", IsSystem = isSystem, IsPublic = isPublic, CreatedByUserId = Owner };

        Assert.Equal(expectedForOther, new ProfileViewer(Other, IsAdmin: false).CanView(profile));
        Assert.Equal(expectedForOther, new ProfileViewer(null, IsAdmin: false).CanView(profile));
        Assert.True(new ProfileViewer(Owner, IsAdmin: false).CanView(profile));
        Assert.True(new ProfileViewer(Other, IsAdmin: true).CanView(profile));
        _ = Assert.Throws<ArgumentNullException>(() => ProfileViewer.Administrator.CanView((MachineProfile)null!));
    }

    [Fact]
    public void CanView_OwnerlessPrivateFilamentAndMachine_HiddenFromEveryNonAdmin()
    {
        FilamentProfile filament = new() { Name = "f", IsSystem = false, IsPublic = false, CreatedByUserId = null };
        MachineProfile machine = new() { Name = "m", IsSystem = false, IsPublic = false, CreatedByUserId = null };

        Assert.False(new ProfileViewer(null, IsAdmin: false).CanView(filament));
        Assert.False(new ProfileViewer(null, IsAdmin: false).CanView(machine));
        Assert.False(new ProfileViewer(Other, IsAdmin: false).CanView(filament));
        Assert.False(new ProfileViewer(Other, IsAdmin: false).CanView(machine));
    }
}
