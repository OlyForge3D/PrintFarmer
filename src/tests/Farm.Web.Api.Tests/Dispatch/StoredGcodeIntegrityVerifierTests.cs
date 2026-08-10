// <copyright file="StoredGcodeIntegrityVerifierTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Security.Cryptography;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.StorageManagement;
using FluentAssertions;
using Moq;

namespace Farm.Web.Api.Tests.Dispatch;

public sealed class StoredGcodeIntegrityVerifierTests : IDisposable
{
    private readonly string _storageRoot = Path.Join(
        Directory.GetCurrentDirectory(),
        ".test-artifacts",
        $"printfarmer-integrity-{Guid.NewGuid():N}");

    public StoredGcodeIntegrityVerifierTests()
    {
        Directory.CreateDirectory(_storageRoot);
    }

    public void Dispose()
    {
        Directory.Delete(_storageRoot, recursive: true);
    }

    [Fact]
    public async Task VerifyAsync_BytesChangedAfterQueue_ReturnsHashMismatch()
    {
        const string FileName = "calibration.gcode";
        string path = Path.Join(_storageRoot, FileName);
        byte[] original = "G28\n"u8.ToArray();
        await File.WriteAllBytesAsync(path, original);
        string expected = Convert.ToHexString(SHA256.HashData(original));
        await File.WriteAllTextAsync(path, "G28\nM112\n");

        Mock<IStoragePathService> storage = new();
        storage.Setup(paths => paths.GetGcodeStorageDirectory()).Returns(_storageRoot);
        var verifier = new StoredGcodeIntegrityVerifier(storage.Object);
        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = FileName,
            FileName = FileName,
            FilePath = string.Empty,
            FileSizeBytes = original.Length,
        };

        StoredGcodeIntegrityResult result = await verifier.VerifyAsync(
            file,
            expected,
            expectedSizeBytes: null,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("gcode_byte_hash_mismatch");
    }

    [Fact]
    public async Task VerifyOpenedStreamAsync_VerifiesAndRewindsTheUploadStream()
    {
        byte[] bytes = "G28\nM109 S200\n"u8.ToArray();
        string expected = Convert.ToHexString(SHA256.HashData(bytes));
        await using var stream = new MemoryStream(bytes);
        stream.Position = 3;

        StoredGcodeIntegrityResult result =
            await StoredGcodeIntegrityVerifier.VerifyOpenedStreamAsync(
                stream,
                expected,
                bytes.Length,
                CancellationToken.None);

        result.Success.Should().BeTrue();
        stream.Position.Should().Be(0, "the same verified stream is uploaded next");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        copy.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task VerifyOpenedStreamAsync_TamperedUploadStreamFails()
    {
        byte[] expectedBytes = "G28\n"u8.ToArray();
        byte[] tamperedBytes = "M112\n"u8.ToArray();
        await using var stream = new MemoryStream(tamperedBytes);

        StoredGcodeIntegrityResult result =
            await StoredGcodeIntegrityVerifier.VerifyOpenedStreamAsync(
                stream,
                Convert.ToHexString(SHA256.HashData(expectedBytes)),
                expectedSizeBytes: null,
                CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("gcode_byte_hash_mismatch");
        stream.Position.Should().Be(0);
    }
}
