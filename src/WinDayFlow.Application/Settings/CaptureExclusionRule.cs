using System.Buffers;

namespace WinDayFlow.Application.Settings;

public enum CaptureExclusionRuleScope
{
    Application = 0,
    Window = 1,
}

public enum ApplicationIdentityKind
{
    ExecutableName = 0,
    PackageFamilyName = 1,
    PublisherCertificateSha256 = 2,
}

public enum WindowTitleMatchKind
{
    Exact = 0,
    StartsWith = 1,
    Contains = 2,
}

public sealed record CaptureExclusionRule
{
    private static readonly SearchValues<char> PackageNameCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.-");
    private static readonly SearchValues<char> PublisherIdCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");

    public const int MaximumNameLength = 80;
    public const int MaximumExecutableNameLength = 260;
    public const int MaximumPackageFamilyNameLength = 255;
    public const int PublisherCertificateSha256Length = 64;
    public const int MaximumWindowTitlePatternLength = 256;

    public CaptureExclusionRule(
        Guid id,
        string name,
        bool enabled,
        CaptureExclusionRuleScope scope,
        ApplicationIdentityKind applicationIdentityKind,
        string identityValue,
        WindowTitleMatchKind? windowTitleMatchKind,
        string? pattern,
        long revision)
    {
        Id = ValidateId(id);
        Name = NormalizeRequiredText(name, MaximumNameLength, nameof(name));
        Enabled = enabled;
        Scope = ValidateEnum(scope, nameof(scope));
        ApplicationIdentityKind = ValidateEnum(
            applicationIdentityKind,
            nameof(applicationIdentityKind));
        IdentityValue = NormalizeIdentity(applicationIdentityKind, identityValue);
        (WindowTitleMatchKind, Pattern) = NormalizeWindowSelector(
            scope,
            windowTitleMatchKind,
            pattern);
        Revision = ValidateRevision(revision);
    }

    public Guid Id { get; }

    public string Name { get; }

    public bool Enabled { get; }

    public CaptureExclusionRuleScope Scope { get; }

    public ApplicationIdentityKind ApplicationIdentityKind { get; }

    public string IdentityValue { get; }

    public WindowTitleMatchKind? WindowTitleMatchKind { get; }

    public string? Pattern { get; }

    public long Revision { get; }

    public override string ToString()
    {
        return $"{nameof(CaptureExclusionRule)} {{ "
            + $"Id = {Id}, Enabled = {Enabled}, Scope = {Scope}, "
            + $"ApplicationIdentityKind = {ApplicationIdentityKind}, "
            + $"WindowTitleMatchKind = {WindowTitleMatchKind}, "
            + $"Revision = {Revision}, SensitiveValues = [REDACTED] }}";
    }

    public static CaptureExclusionRule Create(
        Guid id,
        string name,
        bool enabled,
        CaptureExclusionRuleScope scope,
        ApplicationIdentityKind applicationIdentityKind,
        string identityValue,
        WindowTitleMatchKind? windowTitleMatchKind = null,
        string? pattern = null)
    {
        return new CaptureExclusionRule(
            id,
            name,
            enabled,
            scope,
            applicationIdentityKind,
            identityValue,
            windowTitleMatchKind,
            pattern,
            revision: 1);
    }

    public static bool TryNormalizeApplicationIdentity(
        ApplicationIdentityKind identityKind,
        string? identityValue,
        out string normalizedIdentity)
    {
        try
        {
            _ = ValidateEnum(identityKind, nameof(identityKind));
            normalizedIdentity = NormalizeIdentity(identityKind, identityValue);
            return true;
        }
        catch (ArgumentException)
        {
            normalizedIdentity = string.Empty;
            return false;
        }
    }

    internal CaptureExclusionRule Change(
        string name,
        CaptureExclusionRuleScope scope,
        ApplicationIdentityKind applicationIdentityKind,
        string identityValue,
        WindowTitleMatchKind? windowTitleMatchKind,
        string? pattern)
    {
        var candidate = new CaptureExclusionRule(
            Id,
            name,
            Enabled,
            scope,
            applicationIdentityKind,
            identityValue,
            windowTitleMatchKind,
            pattern,
            Revision);
        return candidate == this
            ? this
            : candidate.WithRevision(NextRevision());
    }

    internal CaptureExclusionRule ChangeEnabled(bool enabled)
    {
        return enabled == Enabled
            ? this
            : new CaptureExclusionRule(
                Id,
                Name,
                enabled,
                Scope,
                ApplicationIdentityKind,
                IdentityValue,
                WindowTitleMatchKind,
                Pattern,
                NextRevision());
    }

    internal CaptureExclusionRule AdvanceRevision()
    {
        return WithRevision(NextRevision());
    }

    private CaptureExclusionRule WithRevision(long revision)
    {
        return new CaptureExclusionRule(
            Id,
            Name,
            Enabled,
            Scope,
            ApplicationIdentityKind,
            IdentityValue,
            WindowTitleMatchKind,
            Pattern,
            revision);
    }

    private long NextRevision()
    {
        if (Revision == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The capture exclusion rule revision has been exhausted.");
        }

        return Revision + 1;
    }

    private static Guid ValidateId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A capture exclusion rule must have a non-empty identifier.",
                nameof(id));
        }

        return id;
    }

    private static string NormalizeIdentity(
        ApplicationIdentityKind identityKind,
        string? identityValue)
    {
        var maximumLength = identityKind switch
        {
            ApplicationIdentityKind.ExecutableName => MaximumExecutableNameLength,
            ApplicationIdentityKind.PackageFamilyName => MaximumPackageFamilyNameLength,
            ApplicationIdentityKind.PublisherCertificateSha256 =>
                PublisherCertificateSha256Length,
            _ => throw new ArgumentOutOfRangeException(
                nameof(identityKind),
                identityKind,
                "The application identity kind is not supported."),
        };
        var normalized = NormalizeRequiredText(
            identityValue,
            maximumLength,
            nameof(identityValue));

        if (identityKind == ApplicationIdentityKind.ExecutableName
            && (normalized.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0
                || !normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || normalized.Length == ".exe".Length))
        {
            throw new ArgumentException(
                "An executable-name identity must be an .exe file name without a path or wildcard.",
                nameof(identityValue));
        }

        if (identityKind == ApplicationIdentityKind.PackageFamilyName)
        {
            ValidatePackageFamilyName(normalized, nameof(identityValue));
        }

        if (identityKind == ApplicationIdentityKind.PublisherCertificateSha256)
        {
            if (normalized.Length != PublisherCertificateSha256Length
                || normalized.Any(static character => !char.IsAsciiHexDigit(character)))
            {
                throw new ArgumentException(
                    "A publisher certificate identity must be a 64-character SHA-256 hexadecimal value.",
                    nameof(identityValue));
            }

            normalized = normalized.ToUpperInvariant();
        }

        return normalized;
    }

    private static (WindowTitleMatchKind? MatchKind, string? Pattern) NormalizeWindowSelector(
        CaptureExclusionRuleScope scope,
        WindowTitleMatchKind? windowTitleMatchKind,
        string? pattern)
    {
        if (scope == CaptureExclusionRuleScope.Application)
        {
            if (windowTitleMatchKind is not null || pattern is not null)
            {
                throw new ArgumentException(
                    "An application exclusion rule cannot contain a window-title selector.",
                    nameof(windowTitleMatchKind));
            }

            return (null, null);
        }

        if (windowTitleMatchKind is not { } matchKind)
        {
            throw new ArgumentException(
                "A window exclusion rule requires a window-title match kind.",
                nameof(windowTitleMatchKind));
        }

        _ = ValidateEnum(matchKind, nameof(windowTitleMatchKind));
        return (
            matchKind,
            ValidateWindowTitlePattern(
                pattern,
                nameof(pattern)));
    }

    private static string NormalizeRequiredText(
        string? value,
        int maximumLength,
        string parameterName,
        int minimumLength = 1)
    {
        if (value is null || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The value cannot contain control characters.",
                parameterName);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "The value cannot be null, blank, or whitespace.",
                parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length < minimumLength || normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                normalized.Length,
                $"The value must contain between {minimumLength} and {maximumLength} characters.");
        }

        return normalized;
    }

    private static string ValidateWindowTitlePattern(
        string? pattern,
        string parameterName)
    {
        if (pattern is null || pattern.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A window-title pattern cannot contain control characters.",
                parameterName);
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException(
                "A window-title pattern cannot be null, blank, or whitespace.",
                parameterName);
        }

        if (pattern.Length is < 2 or > MaximumWindowTitlePatternLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                pattern.Length,
                $"A window-title pattern must contain between 2 and {MaximumWindowTitlePatternLength} characters.");
        }

        return pattern;
    }

    private static void ValidatePackageFamilyName(
        string value,
        string parameterName)
    {
        var separator = value.LastIndexOf('_');
        var nameLength = separator;
        var publisherIdLength = value.Length - separator - 1;
        if (nameLength is < 3 or > 50
            || publisherIdLength != 13
            || value.AsSpan(0, nameLength).ContainsAnyExcept(PackageNameCharacters)
            || value.AsSpan(separator + 1).ContainsAnyExcept(PublisherIdCharacters))
        {
            throw new ArgumentException(
                "The package-family identity is not in the expected Windows package-family format.",
                parameterName);
        }
    }

    private static TEnum ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The value is not supported.");
        }

        return value;
    }

    private static long ValidateRevision(long revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "The capture exclusion rule revision must be positive.");
        }

        return revision;
    }
}
