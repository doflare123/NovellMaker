using System.Globalization;
using System.Text.Json;

namespace NovellMaker.Core.ExternalServices;

public readonly record struct ReleaseVersion(ushort Major, ushort Minor, ushort Build, ushort Revision) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? text, out ReleaseVersion version, bool tag = false)
    {
        version = default;
        if (text is null || text.Length > 24) return false;
        if (tag && text.StartsWith('v')) text = text[1..];
        var parts = text.Split('.');
        if (parts.Length is not (3 or 4)) return false;
        Span<ushort> values = stackalloc ushort[4];
        values.Clear();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > 1 && part[0] == '0' ||
                part.Any(c => c is < '0' or > '9') ||
                !ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return false;
        }
        version = new(values[0], values[1], values[2], values[3]);
        return true;
    }

    public int CompareTo(ReleaseVersion other) => (Major, Minor, Build, Revision).CompareTo((other.Major, other.Minor, other.Build, other.Revision));
    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}.{Build}.{Revision}");
}

public sealed record ReleaseCheckConfiguration
{
    public const string Provider = "github_releases_v1";
    public string Owner { get; }
    public string Repository { get; }
    public ReleaseVersion CurrentVersion { get; }

    public ReleaseCheckConfiguration(string owner, string repository, string currentVersion)
    {
        // Reserve documented credential prefixes, irrespective of current token length/encoding.
        // This is an accidental-paste guard, not a general secret detector.
        if (repository is not null && new[] { "ghp_", "github_pat_", "gho_", "ghu_", "ghs_", "ghr_" }
            .Any(prefix => repository.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Имя репозитория похоже на токен GitHub. Укажите публичное имя репозитория, без токена.");
        static bool AlphaNumeric(char c) => char.IsAsciiLetterOrDigit(c);
        if (owner is not { Length: >= 1 and <= 39 } || !AlphaNumeric(owner[0]) || !AlphaNumeric(owner[^1]) ||
            owner.Contains("--", StringComparison.Ordinal) || owner.Any(c => !AlphaNumeric(c) && c != '-') ||
            repository is not { Length: >= 1 and <= 100 } || repository is "." or ".." ||
            repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ||
            repository.Any(c => !AlphaNumeric(c) && c is not ('_' or '-' or '.')) ||
            !ReleaseVersion.TryParse(currentVersion, out var version))
            throw new ArgumentException("Некорректная конфигурация проверки выпуска.");
        Owner = owner; Repository = repository; CurrentVersion = version;
    }

    public bool IsValidPage(string? page, ReleaseVersion expectedVersion)
    {
        if (page is not { Length: <= 512 } || !Uri.TryCreate(page, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || page.Contains('\\')) return false;
        var prefix = $"https://github.com/{Owner}/{Repository}/releases/tag/";
        // Compare the original spelling too: Uri canonicalization must not hide traversal or delimiters.
        if (!page.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var segment = page[prefix.Length..];
        var decoded = Uri.UnescapeDataString(segment);
        return decoded.IndexOfAny(['/', '\\', '%']) < 0 &&
            ReleaseVersion.TryParse(decoded, out var actual, tag: true) && actual == expectedVersion &&
            uri.AbsolutePath.Equals($"/{Owner}/{Repository}/releases/tag/{segment}", StringComparison.OrdinalIgnoreCase);
    }
}

public enum ReleaseCheckCode
{
    Ok, Disabled, Busy, RateLimited, Cancelled, Timeout, NetworkUnavailable, TlsFailure,
    NotFound, AccessDenied, ProviderUnavailable, InvalidResponse, ResponseTooLarge, InvalidVersion
}

public sealed record ReleaseCheckData(string CurrentVersion, string LatestVersion, bool UpdateAvailable,
    string PageUrl, DateTimeOffset CheckedAt, bool FromCache = false)
{
    public bool IsValid(ReleaseCheckConfiguration configuration) =>
        CurrentVersion == configuration.CurrentVersion.ToString() &&
        ReleaseVersion.TryParse(LatestVersion, out var latest) && LatestVersion == latest.ToString() &&
        UpdateAvailable == (latest.CompareTo(configuration.CurrentVersion) > 0) &&
        CheckedAt.Offset == TimeSpan.Zero && configuration.IsValidPage(PageUrl, latest) &&
        JsonSerializer.SerializeToUtf8Bytes(this).Length <= 8192;
}

/// <summary>Adapter reply. Validated RetryAfterSeconds constrains the next HTTP request independently of Code or release parsing.</summary>
public sealed record ReleaseProviderReply(ReleaseCheckCode Code, ReleaseCheckData? Release = null, int? RetryAfterSeconds = null);

public interface IReleaseCheckProvider
{
    Task<ReleaseProviderReply> CheckAsync(ReleaseCheckConfiguration configuration, CancellationToken cancellationToken);
}

public sealed record ReleaseCheckResult(Guid RequestId, ReleaseCheckCode Code, ReleaseCheckData? Release,
    int? RetryAfterSeconds, Guid Origin, int Depth)
{
    public void Validate()
    {
        if (RequestId == Guid.Empty || Origin == Guid.Empty || Depth is < 1 or > 32 || !Enum.IsDefined(Code) ||
            (Code == ReleaseCheckCode.RateLimited ? RetryAfterSeconds is null or <= 0 : RetryAfterSeconds is not null) ||
            (Success ? Release is null : Release is not null)) throw new ArgumentException("Некорректный результат проверки выпуска.");
        if (Release is { } data)
        {
            // Reconstruct the restricted repository configuration to validate the wire projection too.
            var parts = data.PageUrl?.Split('/');
            if (parts is not { Length: 8 }) throw new ArgumentException("Некорректная страница выпуска.");
            var configuration = new ReleaseCheckConfiguration(parts[3], parts[4], data.CurrentVersion);
            if (!data.IsValid(configuration)) throw new ArgumentException("Некорректные данные выпуска.");
        }
    }
    public bool Success => Code == ReleaseCheckCode.Ok;
    public string Provider => ReleaseCheckConfiguration.Provider;
    public string Operation => "CheckGameRelease";
    public string Message => Code switch
    {
        ReleaseCheckCode.Ok => "Проверка выпуска завершена.",
        ReleaseCheckCode.Disabled => "Проверка выпуска не настроена.",
        ReleaseCheckCode.Busy => "Проверка выпуска уже выполняется или очередь заполнена.",
        ReleaseCheckCode.RateLimited => "Проверку выпуска можно повторить позже.",
        ReleaseCheckCode.Cancelled => "Проверка выпуска отменена.",
        ReleaseCheckCode.Timeout => "Превышено время проверки выпуска.",
        ReleaseCheckCode.NetworkUnavailable => "Не удалось связаться с сервисом выпусков.",
        ReleaseCheckCode.TlsFailure => "Не удалось проверить защищённое соединение.",
        ReleaseCheckCode.NotFound => "Публичный репозиторий или полный выпуск не найден.",
        ReleaseCheckCode.AccessDenied => "Сервис отказал в доступе к выпуску.",
        ReleaseCheckCode.ProviderUnavailable => "Сервис выпусков временно недоступен.",
        ReleaseCheckCode.ResponseTooLarge => "Ответ сервиса превышает допустимый размер.",
        ReleaseCheckCode.InvalidVersion => "Версия выпуска имеет неподдерживаемый формат.",
        _ => "Сервис вернул некорректный ответ."
    };
}
