using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using Fido2NetLib;
using Fido2NetLib.Exceptions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Modules.Identity.Tests.Controllers;

public class PasskeyAccountEligibilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PasskeyLoginCompleteAsync_CurrentAccountEligibility_ControlsBothTokens(bool isActive)
    {
        DbContextOptions<AppDbContext> dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext db = new(dbOptions);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = "passkey-user",
            Email = "passkey@example.com",
            PasswordHash = "unused",
            IsActive = true
        };
        UserPasskeyCredential credential = new()
        {
            User = user,
            UserId = user.Id,
            CredentialId = [1, 2, 3, 4],
            PublicKey = EncodePublicKey(key),
            SignCount = 0
        };
        db.UserPasskeyCredentials.Add(credential);
        await db.SaveChangesAsync();

        Mock<IAuthenticationService> auth = new();
        auth.Setup(service => service.GenerateJwtTokenAsync(It.IsAny<User>())).ReturnsAsync("access-token");
        auth.Setup(service => service.GetUserWithRolesAndPermissionsAsync(user.Id))
            .ReturnsAsync(new UserDto { Id = user.Id, Username = user.Username, IsActive = true });
        Mock<IRefreshTokenService> refresh = new();
        refresh.Setup(service => service.CreateAsync(user.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("refresh-token", DateTime.UtcNow.AddDays(30)));
        MemoryDistributedCache cache = new(Options.Create(new MemoryDistributedCacheOptions()));
        PasskeyService passkeys = new(
            new Fido2(new Fido2Configuration
            {
                RPID = "localhost",
                RPName = "PrintFarmer",
                Origins = new HashSet<string> { "http://localhost:3000" }
            }),
            cache, db, new EfUsersRepository(db), auth.Object, [], NullLogger<PasskeyService>.Instance);
        AssertionOptions options = await passkeys.BeginLoginAsync(user.Username);
        AuthenticatorAssertionRawResponse assertion = CreateAssertion(options, credential, key);

        // Deactivation occurs after the challenge began, in another context. The
        // original context still tracks an active owner and must not be trusted.
        if (!isActive)
        {
            await using AppDbContext adminDb = new(dbOptions);
            User persistedUser = await adminDb.Users.SingleAsync(candidate => candidate.Id == user.Id);
            persistedUser.IsActive = false;
            await adminDb.SaveChangesAsync();
        }

        Farm.Modules.Identity.Controllers.AuthController controller = new(
            auth.Object, Mock.Of<ILoginAuditService>(), passkeys,
            NullLogger<Farm.Modules.Identity.Controllers.AuthController>.Instance,
            Mock.Of<IApiKeyExchangeService>(), refresh.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        IActionResult result = await controller.PasskeyLoginCompleteAsync(
            new Farm.Modules.Identity.Controllers.PasskeyLoginCompleteRequest(user.Username, assertion),
            CancellationToken.None);

        AuthenticationResult response;
        if (isActive)
        {
            response = result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<AuthenticationResult>().Subject;
            response.Success.Should().BeTrue();
            response.Token.Should().Be("access-token");
            response.RefreshToken.Should().Be("refresh-token");
            credential.SignCount.Should().Be(1);
            credential.LastUsedAt.Should().NotBeNull();
        }
        else
        {
            response = result.Should().BeOfType<UnauthorizedObjectResult>().Subject.Value
                .Should().BeOfType<AuthenticationResult>().Subject;
            response.Success.Should().BeFalse();
            response.Token.Should().BeNull();
            response.RefreshToken.Should().BeNull();
            response.ExpiresAt.Should().BeNull();
            response.RefreshTokenExpires.Should().BeNull();
            credential.SignCount.Should().Be(0);
            credential.LastUsedAt.Should().BeNull();
            user.LastLogin.Should().BeNull();
        }

        auth.Verify(service => service.GenerateJwtTokenAsync(It.IsAny<User>()), isActive ? Times.Once() : Times.Never());
        refresh.Verify(service => service.CreateAsync(
            It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), isActive ? Times.Once() : Times.Never());

        Func<Task> replay = async () => await passkeys.CompleteLoginAsync(user.Username, assertion);
        await replay.Should().ThrowAsync<PasskeyChallengeNotFoundException>();
    }

    [Theory]
    [InlineData("origin", Fido2ErrorCode.InvalidAuthenticatorResponseOrigin)]
    [InlineData("challenge", Fido2ErrorCode.InvalidAuthenticatorResponseChallenge)]
    [InlineData("rp", Fido2ErrorCode.InvalidRpidHash)]
    [InlineData("crossOrigin", Fido2ErrorCode.CrossOriginRequestNotAllowed)]
    [InlineData("backupFlags", Fido2ErrorCode.InvalidBackupFlags)]
    [InlineData("counter", Fido2ErrorCode.InvalidSignCount)]
    [InlineData("signature", Fido2ErrorCode.InvalidSignature)]
    public async Task CompleteLoginAsync_InvalidAssertion_RejectsWithoutUpdatingCredential(
        string scenario, Fido2ErrorCode expectedCode)
    {
        DbContextOptions<AppDbContext> dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext db = new(dbOptions);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = "passkey-user",
            Email = "passkey@example.com",
            PasswordHash = "unused",
            IsActive = true
        };
        uint storedCount = scenario == "counter" ? 1u : 0u;
        UserPasskeyCredential credential = new()
        {
            User = user,
            UserId = user.Id,
            CredentialId = [1, 2, 3, 4],
            PublicKey = EncodePublicKey(key),
            SignCount = storedCount
        };
        db.UserPasskeyCredentials.Add(credential);
        await db.SaveChangesAsync();
        Mock<IAuthenticationService> auth = new();
        MemoryDistributedCache cache = new(Options.Create(new MemoryDistributedCacheOptions()));
        PasskeyService passkeys = new(
            new Fido2(new Fido2Configuration
            {
                RPID = "localhost",
                RPName = "PrintFarmer",
                Origins = new HashSet<string> { "http://localhost:3000" }
            }),
            cache, db, new EfUsersRepository(db), auth.Object, [], NullLogger<PasskeyService>.Instance);
        AssertionOptions options = await passkeys.BeginLoginAsync(user.Username);
        AuthenticatorAssertionRawResponse assertion = CreateAssertion(options, credential, key, scenario);

        Func<Task> complete = async () => await passkeys.CompleteLoginAsync(user.Username, assertion);

        var exception = await complete.Should().ThrowAsync<Fido2VerificationException>();
        exception.Which.Code.Should().Be(expectedCode);
        credential.SignCount.Should().Be(storedCount);
        credential.LastUsedAt.Should().BeNull();
        auth.Verify(service => service.GenerateJwtTokenAsync(It.IsAny<User>()), Times.Never());
    }

    [Theory]
    [InlineData("origin", Fido2ErrorCode.InvalidAuthenticatorResponseOrigin)]
    [InlineData("challenge", Fido2ErrorCode.InvalidAuthenticatorResponseChallenge)]
    [InlineData("rp", Fido2ErrorCode.InvalidRpidHash)]
    [InlineData("crossOrigin", Fido2ErrorCode.CrossOriginRequestNotAllowed)]
    [InlineData("backupFlags", Fido2ErrorCode.InvalidBackupFlags)]
    [InlineData("attStmt", Fido2ErrorCode.InvalidAttestation)]
    public async Task CompleteRegistrationAsync_InvalidAttestation_RejectsWithoutStoringCredential(
        string scenario, Fido2ErrorCode expectedCode)
    {
        DbContextOptions<AppDbContext> dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext db = new(dbOptions);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = "passkey-user",
            Email = "passkey@example.com",
            PasswordHash = "unused",
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        MemoryDistributedCache cache = new(Options.Create(new MemoryDistributedCacheOptions()));
        PasskeyService passkeys = new(
            new Fido2(new Fido2Configuration
            {
                RPID = "localhost",
                RPName = "PrintFarmer",
                Origins = new HashSet<string> { "http://localhost:3000" }
            }),
            cache, db, new EfUsersRepository(db), Mock.Of<IAuthenticationService>(), [],
            NullLogger<PasskeyService>.Instance);
        CredentialCreateOptions options = await passkeys.BeginRegistrationAsync(user.Id, user.Username);
        AuthenticatorAttestationRawResponse attestation = CreateAttestation(options, key, scenario);

        Func<Task> complete = async () => await passkeys.CompleteRegistrationAsync(user.Username, attestation);

        var exception = await complete.Should().ThrowAsync<Fido2VerificationException>();
        exception.Which.Code.Should().Be(expectedCode);
        (await db.UserPasskeyCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CompleteRegistrationAsync_ValidNoneAttestation_StoresCredential()
    {
        DbContextOptions<AppDbContext> dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext db = new(dbOptions);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = "passkey-user",
            Email = "passkey@example.com",
            PasswordHash = "unused",
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        MemoryDistributedCache cache = new(Options.Create(new MemoryDistributedCacheOptions()));
        PasskeyService passkeys = new(
            new Fido2(new Fido2Configuration
            {
                RPID = "localhost",
                RPName = "PrintFarmer",
                Origins = new HashSet<string> { "http://localhost:3000" }
            }),
            cache, db, new EfUsersRepository(db), Mock.Of<IAuthenticationService>(), [],
            NullLogger<PasskeyService>.Instance);
        CredentialCreateOptions options = await passkeys.BeginRegistrationAsync(user.Id, user.Username);

        await passkeys.CompleteRegistrationAsync(user.Username, CreateAttestation(options, key, null));

        UserPasskeyCredential stored = await db.UserPasskeyCredentials.SingleAsync();
        stored.UserId.Should().Be(user.Id);
        stored.CredentialId.Should().Equal(1, 2, 3, 4);
        stored.PublicKey.Should().Equal(EncodePublicKey(key));
    }

    private static byte[] EncodePublicKey(ECDsa key)
    {
        ECParameters parameters = key.ExportParameters(false);
        CborWriter writer = new();
        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteInt32(2); // COSE EC2 key
        writer.WriteInt32(3);
        writer.WriteInt32(-7); // ES256
        writer.WriteInt32(-1);
        writer.WriteInt32(1); // P-256
        writer.WriteInt32(-2);
        writer.WriteByteString(parameters.Q.X!);
        writer.WriteInt32(-3);
        writer.WriteByteString(parameters.Q.Y!);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static AuthenticatorAttestationRawResponse CreateAttestation(
        CredentialCreateOptions options, ECDsa key, string? scenario)
    {
        byte[] credentialId = [1, 2, 3, 4];
        byte[] clientData = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.create",
            challenge = Base64Url(scenario == "challenge" ? new byte[32] : options.Challenge),
            origin = scenario == "origin" ? "https://other.example.com" : "http://localhost:3000",
            crossOrigin = scenario == "crossOrigin"
        });
        byte[] rpHash = SHA256.HashData(Encoding.UTF8.GetBytes(scenario == "rp" ? "other.example.com" : "localhost"));
        byte flags = scenario == "backupFlags" ? (byte)0x55 : (byte)0x45;
        byte[] authenticatorData =
            [.. rpHash, flags, 0, 0, 0, 0, .. new byte[16], 0, 4, .. credentialId, .. EncodePublicKey(key)];
        CborWriter writer = new();
        writer.WriteStartMap(3);
        writer.WriteTextString("fmt");
        writer.WriteTextString("none");
        writer.WriteTextString("attStmt");
        writer.WriteStartMap(scenario == "attStmt" ? 1 : 0);
        if (scenario == "attStmt")
        {
            writer.WriteTextString("unexpected");
            writer.WriteTextString("value");
        }
        writer.WriteEndMap();
        writer.WriteTextString("authData");
        writer.WriteByteString(authenticatorData);
        writer.WriteEndMap();

        string json = JsonSerializer.Serialize(new
        {
            id = Base64Url(credentialId),
            rawId = Base64Url(credentialId),
            type = "public-key",
            response = new
            {
                clientDataJSON = Base64Url(clientData),
                attestationObject = Base64Url(writer.Encode())
            },
            clientExtensionResults = new { }
        });
        return JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(json)!;
    }

    private static AuthenticatorAssertionRawResponse CreateAssertion(
        AssertionOptions options, UserPasskeyCredential credential, ECDsa key, string? scenario = null)
    {
        byte[] clientData = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.get",
            challenge = Base64Url(scenario == "challenge" ? new byte[32] : options.Challenge),
            origin = scenario == "origin" ? "https://other.example.com" : "http://localhost:3000",
            crossOrigin = scenario == "crossOrigin"
        });
        byte[] authenticatorData = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(scenario == "rp" ? "other.example.com" : "localhost"))
            .CopyTo(authenticatorData, 0);
        authenticatorData[32] = scenario == "backupFlags" ? (byte)0x15 : (byte)0x05;
        authenticatorData[36] = 1;
        byte[] signedData = [.. authenticatorData, .. SHA256.HashData(clientData)];
        byte[] signature = key.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (scenario == "signature")
        {
            signature[^1] ^= 1;
        }

        string json = JsonSerializer.Serialize(new
        {
            id = Base64Url(credential.CredentialId),
            rawId = Base64Url(credential.CredentialId),
            type = "public-key",
            response = new
            {
                authenticatorData = Base64Url(authenticatorData),
                clientDataJSON = Base64Url(clientData),
                signature = Base64Url(signature),
                userHandle = Base64Url(credential.UserId.ToByteArray())
            },
            clientExtensionResults = new { }
        });
        return JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(json)!;
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
