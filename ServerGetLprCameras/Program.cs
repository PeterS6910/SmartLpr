using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

var server = new LprServer(new Uri("http://localhost:5000/"));
Console.CancelKeyPress += async (_, args) =>
{
    args.Cancel = true;
    await server.StopAsync();
};

await server.StartAsync();

public sealed class LprServer
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, RegisteredCamera> _cameras = new();
    private readonly ConcurrentDictionary<string, DetectionEvent> _events = new();
    private readonly ConcurrentDictionary<string, ProcessingStatus> _processing = new();

    public LprServer(Uri baseAddress)
    {
        if (!HttpListener.IsSupported)
        {
            throw new NotSupportedException("HttpListener is not supported on this platform.");
        }

        if (!baseAddress.AbsoluteUri.EndsWith('/'))
        {
            baseAddress = new Uri(baseAddress.AbsoluteUri + "/");
        }

        _listener.Prefixes.Add(baseAddress.AbsoluteUri);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _listener.Start();
        Console.WriteLine($"Listening on: {string.Join(", ", _listener.Prefixes)}");

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (!_listener.IsListening)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
        }
    }

    public Task StopAsync()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
            Console.WriteLine("Server stopped.");
        }

        return Task.CompletedTask;
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            Console.WriteLine($"{DateTimeOffset.UtcNow:o} {context.Request.HttpMethod} {context.Request.RawUrl}");

            switch (context.Request.HttpMethod)
            {
                case "POST" when Matches(context.Request, "/api/lpr/register"):
                    await HandleRegisterAsync(context).ConfigureAwait(false);
                    break;
                case "POST" when Matches(context.Request, "/api/lpr/heartbeat"):
                    await HandleHeartbeatAsync(context).ConfigureAwait(false);
                    break;
                case "POST" when Matches(context.Request, "/api/lpr/events"):
                    await HandleEventAsync(context).ConfigureAwait(false);
                    break;
                case "PATCH" when TryMatchDetection(context.Request, out var detectionId):
                    await HandleProcessingUpdateAsync(context, detectionId!).ConfigureAwait(false);
                    break;
                case "DELETE" when TryMatchCamera(context.Request, out var cameraId):
                    await HandleDeregisterAsync(context, cameraId!).ConfigureAwait(false);
                    break;
                default:
                    await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
                    {
                        schemaVersion = 1,
                        message = "Endpoint not implemented"
                    }).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (context.Response.OutputStream.CanWrite)
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.InternalServerError, new
                {
                    schemaVersion = 1,
                    message = "Unexpected server error"
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleRegisterAsync(HttpListenerContext context)
    {
        var payload = await DeserializeAsync<RegisterCameraRequest>(context.Request).ConfigureAwait(false);
        if (payload is null || string.IsNullOrWhiteSpace(payload.CameraId))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new
            {
                schemaVersion = 1,
                message = "cameraId is required"
            }).ConfigureAwait(false);
            return;
        }

        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var camera = new RegisteredCamera(payload.CameraId!, payload.UniqueKey, payload.InterfaceSource)
        {
            IpAddress = payload.IpAddress,
            MacAddress = payload.MacAddress,
            HttpPort = payload.HttpPort,
            HttpsPort = payload.HttpsPort,
            Model = payload.Model,
            FirmwareVersion = payload.FirmwareVersion,
            LastNonce = token,
            TokenExpiresAt = expiresAt
        };

        _cameras[payload.CameraId!] = camera;

        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
        {
            schemaVersion = payload.SchemaVersion,
            nonce = token,
            expiresAt
        }).ConfigureAwait(false);
    }

    private async Task HandleHeartbeatAsync(HttpListenerContext context)
    {
        var payload = await DeserializeAsync<HeartbeatRequest>(context.Request).ConfigureAwait(false);
        if (payload is null || string.IsNullOrWhiteSpace(payload.CameraId) || !_cameras.TryGetValue(payload.CameraId!, out var camera))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
            {
                schemaVersion = 1,
                message = "Camera not registered"
            }).ConfigureAwait(false);
            return;
        }

        camera.LastHeartbeatAt = payload.LastHeartbeatAt ?? DateTimeOffset.UtcNow;
        camera.IsOnline = payload.IsOnline;
        camera.HealthState = payload.HealthState;

        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
        {
            schemaVersion = payload.SchemaVersion,
            message = "Heartbeat accepted"
        }).ConfigureAwait(false);
    }

    private async Task HandleEventAsync(HttpListenerContext context)
    {
        var payload = await DeserializeAsync<DetectionEvent>(context.Request).ConfigureAwait(false);
        if (payload is null || string.IsNullOrWhiteSpace(payload.DetectionId) || string.IsNullOrWhiteSpace(payload.CameraId))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new
            {
                schemaVersion = 1,
                message = "Invalid detection payload"
            }).ConfigureAwait(false);
            return;
        }

        var processingId = Guid.NewGuid().ToString("N");
        _events[payload.DetectionId!] = payload;
        _processing[payload.DetectionId!] = new ProcessingStatus(processingId, "received", DateTimeOffset.UtcNow);

        context.Response.StatusCode = (int)HttpStatusCode.Accepted;
        await WriteJsonAsync(context.Response, HttpStatusCode.Accepted, new
        {
            schemaVersion = payload.SchemaVersion,
            processingId
        }).ConfigureAwait(false);
    }

    private async Task HandleProcessingUpdateAsync(HttpListenerContext context, string detectionId)
    {
        var payload = await DeserializeAsync<ProcessingUpdateRequest>(context.Request).ConfigureAwait(false);
        if (payload is null || !_events.ContainsKey(detectionId))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
            {
                schemaVersion = payload?.SchemaVersion ?? 1,
                message = "Detection not found"
            }).ConfigureAwait(false);
            return;
        }

        _processing[detectionId] = new ProcessingStatus(payload!.ProcessingId ?? detectionId, payload.Status ?? "unknown", DateTimeOffset.UtcNow)
        {
            Metadata = payload.Metadata
        };

        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
        {
            schemaVersion = payload.SchemaVersion,
            message = "Processing state updated"
        }).ConfigureAwait(false);
    }

    private async Task HandleDeregisterAsync(HttpListenerContext context, string cameraId)
    {
        if (_cameras.TryRemove(cameraId, out _))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
            {
                schemaVersion = 1,
                message = "Camera deregistered"
            }).ConfigureAwait(false);
        }
        else
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
            {
                schemaVersion = 1,
                message = "Camera not found"
            }).ConfigureAwait(false);
        }
    }

    private static bool Matches(HttpListenerRequest request, string path)
    {
        return request.Url?.AbsolutePath.Equals(path, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryMatchDetection(HttpListenerRequest request, out string? detectionId)
    {
        var path = request.Url?.AbsolutePath;
        if (path is not null && request.HttpMethod.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
        {
            const string prefix = "/api/lpr/events/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                detectionId = path[prefix.Length..];
                return !string.IsNullOrWhiteSpace(detectionId);
            }
        }

        detectionId = null;
        return false;
    }

    private static bool TryMatchCamera(HttpListenerRequest request, out string? cameraId)
    {
        var path = request.Url?.AbsolutePath;
        if (path is not null && request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            const string prefix = "/api/lpr/register/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cameraId = path[prefix.Length..];
                return !string.IsNullOrWhiteSpace(cameraId);
            }
        }

        cameraId = null;
        return false;
    }

    private static async Task<T?> DeserializeAsync<T>(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions.Default);
    }

    private static Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(payload, JsonOptions.Default);
        var buffer = Encoding.UTF8.GetBytes(json);
        return response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
    }
}

public sealed record RegisteredCamera(string CameraId, string? UniqueKey, string? InterfaceSource)
    {
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public int? HttpPort { get; set; }
    public int? HttpsPort { get; set; }
    public string? Model { get; set; }
    public string? FirmwareVersion { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public bool? IsOnline { get; set; }
    public string? HealthState { get; set; }
    public string? LastNonce { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
}

public sealed record DetectionEvent
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("detectionId")]
        public string? DetectionId { get; init; }

        [JsonPropertyName("cameraId")]
        public string? CameraId { get; init; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; init; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; init; }

        [JsonPropertyName("plateNumber")]
        public string? PlateNumber { get; init; }

        [JsonPropertyName("direction")]
        public string? Direction { get; init; }

        [JsonPropertyName("imageUrls")]
        public IReadOnlyList<string>? ImageUrls { get; init; }

        [JsonPropertyName("imageHash")]
        public string? ImageHash { get; init; }

        [JsonPropertyName("cameraState")]
        public string? CameraState { get; init; }

        [JsonPropertyName("cameraSnapshot")]
        public CameraSnapshot? CameraSnapshot { get; init; }
    }

    public sealed record CameraSnapshot
    {
        [JsonPropertyName("ipAddress")]
        public string? IpAddress { get; init; }

        [JsonPropertyName("httpPort")]
        public int? HttpPort { get; init; }

        [JsonPropertyName("httpsPort")]
        public int? HttpsPort { get; init; }

        [JsonPropertyName("macAddress")]
        public string? MacAddress { get; init; }

        [JsonPropertyName("firmwareVersion")]
        public string? FirmwareVersion { get; init; }

        [JsonPropertyName("locked")]
        public bool? Locked { get; init; }

        [JsonPropertyName("lockingClientIp")]
        public string? LockingClientIp { get; init; }
    }

    public sealed record RegisterCameraRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("cameraId")]
        public string? CameraId { get; init; }

        [JsonPropertyName("uniqueKey")]
        public string? UniqueKey { get; init; }

        [JsonPropertyName("interfaceSource")]
        public string? InterfaceSource { get; init; }

        [JsonPropertyName("ipAddress")]
        public string? IpAddress { get; init; }

        [JsonPropertyName("macAddress")]
        public string? MacAddress { get; init; }

        [JsonPropertyName("httpPort")]
        public int? HttpPort { get; init; }

        [JsonPropertyName("httpsPort")]
        public int? HttpsPort { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("firmwareVersion")]
        public string? FirmwareVersion { get; init; }
    }

    public sealed record HeartbeatRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("cameraId")]
        public string? CameraId { get; init; }

        [JsonPropertyName("lastHeartbeatAt")]
        public DateTimeOffset? LastHeartbeatAt { get; init; }

        [JsonPropertyName("isOnline")]
        public bool? IsOnline { get; init; }

        [JsonPropertyName("healthState")]
        public string? HealthState { get; init; }
    }

    public sealed record ProcessingUpdateRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("processingId")]
        public string? ProcessingId { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("metadata")]
        public IDictionary<string, string>? Metadata { get; init; }
    }

    public sealed record ProcessingStatus(string ProcessingId, string Status, DateTimeOffset UpdatedAt)
    {
    public IDictionary<string, string>? Metadata { get; set; }
}

file static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}