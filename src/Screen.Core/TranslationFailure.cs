using System.Net;
using System.Text.Json;

namespace Screen.Core;

// Only these fields may leave the translation UI for the diagnostic log.
public sealed record TranslationFailure
{
    private TranslationFailure(TranslationProviderKind provider, string kind, HttpStatusCode? httpStatus)
    {
        Provider = provider;
        Kind = kind;
        HttpStatus = httpStatus;
    }

    public TranslationProviderKind Provider { get; }
    public bool IsSecondaryAi { get; internal init; }
    public string Kind { get; }
    public HttpStatusCode? HttpStatus { get; }

    internal static TranslationFailure From(TranslationProviderKind provider, Exception exception)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is HttpRequestException transport)
            {
                return new(provider, "HttpRequestException", transport.StatusCode);
            }
            if (cause is TimeoutException)
            {
                return new(provider, "Timeout", null);
            }
            if (cause is JsonException or BatchTranslationFormatException)
            {
                return new(provider, "InvalidResponse", null);
            }
        }
        return new(provider, "ProviderError", null);
    }

    public override string ToString() => $"{Provider}{(IsSecondaryAi ? " (secondary)" : "")}: {Kind}"
        + (HttpStatus is { } http ? $" (HTTP {(int)http})" : "");
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
