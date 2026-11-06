using System.Security.Cryptography;
using Farm.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Farm.Infrastructure.Tests;

public sealed class ObicoRollbackPreflightTests
{
    [Fact]
    public void VerifyAssemblyHash_AcceptsExactHashAndRejectsChangedBytes()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "rollback artifact");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            ObicoRollbackPreflight.Program.VerifyAssemblyHash(path, hash.ToLowerInvariant());
            File.AppendAllText(path, "modified");
            Assert.Throws<InvalidOperationException>(() => ObicoRollbackPreflight.Program.VerifyAssemblyHash(path, hash));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void VerifyAssemblyHash_RejectsInvalidTrustedHash(string hash)
    {
        Assert.Throws<InvalidOperationException>(() => ObicoRollbackPreflight.Program.VerifyAssemblyHash("unused", hash));
    }

    [Theory]
    [InlineData(0, "{}")]
    [InlineData(1, null)]
    [InlineData(1, "")]
    [InlineData(1, "  ")]
    [InlineData(2, "{}")]
    public async Task ReadRestoredSettingsAsync_RejectsMissingEmptyOrDuplicateRows(int rows, string? json)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await CreateRowsAsync(connection, rows, json);
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ObicoRollbackPreflight.Program.ReadRestoredSettingsAsync(connection, transaction, "sqlite"));
    }

    [Fact]
    public async Task ReadRestoredSettingsAsync_SelectsOnlyObicoAndLeavesRowsUnchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        const string json = "{\"enabled\":false}";
        await CreateRowsAsync(connection, 1, json);
        await using (var unrelated = connection.CreateCommand())
        {
            unrelated.CommandText = "INSERT INTO AppSettingsEntities VALUES ('Other', 'not Obico')";
            await unrelated.ExecuteNonQueryAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.Equal(json, await ObicoRollbackPreflight.Program.ReadRestoredSettingsAsync(connection, transaction, "sqlite"));
            await transaction.RollbackAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SettingsJson FROM AppSettingsEntities WHERE Key = 'Obico'";
        Assert.Equal(json, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM AppSettingsEntities";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public void ValidateWithOldAssembly_InvokesSelectedAssemblyValidator()
    {
        // This tests the loader contract, not deployed-version compatibility.
        string assembly = typeof(ObicoSettings).Assembly.Location;
        ObicoRollbackPreflight.Program.ValidateWithOldAssembly(assembly, "{\"enabled\":false}");
        Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            ObicoRollbackPreflight.Program.ValidateWithOldAssembly(assembly, "{\"enabled\":true,\"obicoApiUrl\":\"https://example.invalid\",\"confidenceThreshold\":-1}"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    public void ValidateWithOldAssembly_RejectsNullOrMalformedSettings(string json)
    {
        Assert.ThrowsAny<Exception>(() =>
            ObicoRollbackPreflight.Program.ValidateWithOldAssembly(typeof(ObicoSettings).Assembly.Location, json));
    }

    private static async Task CreateRowsAsync(SqliteConnection connection, int rows, string? json)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE AppSettingsEntities (Key TEXT, SettingsJson TEXT)";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO AppSettingsEntities VALUES ('Obico', @json)";
        command.Parameters.AddWithValue("@json", (object?)json ?? DBNull.Value);
        for (int index = 0; index < rows; index++)
        {
            await command.ExecuteNonQueryAsync();
        }
    }
}
