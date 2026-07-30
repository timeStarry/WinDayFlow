using System.Globalization;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Ai;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Ai;

public sealed class SqliteAiProviderProfileStore : IAiProviderProfileStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IAiProviderCredentialProtector _credentialProtector;

    public SqliteAiProviderProfileStore(SqliteConnectionFactory connectionFactory)
        : this(connectionFactory, new WindowsDpapiCredentialProtector())
    {
    }

    public SqliteAiProviderProfileStore(
        SqliteConnectionFactory connectionFactory,
        WindowsDpapiCredentialProtector credentialProtector)
        : this(connectionFactory, (IAiProviderCredentialProtector)credentialProtector)
    {
    }

    internal SqliteAiProviderProfileStore(
        SqliteConnectionFactory connectionFactory,
        IAiProviderCredentialProtector credentialProtector)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
        _credentialProtector = credentialProtector
            ?? throw new ArgumentNullException(nameof(credentialProtector));
    }

    public async Task<IReadOnlyList<AiProviderProfileSnapshot>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ProfileSelectSql + " ORDER BY lower(display_name), id;";
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var profiles = new List<AiProviderProfileSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(MaterializePersistedProfile(reader).Snapshot);
        }

        return profiles;
    }

    public async Task<AiProviderProfileSnapshot?> GetAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ValidateProfileId(profileId);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return (await ReadByIdAsync(
                connection,
                transaction: null,
                profileId,
                cancellationToken)
            .ConfigureAwait(false))?.Snapshot;
    }

    public async Task<AiProviderProfileSnapshot> CreateAsync(
        AiProviderProfile profile,
        AiProviderCredentialUpdate credentialUpdate,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentialUpdate);
        cancellationToken.ThrowIfCancellationRequested();
        const long revision = 1;
        ProtectedAiProviderCredential? protectedCredential = credentialUpdate.Kind switch
        {
            AiProviderCredentialUpdateKind.Preserve => (ProtectedAiProviderCredential?)null,
            AiProviderCredentialUpdateKind.Replace => _credentialProtector.Protect(
                credentialUpdate.GetReplacement(),
                profile.Id,
                revision,
                profile.BaseEndpoint.AbsoluteUri),
            AiProviderCredentialUpdateKind.Clear => (ProtectedAiProviderCredential?)null,
            _ => throw new ArgumentOutOfRangeException(nameof(credentialUpdate)),
        };
        var changedAtTicks = ToUtcTicks(changedAtUtc);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = InsertProfileSql;
        AddProfileParameters(command.Parameters, profile);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$is_active", 0);
        AddCredentialParameters(command.Parameters, protectedCredential);
        command.Parameters.AddWithValue("$created_at_utc_ticks", changedAtTicks);
        try
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (
            exception.SqliteExtendedErrorCode is 1555 or 2067)
        {
            throw new AiProviderConfigurationConflictException();
        }

        return new AiProviderProfileSnapshot(
            profile,
            revision,
            protectedCredential.HasValue,
            validatedRevision: null,
            validatedAtUtc: null);
    }

    public async Task<AiProviderProfileSnapshot> UpdateAsync(
        AiProviderProfile profile,
        long expectedRevision,
        AiProviderCredentialUpdate credentialUpdate,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentialUpdate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByIdAsync(
                connection,
                transaction,
                profile.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.Snapshot.Revision != expectedRevision)
        {
            throw new AiProviderConfigurationConflictException();
        }

        if (profile == current.Snapshot.Profile
            && credentialUpdate.Kind == AiProviderCredentialUpdateKind.Preserve)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current.Snapshot;
        }

        var nextRevision = checked(expectedRevision + 1);
        var protectedCredential = CreateUpdatedCredential(
            current,
            credentialUpdate,
            profile,
            nextRevision);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ai_provider_profiles
            SET display_name = $display_name,
                kind = $kind,
                base_endpoint = $base_endpoint,
                model = $model,
                request_timeout_ticks = $request_timeout_ticks,
                revision = $next_revision,
                api_key_ciphertext = $api_key_ciphertext,
                api_key_salt = $api_key_salt,
                api_key_protection_version = $api_key_protection_version,
                validated_revision = NULL,
                validated_at_utc_ticks = NULL,
                updated_at_utc_ticks = $updated_at_utc_ticks
            WHERE id = $id AND revision = $expected_revision;
            """;
        AddProfileParameters(command.Parameters, profile);
        command.Parameters.AddWithValue("$next_revision", nextRevision);
        command.Parameters.AddWithValue("$expected_revision", expectedRevision);
        AddCredentialParameters(command.Parameters, protectedCredential);
        command.Parameters.AddWithValue(
            "$updated_at_utc_ticks",
            Math.Max(ToUtcTicks(changedAtUtc), current.UpdatedAtUtcTicks));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new AiProviderConfigurationConflictException();
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AiProviderProfileSnapshot(
            profile,
            nextRevision,
            protectedCredential.HasValue,
            validatedRevision: null,
            validatedAtUtc: null);
    }

    public async Task DeleteAsync(
        Guid profileId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateProfileId(profileId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM ai_provider_profiles
            WHERE id = $id AND revision = $expected_revision;
            """;
        command.Parameters.AddWithValue("$id", FormatId(profileId));
        command.Parameters.AddWithValue("$expected_revision", expectedRevision);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new AiProviderConfigurationConflictException();
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new AiProviderProfileInUseException(profileId);
        }
    }

    internal async Task<string?> ReadApiKeyAsync(
        AiProviderProfileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        // Schema v6 deliberately retains only the active revision's credential.
        // Jobs pinned to older revisions must be terminally resolved by the worker.
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var persisted = await ReadByIdAsync(
                connection,
                transaction: null,
                snapshot.Profile.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (persisted is null
            || persisted.Snapshot.Profile != snapshot.Profile
            || persisted.Snapshot.Revision != snapshot.Revision
            || persisted.Snapshot.HasApiKey != snapshot.HasApiKey)
        {
            throw new AiProviderConfigurationConflictException();
        }

        if (persisted.Ciphertext is null || persisted.Salt is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _credentialProtector.Unprotect(
            persisted.Ciphertext,
            persisted.Salt,
            persisted.CredentialProtectionVersion
                ?? throw new InvalidDataException(
                    "The persisted AI provider credential has no protection version."),
            snapshot.Profile.Id,
            snapshot.Revision,
            snapshot.Profile.BaseEndpoint.AbsoluteUri);
    }

    private async Task<AiProviderProfileSnapshot> InsertFirstActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AiProviderProfile profile,
        AiProviderCredentialUpdate credentialUpdate,
        long createdAtTicks,
        CancellationToken cancellationToken)
    {
        const long revision = 1;
        ProtectedAiProviderCredential? protectedCredential = credentialUpdate.Kind switch
        {
            AiProviderCredentialUpdateKind.Preserve => null,
            AiProviderCredentialUpdateKind.Replace => _credentialProtector.Protect(
                credentialUpdate.GetReplacement(),
                profile.Id,
                revision,
                profile.BaseEndpoint.AbsoluteUri),
            AiProviderCredentialUpdateKind.Clear => null,
            _ => throw new ArgumentOutOfRangeException(nameof(credentialUpdate)),
        };

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ai_provider_profiles(
                id,
                display_name,
                kind,
                base_endpoint,
                model,
                request_timeout_ticks,
                revision,
                is_active,
                api_key_ciphertext,
                api_key_salt,
                api_key_protection_version,
                validated_revision,
                validated_at_utc_ticks,
                created_at_utc_ticks,
                updated_at_utc_ticks)
            VALUES (
                $id,
                $display_name,
                $kind,
                $base_endpoint,
                $model,
                $request_timeout_ticks,
                $revision,
                1,
                $api_key_ciphertext,
                $api_key_salt,
                $api_key_protection_version,
                NULL,
                NULL,
                $created_at_utc_ticks,
                $created_at_utc_ticks);
            """;
        AddProfileParameters(command.Parameters, profile);
        command.Parameters.AddWithValue("$revision", revision);
        AddCredentialParameters(command.Parameters, protectedCredential);
        command.Parameters.AddWithValue("$created_at_utc_ticks", createdAtTicks);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (
            exception.SqliteExtendedErrorCode is 1555 or 2067)
        {
            throw new AiProviderConfigurationConflictException();
        }

        return new AiProviderProfileSnapshot(
            profile,
            revision,
            protectedCredential.HasValue,
            validatedRevision: null,
            validatedAtUtc: null);
    }

    private ProtectedAiProviderCredential? CreateUpdatedCredential(
        PersistedProfile current,
        AiProviderCredentialUpdate credentialUpdate,
        AiProviderProfile profile,
        long nextRevision)
    {
        return credentialUpdate.Kind switch
        {
            AiProviderCredentialUpdateKind.Preserve => ReprotectCurrentCredential(
                current,
                profile,
                nextRevision),
            AiProviderCredentialUpdateKind.Replace => _credentialProtector.Protect(
                credentialUpdate.GetReplacement(),
                profile.Id,
                nextRevision,
                profile.BaseEndpoint.AbsoluteUri),
            AiProviderCredentialUpdateKind.Clear => null,
            _ => throw new ArgumentOutOfRangeException(nameof(credentialUpdate)),
        };
    }

    private ProtectedAiProviderCredential? ReprotectCurrentCredential(
        PersistedProfile current,
        AiProviderProfile profile,
        long nextRevision)
    {
        if (current.Ciphertext is null || current.Salt is null)
        {
            return null;
        }

        var apiKey = _credentialProtector.Unprotect(
            current.Ciphertext,
            current.Salt,
            current.CredentialProtectionVersion
                ?? throw new InvalidDataException(
                    "The persisted AI provider credential has no protection version."),
            current.Snapshot.Profile.Id,
            current.Snapshot.Revision,
            current.Snapshot.Profile.BaseEndpoint.AbsoluteUri);
        return _credentialProtector.Protect(
            apiKey,
            profile.Id,
            nextRevision,
            profile.BaseEndpoint.AbsoluteUri);
    }

    private static async Task<PersistedProfile?> ReadActiveAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                display_name,
                kind,
                base_endpoint,
                model,
                request_timeout_ticks,
                revision,
                api_key_ciphertext,
                api_key_salt,
                api_key_protection_version,
                validated_revision,
                validated_at_utc_ticks,
                created_at_utc_ticks,
                updated_at_utc_ticks
            FROM ai_provider_profiles
            WHERE is_active = 1;
            """;
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return MaterializePersistedProfile(reader);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FormatException
                or InvalidCastException
                or OverflowException)
        {
            throw new InvalidDataException(
                "The persisted AI provider profile is invalid.",
                exception);
        }
    }

    private static async Task<PersistedProfile?> ReadByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ProfileSelectSql + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FormatId(profileId));
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return MaterializePersistedProfile(reader);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FormatException
                or InvalidCastException
                or OverflowException)
        {
            throw new InvalidDataException(
                "The persisted AI provider profile is invalid.",
                exception);
        }
    }

    private static PersistedProfile MaterializePersistedProfile(SqliteDataReader reader)
    {
        var ciphertext = reader.IsDBNull(7)
            ? null
            : reader.GetFieldValue<byte[]>(7);
        var salt = reader.IsDBNull(8)
            ? null
            : reader.GetFieldValue<byte[]>(8);
        int? credentialProtectionVersion = reader.IsDBNull(9)
            ? null
            : reader.GetInt32(9);
        if ((ciphertext is null) != (salt is null)
            || (ciphertext is null) != !credentialProtectionVersion.HasValue)
        {
            throw new InvalidDataException(
                "The persisted AI provider credential is incomplete.");
        }

        if (ciphertext is not null
            && (ciphertext.Length is 0 or > WindowsDpapiCredentialProtector.MaximumCiphertextLength
                || salt!.Length != WindowsDpapiCredentialProtector.SaltLength
                || credentialProtectionVersion
                    != WindowsDpapiCredentialProtector.CurrentProtectionVersion))
        {
            throw new InvalidDataException(
                "The persisted AI provider credential metadata is invalid.");
        }

        var idText = reader.GetString(0);
        if (!Guid.TryParseExact(idText, "D", out var profileId)
            || !string.Equals(idText, FormatId(profileId), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The persisted AI provider identifier is not canonical.");
        }

        var endpointText = reader.GetString(3);
        var profile = new AiProviderProfile(
            profileId,
            reader.GetString(1),
            (AiProviderKind)reader.GetInt32(2),
            new Uri(endpointText, UriKind.Absolute),
            reader.GetString(4),
            TimeSpan.FromTicks(reader.GetInt64(5)));
        if (!string.Equals(
                endpointText,
                profile.BaseEndpoint.AbsoluteUri,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The persisted AI provider endpoint is not canonical.");
        }

        long? validatedRevision = reader.IsDBNull(10)
            ? null
            : reader.GetInt64(10);
        DateTimeOffset? validatedAt = reader.IsDBNull(11)
            ? null
            : ReadUtcTimestamp(reader.GetInt64(11));
        var snapshot = new AiProviderProfileSnapshot(
            profile,
            reader.GetInt64(6),
            ciphertext is not null,
            validatedRevision,
            validatedAt);
        var createdAtUtcTicks = reader.GetInt64(12);
        var updatedAtUtcTicks = reader.GetInt64(13);
        _ = ReadUtcTimestamp(createdAtUtcTicks);
        _ = ReadUtcTimestamp(updatedAtUtcTicks);
        if (updatedAtUtcTicks < createdAtUtcTicks)
        {
            throw new InvalidDataException(
                "The persisted AI provider timestamps are inconsistent.");
        }

        return new PersistedProfile(
            snapshot,
            ciphertext,
            salt,
            credentialProtectionVersion,
            createdAtUtcTicks,
            updatedAtUtcTicks);
    }

    private static void AddProfileParameters(
        SqliteParameterCollection parameters,
        AiProviderProfile profile)
    {
        parameters.AddWithValue("$id", FormatId(profile.Id));
        parameters.AddWithValue("$display_name", profile.DisplayName);
        parameters.AddWithValue("$kind", (int)profile.Kind);
        parameters.AddWithValue("$base_endpoint", profile.BaseEndpoint.AbsoluteUri);
        parameters.AddWithValue("$model", profile.Model);
        parameters.AddWithValue("$request_timeout_ticks", profile.RequestTimeout.Ticks);
    }

    private static void ValidateProfileId(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException(
                "An AI provider profile identifier cannot be empty.",
                nameof(profileId));
        }
    }

    private const string ProfileSelectSql = """
        SELECT
            id,
            display_name,
            kind,
            base_endpoint,
            model,
            request_timeout_ticks,
            revision,
            api_key_ciphertext,
            api_key_salt,
            api_key_protection_version,
            validated_revision,
            validated_at_utc_ticks,
            created_at_utc_ticks,
            updated_at_utc_ticks
        FROM ai_provider_profiles
        """;

    private const string InsertProfileSql = """
        INSERT INTO ai_provider_profiles(
            id,
            display_name,
            kind,
            base_endpoint,
            model,
            request_timeout_ticks,
            revision,
            is_active,
            api_key_ciphertext,
            api_key_salt,
            api_key_protection_version,
            validated_revision,
            validated_at_utc_ticks,
            created_at_utc_ticks,
            updated_at_utc_ticks)
        VALUES (
            $id,
            $display_name,
            $kind,
            $base_endpoint,
            $model,
            $request_timeout_ticks,
            $revision,
            $is_active,
            $api_key_ciphertext,
            $api_key_salt,
            $api_key_protection_version,
            NULL,
            NULL,
            $created_at_utc_ticks,
            $created_at_utc_ticks);
        """;

    private static void AddCredentialParameters(
        SqliteParameterCollection parameters,
        ProtectedAiProviderCredential? protectedCredential)
    {
        var ciphertextParameter = parameters.Add(
            "$api_key_ciphertext",
            SqliteType.Blob);
        ciphertextParameter.Value = protectedCredential.HasValue
            ? protectedCredential.Value.Ciphertext
            : DBNull.Value;
        var saltParameter = parameters.Add("$api_key_salt", SqliteType.Blob);
        saltParameter.Value = protectedCredential.HasValue
            ? protectedCredential.Value.Salt
            : DBNull.Value;
        var protectionVersionParameter = parameters.Add(
            "$api_key_protection_version",
            SqliteType.Integer);
        protectionVersionParameter.Value = protectedCredential.HasValue
            ? protectedCredential.Value.ProtectionVersion
            : DBNull.Value;
    }

    private static string FormatId(Guid id) =>
        id.ToString("D", CultureInfo.InvariantCulture);

    private static long ToUtcTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    private static DateTimeOffset ReadUtcTimestamp(long ticks) =>
        new(ticks, TimeSpan.Zero);

    private sealed record PersistedProfile(
        AiProviderProfileSnapshot Snapshot,
        byte[]? Ciphertext,
        byte[]? Salt,
        int? CredentialProtectionVersion,
        long CreatedAtUtcTicks,
        long UpdatedAtUtcTicks);
}
