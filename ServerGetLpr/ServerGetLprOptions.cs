using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;

namespace SmartLpr.ServerGetLpr;

/// <summary>
/// Configuration used by <see cref="ServerGetLpr"/>. The defaults are tuned for HTTPS push callbacks
/// emitted by Nanopack cameras, but every aspect can be customised when integrating with
/// different firmware revisions or bespoke deployments.
/// </summary>
public sealed class ServerGetLprOptions
{
    private readonly List<string> _prefixes = new();
    private readonly HashSet<string> _allowedMethods =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WebRequestMethods.Http.Post };
    private readonly HashSet<string> _plateKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "plate",
            "plate_text",
            "license_plate",
            "licenseplate",
            "plateNumber",
            "number",
            "spz",
            "tag",
            "lpr",
            "plateText"
        };
    private readonly HashSet<string> _timestampKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "timestamp",
            "time",
            "datetime",
            "date",
            "detected_at",
            "detectedAt",
            "event_time",
            "eventTime"
        };
    private readonly HashSet<string> _confidenceKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "confidence",
            "score",
            "probability",
            "accuracy",
            "quality"
        };
    private readonly HashSet<string> _tokenKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "token",
            "auth",
            "key",
            "secret",
            "access_token",
            "accessToken"
        };
    private readonly HashSet<string> _cameraIdKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "camera",
            "camera_id",
            "cameraId",
            "device",
            "device_id",
            "deviceId",
            "unit",
            "unitId"
        };
    private readonly HashSet<string> _cameraNameKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "camera_name",
            "cameraName",
            "device_name",
            "deviceName",
            "unit_name",
            "unitName",
            "location"
        };

    /// <summary>
    /// Gets or sets the maximum allowed size (in bytes) of an incoming payload. The default is 64 KiB.
    /// A value of <c>0</c> disables the limit.
    /// </summary>
    public int MaxRequestBodySize { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets whether HTTPS is required. When enabled (the default) all configured prefixes must
    /// use the <c>https://</c> scheme and plain HTTP requests will be rejected with 403 Forbidden.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Gets or sets whether an empty body is allowed. Nanopack cameras typically send JSON payloads,
    /// but certain integrations may only push query string fields. The default allows empty bodies.
    /// </summary>
    public bool AllowEmptyBody { get; set; } = true;

    /// <summary>
    /// Optional shared secret that must match one of the recognised token fields. When provided the
    /// server will reject requests that do not supply a matching token value.
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>
    /// Optional delegate invoked for diagnostic messages (warnings, parse failures, etc.).
    /// </summary>
    public Action<string>? Logger { get; set; }

    /// <summary>
    /// Gets or sets the maximum amount of time the server waits for background operations to finish
    /// while stopping. Defaults to five seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the text sent back to the camera when a push message is processed successfully.
    /// </summary>
    public string SuccessResponseMessage { get; set; } = "{\"status\":\"ok\"}";

    /// <summary>
    /// Gets or sets the HTTP status code returned when a push message is accepted.
    /// </summary>
    public int SuccessStatusCode { get; set; } = 200;

    /// <summary>
    /// Gets or sets the content type used when sending textual responses back to the camera.
    /// </summary>
    public string ResponseContentType { get; set; } = "application/json";

    /// <summary>
    /// Gets or sets the encoding used for textual responses and as a fallback when the request does
    /// not provide its own character set.
    /// </summary>
    public Encoding ResponseEncoding { get; set; } = Encoding.UTF8;

    /// <summary>
    /// Gets a mutable collection of HTTP prefixes that should be registered with <see cref="HttpListener"/>.
    /// </summary>
    public IList<string> Prefixes => _prefixes;

    /// <summary>
    /// Gets the set of HTTP methods accepted by the server. The collection is seeded with <c>POST</c> but
    /// may be extended if, for example, Nanopack firmware updates start using <c>PUT</c> or <c>PATCH</c>.
    /// </summary>
    public ISet<string> AllowedMethods => _allowedMethods;

    /// <summary>
    /// Gets the collection of field names that are treated as license plate candidates.
    /// </summary>
    public ISet<string> PlateKeys => _plateKeys;

    /// <summary>
    /// Gets the collection of field names that are treated as timestamps.
    /// </summary>
    public ISet<string> TimestampKeys => _timestampKeys;

    /// <summary>
    /// Gets the collection of field names that are treated as confidence values.
    /// </summary>
    public ISet<string> ConfidenceKeys => _confidenceKeys;

    /// <summary>
    /// Gets the collection of field names that are treated as authentication tokens.
    /// </summary>
    public ISet<string> TokenKeys => _tokenKeys;

    /// <summary>
    /// Gets the collection of field names that contain the camera identifier.
    /// </summary>
    public ISet<string> CameraIdKeys => _cameraIdKeys;

    /// <summary>
    /// Gets the collection of field names that contain the human readable camera name.
    /// </summary>
    public ISet<string> CameraNameKeys => _cameraNameKeys;

    /// <summary>
    /// Validates configured prefixes and returns an immutable snapshot for consumption by
    /// <see cref="ServerGetLpr"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no prefixes are configured.</exception>
    /// <exception cref="ArgumentException">Thrown when a prefix is invalid or violates the HTTPS requirement.</exception>
    internal IReadOnlyList<string> BuildPrefixSnapshot()
    {
        if (_prefixes.Count == 0)
        {
            throw new InvalidOperationException("At least one prefix must be configured for ServerGetLpr.");
        }

        var validated = new List<string>(_prefixes.Count);
        foreach (var rawPrefix in _prefixes)
        {
            if (string.IsNullOrWhiteSpace(rawPrefix))
            {
                throw new ArgumentException("Listener prefixes cannot be null or whitespace.", nameof(Prefixes));
            }

            var prefix = rawPrefix.Trim();
            if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri))
            {
                throw new ArgumentException($"Invalid listener prefix '{rawPrefix}'.", nameof(Prefixes));
            }

            if (RequireHttps && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The prefix '{rawPrefix}' must use HTTPS because RequireHttps is enabled.",
                    nameof(Prefixes));
            }

            if (!prefix.EndsWith("/", StringComparison.Ordinal))
            {
                prefix += "/";
            }

            validated.Add(prefix);
        }

        return new ReadOnlyCollection<string>(validated);
    }

    internal bool IsMethodAllowed(string? method)
        => method != null && _allowedMethods.Contains(method);
}