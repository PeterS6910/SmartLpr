using System;
using System.Collections.Generic;

namespace SmartLpr.ServerGetLpr;

/// <summary>
/// Event arguments raised whenever the server receives a Nanopack push notification that was parsed
/// successfully.
/// </summary>
public sealed class LicensePlateReceivedEventArgs : EventArgs
{
    internal LicensePlateReceivedEventArgs(
        NanopackPushPayload payload,
        string? remoteAddress,
        IReadOnlyDictionary<string, string> headers,
        string httpMethod,
        Uri requestUri,
        DateTimeOffset receivedAtUtc)
    {
        Payload = payload;
        RemoteAddress = remoteAddress;
        Headers = headers;
        HttpMethod = httpMethod;
        RequestUri = requestUri;
        ReceivedAtUtc = receivedAtUtc;
    }

    /// <summary>
    /// Gets the payload that was parsed from the Nanopack push message.
    /// </summary>
    public NanopackPushPayload Payload { get; }

    /// <summary>
    /// Gets the remote IP address of the camera (if provided by <see cref="System.Net.HttpListener"/>).
    /// </summary>
    public string? RemoteAddress { get; }

    /// <summary>
    /// Gets a read-only snapshot of HTTP headers supplied with the push request.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// Gets the HTTP method used by the camera.
    /// </summary>
    public string HttpMethod { get; }

    /// <summary>
    /// Gets the absolute URI that was invoked by the camera.
    /// </summary>
    public Uri RequestUri { get; }

    /// <summary>
    /// Gets the moment the push message was received (UTC).
    /// </summary>
    public DateTimeOffset ReceivedAtUtc { get; }
}