using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Screen.Core;

// Only these fields may leave the translation UI for the diagnostic log.
public sealed record TranslationFailure
{
    private TranslationFailure(TranslationProviderKind provider, string kind, HttpStatusCode? httpStatus, string? serviceCode)
    {
        Provider = provider;
        Kind = kind;
        HttpStatus = httpStatus;
        ServiceCode = serviceCode;
    }

    public TranslationProviderKind Provider { get; }
    public bool IsSecondaryAi { get; internal init; }
    public string Kind { get; }
    public HttpStatusCode? HttpStatus { get; }
    public string? ServiceCode { get; }

    internal static TranslationFailure From(TranslationProviderKind provider, Exception exception)
    {
        HttpStatusCode? httpStatus = null;
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is TranslationServiceException service)
            {
                return new(provider, "ServiceError", httpStatus ?? service.HttpStatus, service.SafeCode);
            }
            if (cause is HttpRequestException transport)
            {
                httpStatus ??= transport.StatusCode;
                if (transport.InnerException is not TranslationServiceException)
                {
                    return new(provider, "HttpRequestException", httpStatus, null);
                }
            }
            if (cause is TimeoutException)
            {
                return new(provider, "Timeout", null, null);
            }
            if (cause is JsonException or BatchTranslationFormatException)
            {
                return new(provider, "InvalidResponse", null, null);
            }
        }
        return new(provider, "ProviderError", null, null);
    }

    public override string ToString() => $"{Provider}{(IsSecondaryAi ? " (secondary)" : "")}: {Kind}"
        + (HttpStatus is { } http ? $" (HTTP {(int)http})" : "")
        + (ServiceCode is { } code ? $" [service {code}]" : "");
}

internal sealed class TranslationServiceException(string message, HttpStatusCode httpStatus, string? safeCode)
    : InvalidOperationException(message)
{
    internal HttpStatusCode HttpStatus { get; } = httpStatus;
    internal string? SafeCode { get; } = safeCode;

    internal static string? MyMemoryCode(string? code) =>
        code?.Length == 3 && int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
        && status is >= 100 and <= 599 ? status.ToString(CultureInfo.InvariantCulture) : null;

    // Unknown suffixes may contain echoed user input or credentials, even when they look like identifiers.
    internal static string? TencentCode(string? code) => code switch
    {
        "AuthFailure.SignatureFailure" or "AuthFailure.SecretIdNotFound" or "AuthFailure.SignatureExpire"
            or "AuthFailure.InvalidSecretId" or "AuthFailure.TokenFailure" or "LimitExceeded.TooManyRequests"
            or "FailedOperation.ServiceIsolate" or "FailedOperation.UserNotRegistered" => code,
        _ => code?.Split('.', 2)[0] switch
        {
            "AuthFailure" => "AuthFailure",
            "FailedOperation" => "FailedOperation",
            "InternalError" => "InternalError",
            "InvalidParameter" => "InvalidParameter",
            "InvalidParameterValue" => "InvalidParameterValue",
            "LimitExceeded" => "LimitExceeded",
            "RequestLimitExceeded" => "RequestLimitExceeded",
            "ResourceUnavailable" => "ResourceUnavailable",
            "UnauthorizedOperation" => "UnauthorizedOperation",
            _ => null
        }
    };
}

public sealed class TranslationRouteException : InvalidOperationException
{
    internal TranslationRouteException(string message, IReadOnlyList<TranslationFailure> failures, Exception inner)
        : base(message, inner) => Failures = failures;

    public IReadOnlyList<TranslationFailure> Failures { get; }
}

// A single call can race two AI endpoints or fall back across multiple batches.
// Record each distinct failure once, in the order it was observed.
internal sealed class TranslationFailureTrace
{
    private readonly List<TranslationFailure> _failures = [];
    private readonly HashSet<Exception> _seenExceptions = new(ReferenceEqualityComparer.Instance);

    internal void Record(TranslationProviderKind provider, Exception exception, bool secondaryAi = false)
    {
        lock (_failures)
        {
            if (_seenExceptions.Add(exception))
            {
                var failure = TranslationFailure.From(provider, exception) with { IsSecondaryAi = secondaryAi };
                if (!_failures.Contains(failure))
                {
                    _failures.Add(failure);
                }
            }
        }
    }

    internal TranslationFailure[] Snapshot()
    {
        lock (_failures)
        {
            return _failures.ToArray();
        }
    }
}
