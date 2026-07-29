using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Ai;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Ai;
using WinDayFlow.Infrastructure.Persistence;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Ai;

public sealed class SqliteAiProviderProfileStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 3, 4, 5, TimeSpan.Zero);

    private static readonly Guid ProfileId =
        Guid.Parse("72c1d90a-361e-42fa-bf7b-27e319d9c532");

    [Fact]
    public async Task VersionFiveUpgradePreservesDataAndForcesCloudAnalysisOff()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP INDEX ix_analysis_jobs_provider_revision_state;
                DROP TABLE ai_provider_profiles;
                DELETE FROM schema_migrations WHERE version = 6;
                UPDATE app_settings
                SET theme = 2,
                    cloud_analysis_enabled = 1,
                    evidence_retention_days = 90
                WHERE id = 1;
                INSERT INTO capture_chunks(
                    id,
                    manifest_relative_path,
                    start_utc_ticks,
                    start_offset_minutes,
                    end_utc_ticks,
                    end_offset_minutes,
                    captured_frame_count,
                    frame_count,
                    frame_width,
                    frame_height,
                    frame_byte_count,
                    persistence_generation_hex,
                    target_epoch_hex,
                    committed_at_utc_ticks,
                    ingested_at_utc_ticks,
                    availability)
                VALUES (
                    'migration-chunk',
                    'chunks/migration-chunk/manifest.json',
                    100,
                    0,
                    200,
                    0,
                    1,
                    1,
                    2,
                    2,
                    4,
                    '0000000000000001',
                    '0000000000000001',
                    200,
                    201,
                    0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        await using var migrated = await factory.OpenConnectionAsync();
        await using var verify = migrated.CreateCommand();
        verify.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 6),
                (SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'table' AND name = 'ai_provider_profiles'),
                (SELECT theme FROM app_settings WHERE id = 1),
                (SELECT cloud_analysis_enabled FROM app_settings WHERE id = 1),
                (SELECT evidence_retention_days FROM app_settings WHERE id = 1),
                (SELECT COUNT(*) FROM capture_chunks WHERE id = 'migration-chunk');
            """;
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(90, reader.GetInt32(4));
        Assert.Equal(1, reader.GetInt32(5));
    }

    [Fact]
    public async Task DpapiRoundTripSupportsNoOpAndRevisionReprotection()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        const string apiKey = "sk-roundtrip-secret-value";
        var profile = CreateProfile();

        var created = await store.SaveActiveAsync(
            profile,
            expectedRevision: null,
            AiProviderCredentialUpdate.Replace(apiKey),
            Now);
        var validated = await store.MarkValidatedAsync(
            profile.Id,
            created.Revision,
            Now.AddMinutes(1));
        Assert.NotNull(validated);

        var noOp = await store.SaveActiveAsync(
            profile,
            validated.Revision,
            AiProviderCredentialUpdate.Preserve,
            Now.AddMinutes(2));
        Assert.Equal(validated, noOp);

        var changedProfile = CreateProfile(displayName: "Updated provider");
        var changed = await store.SaveActiveAsync(
            changedProfile,
            noOp.Revision,
            AiProviderCredentialUpdate.Preserve,
            Now.AddMinutes(3));
        Assert.Equal(2, changed.Revision);
        Assert.True(changed.HasApiKey);
        Assert.Null(changed.ValidatedRevision);
        Assert.Null(changed.ValidatedAtUtc);

        var handler = new CredentialObservingHandler();
        var providerFactory = new OpenAiCompatibleProviderFactory(
            store,
            () => handler,
            TimeProvider.System);
        using var provider = Assert.IsType<OpenAiCompatibleProvider>(
            await providerFactory.CreateAsync(changed));
        var failure = await Assert.ThrowsAsync<AiProviderException>(
            () => provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal(AiProviderErrorCode.ProviderUnavailable, failure.ErrorCode);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(apiKey, handler.Authorization?.Parameter);
    }

    [Fact]
    public async Task ReplacementUsesFreshSaltAndDatabaseNeverContainsPlaintext()
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        const string apiKey = "sk-plaintext-sentinel-4e5bd7bb";
        var profile = CreateProfile();

        var first = await store.SaveActiveAsync(
            profile,
            expectedRevision: null,
            AiProviderCredentialUpdate.Replace(apiKey),
            Now);
        var firstCredential = await ReadCredentialColumnsAsync(factory);
        var second = await store.SaveActiveAsync(
            profile,
            first.Revision,
            AiProviderCredentialUpdate.Replace(apiKey),
            Now.AddMinutes(1));
        var secondCredential = await ReadCredentialColumnsAsync(factory);

        Assert.Equal(2, second.Revision);
        Assert.False(firstCredential.Salt.SequenceEqual(secondCredential.Salt));
        Assert.False(firstCredential.Ciphertext.SequenceEqual(secondCredential.Ciphertext));
        Assert.Equal(1, firstCredential.ProtectionVersion);
        Assert.Equal(1, secondCredential.ProtectionVersion);
        var plaintext = Encoding.UTF8.GetBytes(apiKey);
        Assert.Equal(-1, firstCredential.Ciphertext.AsSpan().IndexOf(plaintext));
        Assert.Equal(-1, secondCredential.Ciphertext.AsSpan().IndexOf(plaintext));
        Assert.Equal(-1, File.ReadAllBytes(database.DatabasePath).AsSpan().IndexOf(plaintext));
    }

    [Fact]
    public async Task TamperedCiphertextCannotBeDecrypted()
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        var snapshot = await store.SaveActiveAsync(
            CreateProfile(),
            expectedRevision: null,
            AiProviderCredentialUpdate.Replace("sk-tamper-test"),
            Now);
        var credential = await ReadCredentialColumnsAsync(factory);
        credential.Ciphertext[credential.Ciphertext.Length / 2] ^= 0x5a;
        await UpdateCredentialCiphertextAsync(factory, credential.Ciphertext);

        var providerFactory = new OpenAiCompatibleProviderFactory(store);
        var failure = await Assert.ThrowsAsync<AiProviderException>(
            () => providerFactory.CreateAsync(snapshot));
        Assert.Equal(AiProviderErrorCode.InvalidConfiguration, failure.ErrorCode);
        Assert.False(failure.IsRetryable);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("endpoint")]
    public async Task CredentialIsBoundToPersistedProfileMetadata(string mutation)
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        await store.SaveActiveAsync(
            CreateProfile(),
            expectedRevision: null,
            AiProviderCredentialUpdate.Replace("sk-profile-binding-test"),
            Now);

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = mutation == "profile"
                ? "UPDATE ai_provider_profiles SET id = $value WHERE is_active = 1;"
                : "UPDATE ai_provider_profiles SET base_endpoint = $value WHERE is_active = 1;";
            command.Parameters.AddWithValue(
                "$value",
                mutation == "profile"
                    ? "199675f1-a947-49e7-b0db-64273a1da387"
                    : "https://attacker.example/v1/");
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var mutated = await store.GetActiveAsync();
        Assert.NotNull(mutated);
        var failure = await Assert.ThrowsAsync<AiProviderException>(
            () => new OpenAiCompatibleProviderFactory(store).CreateAsync(mutated));
        Assert.Equal(AiProviderErrorCode.InvalidConfiguration, failure.ErrorCode);
        Assert.False(failure.IsRetryable);
    }

    [Fact]
    public async Task ConcurrentSavesHaveOneRevisionWinner()
    {
        using var database = new TemporaryDatabase();
        var (factory, firstStore) = await CreateStoreAsync(database);
        var initial = await firstStore.SaveActiveAsync(
            CreateProfile(new Uri("http://127.0.0.1:11434/v1")),
            expectedRevision: null,
            AiProviderCredentialUpdate.Preserve,
            Now);
        var secondStore = new SqliteAiProviderProfileStore(
            factory,
            new WindowsDpapiCredentialProtector());

        var attempts = await Task.WhenAll(
            TrySaveAsync(
                firstStore,
                CreateProfile(
                    new Uri("http://127.0.0.1:11434/v1"),
                    displayName: "First writer"),
                initial.Revision),
            TrySaveAsync(
                secondStore,
                CreateProfile(
                    new Uri("http://127.0.0.1:11434/v1"),
                    displayName: "Second writer"),
                initial.Revision));

        var winner = Assert.Single(attempts, static attempt => attempt.Snapshot is not null);
        var conflict = Assert.Single(attempts, static attempt => attempt.Exception is not null);
        Assert.Equal(2, winner.Snapshot!.Revision);
        Assert.IsType<AiProviderConfigurationConflictException>(conflict.Exception);
        Assert.Equal(2, (await firstStore.GetActiveAsync())?.Revision);
    }

    [Fact]
    public async Task ValidationUsesRevisionCompareAndSwapAndConfigurationClearsIt()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var profile = CreateProfile(new Uri("http://127.0.0.1:11434/v1"));
        var first = await store.SaveActiveAsync(
            profile,
            expectedRevision: null,
            AiProviderCredentialUpdate.Preserve,
            Now);
        var validated = await store.MarkValidatedAsync(
            profile.Id,
            first.Revision,
            Now.AddMinutes(1));
        Assert.NotNull(validated);
        Assert.Equal(first.Revision, validated.ValidatedRevision);
        Assert.Equal(Now.AddMinutes(1), validated.ValidatedAtUtc);

        var second = await store.SaveActiveAsync(
            CreateProfile(
                new Uri("http://127.0.0.1:11434/v1"),
                model: "new-model"),
            first.Revision,
            AiProviderCredentialUpdate.Preserve,
            Now.AddMinutes(2));
        Assert.Equal(2, second.Revision);
        Assert.False(second.IsValidated);
        Assert.Null(await store.MarkValidatedAsync(
            profile.Id,
            expectedRevision: 1,
            Now.AddMinutes(3)));

        var current = await store.MarkValidatedAsync(
            profile.Id,
            second.Revision,
            Now.AddMinutes(4));
        Assert.NotNull(current);
        Assert.True(current.IsValidated);
        Assert.Equal(Now.AddMinutes(4), current.ValidatedAtUtc);
    }

    [Fact]
    public async Task FactoryRejectsSnapshotAfterActiveRevisionChanges()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var first = await store.SaveActiveAsync(
            CreateProfile(),
            expectedRevision: null,
            AiProviderCredentialUpdate.Replace("sk-stale-revision"),
            Now);
        var second = await store.SaveActiveAsync(
            CreateProfile(model: "new-model"),
            first.Revision,
            AiProviderCredentialUpdate.Preserve,
            Now.AddMinutes(1));

        Assert.Equal(2, second.Revision);
        await Assert.ThrowsAsync<AiProviderConfigurationConflictException>(
            () => new OpenAiCompatibleProviderFactory(store).CreateAsync(first));
    }

    [Fact]
    public async Task CorruptPersistedProfileIsReportedAsInvalidData()
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        await store.SaveActiveAsync(
            CreateProfile(new Uri("http://127.0.0.1:11434/v1")),
            expectedRevision: null,
            AiProviderCredentialUpdate.Preserve,
            Now);

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                UPDATE ai_provider_profiles
                SET request_timeout_ticks = 1
                WHERE is_active = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetActiveAsync());
    }

    [Fact]
    public async Task SchemaAllowsManyProfilesButOnlyOneActiveProfile()
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        await store.SaveActiveAsync(
            CreateProfile(new Uri("http://127.0.0.1:11434/v1")),
            expectedRevision: null,
            AiProviderCredentialUpdate.Preserve,
            Now);

        await using var connection = await factory.OpenConnectionAsync();
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO ai_provider_profiles(
                    id, display_name, kind, base_endpoint, model,
                    request_timeout_ticks, revision, is_active,
                    api_key_ciphertext, api_key_salt, api_key_protection_version,
                    validated_revision, validated_at_utc_ticks,
                    created_at_utc_ticks, updated_at_utc_ticks)
                SELECT
                    '0a5516a7-018a-42c0-9bca-9404ca9d8a51',
                    'Inactive provider', kind, base_endpoint, model,
                    request_timeout_ticks, revision, 0,
                    NULL, NULL, NULL, NULL, NULL,
                    created_at_utc_ticks, updated_at_utc_ticks
                FROM ai_provider_profiles
                WHERE is_active = 1;
                """;
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = """
                SELECT COUNT(*), SUM(is_active)
                FROM ai_provider_profiles;
                """;
            await using var reader = await count.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
        }

        await using var activate = connection.CreateCommand();
        activate.CommandText = """
            UPDATE ai_provider_profiles
            SET is_active = 1
            WHERE id = '0a5516a7-018a-42c0-9bca-9404ca9d8a51';
            """;
        await Assert.ThrowsAsync<SqliteException>(() => activate.ExecuteNonQueryAsync());
    }

    private static async Task<(SqliteConnectionFactory Factory, SqliteAiProviderProfileStore Store)>
        CreateStoreAsync(TemporaryDatabase database)
    {
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        return (
            factory,
            new SqliteAiProviderProfileStore(
                factory,
                new WindowsDpapiCredentialProtector()));
    }

    private static AiProviderProfile CreateProfile(
        Uri? endpoint = null,
        string displayName = "Primary provider",
        string model = "vision-model")
    {
        return new AiProviderProfile(
            ProfileId,
            displayName,
            AiProviderKind.OpenAiCompatible,
            endpoint ?? new Uri("https://api.example.com/v1"),
            model,
            TimeSpan.FromMilliseconds(12_345));
    }

    private static async Task<SaveAttempt> TrySaveAsync(
        SqliteAiProviderProfileStore store,
        AiProviderProfile profile,
        long expectedRevision)
    {
        try
        {
            return new SaveAttempt(
                await store.SaveActiveAsync(
                    profile,
                    expectedRevision,
                    AiProviderCredentialUpdate.Preserve,
                    Now.AddMinutes(1)),
                Exception: null);
        }
        catch (Exception exception)
        {
            return new SaveAttempt(Snapshot: null, exception);
        }
    }

    private static async Task<CredentialColumns> ReadCredentialColumnsAsync(
        SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                api_key_ciphertext,
                api_key_salt,
                api_key_protection_version
            FROM ai_provider_profiles
            WHERE is_active = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new CredentialColumns(
            reader.GetFieldValue<byte[]>(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetInt32(2));
    }

    private static async Task UpdateCredentialCiphertextAsync(
        SqliteConnectionFactory factory,
        byte[] ciphertext)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_provider_profiles
            SET api_key_ciphertext = $ciphertext
            WHERE is_active = 1;
            """;
        command.Parameters.Add("$ciphertext", SqliteType.Blob).Value = ciphertext;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static AiAnalysisRequest CreateRequest()
    {
        var range = new TimeRange(Now, Now.AddSeconds(1));
        return new AiAnalysisRequest(
            Guid.Parse("0566f63b-8880-4730-a539-d7fe5b63216c"),
            Guid.Parse("16fc6ff3-b1dd-4c30-a219-5980fd88bcb5"),
            attempt: 1,
            "factory-test-chunk",
            "chunks/factory-test/frame.jpg",
            range,
            "factory-test-v1",
            AiAnalysisContract.CurrentSchemaVersion,
            "en-US",
            [new AiEvidenceImage(
                "factory-test-frame",
                Now,
                new byte[] { 0xff, 0xd8, 0xff, 0xd9 })],
            context: []);
    }

    private sealed record CredentialColumns(
        byte[] Ciphertext,
        byte[] Salt,
        int ProtectionVersion);

    private sealed record SaveAttempt(
        AiProviderProfileSnapshot? Snapshot,
        Exception? Exception);

    private sealed class CredentialObservingHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.InternalServerError));
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.AiProviderProfile.Tests",
            Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(_root, "windayflow.db");

        public void Dispose()
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
