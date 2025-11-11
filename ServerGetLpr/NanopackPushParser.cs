using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;

namespace SmartLpr.ServerGetLpr;

internal sealed class NanopackPushParser
{
    private static readonly Regex PlateRegex = new(
        @"(?<![A-Z0-9])([A-Z0-9]{2,3}[- _]?[A-Z0-9]{3,4})(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public NanopackPushPayload? Parse(HttpListenerRequest request, string body, ServerGetLprOptions options)
    {
        var accumulator = new FieldAccumulator();

        AddQueryStringFields(request.QueryString, accumulator);

        string? bodySource = null;
        if (!string.IsNullOrEmpty(body))
        {
            if (LooksLikeJson(request.ContentType, body))
            {
                bodySource = "json";
                ParseJson(body, accumulator, bodySource, options.Logger);
            }
            else if (LooksLikeForm(request.ContentType, body))
            {
                bodySource = "form";
                ParseForm(body, request.ContentEncoding, accumulator, bodySource);
            }
            else
            {
                bodySource = "text";
                ParseLooseKeyValuePairs(body, accumulator, bodySource);
            }
        }

        var plateResult = accumulator.Find(options.PlateKeys);
        string? plate = NormalizePlate(plateResult.value);
        var plateSource = plateResult.source ?? bodySource ?? "unknown";

        if (string.IsNullOrWhiteSpace(plate))
        {
            var fallback = ExtractPlateFallback(accumulator, body);
            if (!string.IsNullOrEmpty(fallback))
            {
                plate = fallback;
                plateSource = "regex";
            }
        }

        if (string.IsNullOrWhiteSpace(plate))
        {
            options.Logger?.Invoke("Nanopack push payload did not contain a recognisable license plate.");
            return null;
        }

        var timestampResult = accumulator.Find(options.TimestampKeys);
        var timestamp = ParseTimestamp(timestampResult.value, options.Logger);

        var confidenceResult = accumulator.Find(options.ConfidenceKeys);
        var confidence = ParseConfidence(confidenceResult.value, options.Logger);

        var cameraIdResult = accumulator.Find(options.CameraIdKeys);
        var cameraNameResult = accumulator.Find(options.CameraNameKeys);
        var tokenResult = accumulator.Find(options.TokenKeys);

        return new NanopackPushPayload(
            plate!,
            plateSource,
            timestamp,
            timestampResult.source,
            confidence,
            confidenceResult.source,
            NormalizeCameraField(cameraIdResult.value),
            NormalizeCameraField(cameraNameResult.value),
            tokenResult.value,
            tokenResult.source,
            body,
            accumulator.ToValueDictionary(),
            accumulator.ToSourceDictionary());
    }

    private static void AddQueryStringFields(NameValueCollection? query, FieldAccumulator accumulator)
    {
        if (query == null)
        {
            return;
        }

        foreach (string key in query)
        {
            if (key == null)
            {
                continue;
            }

            var value = query[key];
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            accumulator.Set(key, value, "query");
        }
    }

    private static void ParseJson(
        string body,
        FieldAccumulator accumulator,
        string origin,
        Action<string>? logger)
    {
        try
        {
            var serializer = new JavaScriptSerializer();
            var deserialized = serializer.DeserializeObject(body);
            if (deserialized == null)
            {
                return;
            }

            foreach (var kvp in Flatten(deserialized, null))
            {
                if (string.IsNullOrEmpty(kvp.Key) || kvp.Value == null)
                {
                    continue;
                }

                accumulator.Set(kvp.Key, kvp.Value, origin);
            }
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
        {
            logger?.Invoke($"Failed to parse JSON payload: {ex.Message}");
        }
    }

    private static void ParseForm(
        string body,
        Encoding? encoding,
        FieldAccumulator accumulator,
        string origin)
    {
        var parsed = HttpUtility.ParseQueryString(body, encoding ?? Encoding.UTF8);
        foreach (string key in parsed)
        {
            if (key == null)
            {
                continue;
            }

            var value = parsed[key];
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            accumulator.Set(key, value, origin);
        }
    }

    private static void ParseLooseKeyValuePairs(
        string body,
        FieldAccumulator accumulator,
        string origin)
    {
        var separators = new[] { '\n', '\r', '&', ';' };
        var pairs = body.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var index = pair.IndexOfAny(new[] { '=', ':' });
            if (index <= 0 || index >= pair.Length - 1)
            {
                continue;
            }

            var key = pair.Substring(0, index).Trim();
            var value = pair.Substring(index + 1).Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            var decodedKey = HttpUtility.UrlDecode(key) ?? key;
            var decodedValue = HttpUtility.UrlDecode(value) ?? value;
            accumulator.Set(decodedKey, decodedValue, origin);
        }
    }

    private static bool LooksLikeJson(string? contentType, string body)
    {
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        var trimmed = body.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal);
    }

    private static bool LooksLikeForm(string? contentType, string body)
    {
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return body.Contains("=") && body.Contains("&");
    }

    private static IEnumerable<KeyValuePair<string, string>> Flatten(object value, string? prefix)
    {
        switch (value)
        {
            case IDictionary<string, object> dict:
                foreach (var kvp in dict)
                {
                    var key = string.IsNullOrEmpty(prefix) ? kvp.Key : prefix + "." + kvp.Key;
                    foreach (var nested in Flatten(kvp.Value, key))
                    {
                        yield return nested;
                    }
                }

                break;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    var rawKey = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                    var key = string.IsNullOrEmpty(prefix) ? rawKey : prefix + "." + rawKey;

                    foreach (var nested in Flatten(entry.Value!, key))
                    {
                        yield return nested;
                    }
                }

                break;
            case IEnumerable enumerable when value is not string:
                var index = 0;
                foreach (var item in enumerable)
                {
                    var key = string.IsNullOrEmpty(prefix)
                        ? index.ToString(CultureInfo.InvariantCulture)
                        : string.Format(CultureInfo.InvariantCulture, "{0}[{1}]", prefix, index);

                    foreach (var nested in Flatten(item!, key))
                    {
                        yield return nested;
                    }

                    index++;
                }

                break;
            case null:
                yield break;
            default:
                var stringValue = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (stringValue != null)
                {
                    yield return new KeyValuePair<string, string>(prefix ?? string.Empty, stringValue);
                }

                break;
        }
    }

    private static string? ExtractPlateFallback(FieldAccumulator accumulator, string body)
    {
        foreach (var field in accumulator.EnumerateValues())
        {
            var match = PlateRegex.Match(field.Value);
            if (match.Success)
            {
                return NormalizePlate(match.Groups[1].Value);
            }
        }

        if (!string.IsNullOrEmpty(body))
        {
            var match = PlateRegex.Match(body);
            if (match.Success)
            {
                return NormalizePlate(match.Groups[1].Value);
            }
        }

        return null;
    }

    private static string? NormalizePlate(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
        {
            return null;
        }

        var builder = new StringBuilder(plate.Length);
        foreach (var ch in plate)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static string? NormalizeCameraField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static DateTimeOffset? ParseTimestamp(string? candidate, Action<string>? logger)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim();
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return dto;
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            try
            {
                if (trimmed.Length >= 16)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(integer / 1000);
                }

                if (trimmed.Length >= 13)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(integer);
                }

                return DateTimeOffset.FromUnixTimeSeconds(integer);
            }
            catch (ArgumentOutOfRangeException)
            {
                logger?.Invoke($"Timestamp '{trimmed}' is outside the supported Unix epoch range.");
            }
        }
        else if (double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var floating))
        {
            try
            {
                if (Math.Abs(floating) >= 1_000_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(floating));
                }

                if (Math.Abs(floating) >= 1_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(floating));
                }

                return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(floating * 1000));
            }
            catch (ArgumentOutOfRangeException)
            {
                logger?.Invoke($"Timestamp '{trimmed}' is outside the supported Unix epoch range.");
            }
        }
        else
        {
            logger?.Invoke($"Unable to parse timestamp value '{trimmed}'.");
        }

        return null;
    }

    private static double? ParseConfidence(string? candidate, Action<string>? logger)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
        {
            logger?.Invoke($"Unable to parse confidence value '{trimmed}'.");
            return null;
        }

        if (value > 1.0 && value <= 100.0)
        {
            value /= 100.0;
        }
        else if (value > 100.0)
        {
            value = 1.0;
        }
        else if (value < 0.0)
        {
            value = 0.0;
        }

        return value;
    }

    private sealed class FieldAccumulator
    {
        private readonly Dictionary<string, FieldValue> _fields = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FieldValue> _normalized = new(StringComparer.Ordinal);

        public void Set(string key, string value, string origin)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null)
            {
                return;
            }

            var trimmedKey = key.Trim();
            var field = new FieldValue(trimmedKey, value, origin);
            _fields[trimmedKey] = field;

            var normalizedKey = NormalizeKey(trimmedKey);
            _normalized[normalizedKey] = field;

            var simplified = NormalizeKey(ExtractSimplifiedKey(trimmedKey));
            if (simplified.Length > 0)
            {
                _normalized[simplified] = field;
            }
        }

        public (string? value, string? source) Find(IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                var normalized = NormalizeKey(key);
                if (_normalized.TryGetValue(normalized, out var field))
                {
                    return (field.Value, field.Source);
                }
            }

            return (null, null);
        }

        public IEnumerable<FieldValue> EnumerateValues()
        {
            return _fields.Values;
        }

        public IDictionary<string, string> ToValueDictionary()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _fields)
            {
                result[pair.Key] = pair.Value.Value;
            }

            return result;
        }

        public IDictionary<string, string> ToSourceDictionary()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _fields)
            {
                result[pair.Key] = pair.Value.Source;
            }

            return result;
        }
    }

    private sealed class FieldValue
    {
        public FieldValue(string key, string value, string source)
        {
            Key = key;
            Value = value;
            Source = source;
        }

        public string Key { get; }
        public string Value { get; }
        public string Source { get; }
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static string ExtractSimplifiedKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var simplified = key;
        var separatorIndex = simplified.LastIndexOfAny(new[] { '.', '/', '\\', ':' });
        if (separatorIndex >= 0 && separatorIndex + 1 < simplified.Length)
        {
            simplified = simplified.Substring(separatorIndex + 1);
        }

        if (simplified.EndsWith("]", StringComparison.Ordinal))
        {
            var bracketIndex = simplified.LastIndexOf('[');
            if (bracketIndex >= 0)
            {
                simplified = simplified.Substring(0, bracketIndex);
            }
        }

        return simplified;
    }
}