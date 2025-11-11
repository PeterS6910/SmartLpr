using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace SmartLpr.ServerGetLpr;

/// <summary>
/// Represents a single Nanopack push payload that has been successfully parsed by <see cref="ServerGetLpr"/>.
/// </summary>
public sealed class NanopackPushPayload
{
    private readonly ReadOnlyDictionary<string, string> _fields;
    private readonly ReadOnlyDictionary<string, string> _fieldSources;

    internal NanopackPushPayload(
        string plate,
        string plateSource,
        DateTimeOffset? timestamp,
        string? timestampSource,
        double? confidence,
        string? confidenceSource,
        string? cameraId,
        string? cameraName,
        string? token,
        string? tokenSource,
        string rawBody,
        IDictionary<string, string> fields,
        IDictionary<string, string> fieldSources)
    {
        Plate = plate;
        PlateSource = plateSource;
        Timestamp = timestamp;
        TimestampSource = timestampSource;
        Confidence = confidence;
        ConfidenceSource = confidenceSource;
        CameraId = cameraId;
        CameraName = cameraName;
        Token = token;
        TokenSource = tokenSource;
        RawBody = rawBody;
        _fields = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase));
        _fieldSources = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(fieldSources, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The detected license plate string extracted from the push payload.
    /// </summary>
    public string Plate { get; }

    /// <summary>
    /// Indicates where the plate value originated from (e.g. <c>json</c>, <c>form</c>, <c>query</c>, <c>regex</c>).
    /// </summary>
    public string PlateSource { get; }

    /// <summary>
    /// Optional timestamp describing when the plate was captured by the camera.
    /// </summary>
    public DateTimeOffset? Timestamp { get; }

    /// <summary>
    /// Source indicator for <see cref="Timestamp"/>.
    /// </summary>
    public string? TimestampSource { get; }

    /// <summary>
    /// Optional confidence value reported by the camera. Normalised to the 0-1 range when possible.
    /// </summary>
    public double? Confidence { get; }

    /// <summary>
    /// Source indicator for <see cref="Confidence"/>.
    /// </summary>
    public string? ConfidenceSource { get; }

    /// <summary>
    /// Optional identifier describing which camera sent the event.
    /// </summary>
    public string? CameraId { get; }

    /// <summary>
    /// Optional human readable name of the camera.
    /// </summary>
    public string? CameraName { get; }

    /// <summary>
    /// Optional token supplied by the camera (e.g. shared secret).
    /// </summary>
    public string? Token { get; }

    /// <summary>
    /// Source indicator for <see cref="Token"/>.
    /// </summary>
    public string? TokenSource { get; }

    /// <summary>
    /// The raw request body as received from the camera.
    /// </summary>
    public string RawBody { get; }

    /// <summary>
    /// Returns a case-insensitive read-only view of all fields discovered in the payload.
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields => _fields;

    /// <summary>
    /// Returns the origin (json/form/query/etc.) for each entry inside <see cref="Fields"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> FieldSources => _fieldSources;

    /// <summary>
    /// Tries to get a payload field using case-insensitive lookup.
    /// </summary>
    public bool TryGetField(string key, [NotNullWhen(true)] out string? value)
    {
        if (_fields.TryGetValue(key, out var fieldValue))
        {
            value = fieldValue;
            return true;
        }

        value = null;
        return false;
    }
}