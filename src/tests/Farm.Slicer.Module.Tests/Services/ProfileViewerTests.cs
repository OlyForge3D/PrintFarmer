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
}
