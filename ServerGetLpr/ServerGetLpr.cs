using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SmartLpr.ServerGetLpr;

/// <summary>
/// Lightweight HTTPS listener that accepts Nanopack push callbacks and exposes the decoded
/// license plates through <see cref="LicensePlateReceived"/>.
/// </summary>
public sealed class ServerGetLpr : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ServerGetLprOptions _options;
    private readonly NanopackPushParser _parser = new();
    private readonly object _syncRoot = new();

    private CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private bool _disposed;

    /// <summary>
    /// Creates a new instance configured with the specified <paramref name="options"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    public ServerGetLpr(ServerGetLprOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var prefixes = options.BuildPrefixSnapshot();
        _listener = new HttpListener();
        foreach (var prefix in prefixes)
        {
            _listener.Prefixes.Add(prefix);
        }
    }

    /// <summary>
    /// Occurs whenever a Nanopack push message is accepted and parsed successfully.
    /// </summary>
    public event EventHandler<LicensePlateReceivedEventArgs>? LicensePlateReceived;

    /// <summary>
    /// Starts listening for incoming HTTPS requests.
    /// </summary>
    public void Start()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (_listenTask != null)
            {
                throw new InvalidOperationException("ServerGetLpr is already running.");
            }

            _cts.Dispose();
            _cts = new CancellationTokenSource();
            _listener.Start();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Stops listening for incoming requests and waits for outstanding handlers to finish.
    /// </summary>
    public void Stop()
    {
        Task? listenTask = null;
        lock (_syncRoot)
        {
            if (_listenTask == null)
            {
                return;
            }

            _cts.Cancel();
            listenTask = _listenTask;
            _listenTask = null;
        }

        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (HttpListenerException)
        {
        }

        if (listenTask != null)
        {
            try
            {
                listenTask.Wait(_options.ShutdownTimeout);
            }
            catch (AggregateException ex)
            {
                if (ex.InnerExceptions.Any(inner => inner is not OperationCanceledException && inner is not ObjectDisposedException && inner is not HttpListenerException))
                {
                    throw;
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        _cts.Dispose();
        _listener.Close();
        GC.SuppressFinalize(this);
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (context == null)
            {
                continue;
            }

            _ = Task.Run(() => ProcessRequestAsync(context), CancellationToken.None);
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (_options.RequireHttps)
            {
                if (!context.Request.IsSecureConnection)
                {
                    await WriteResponseAsync(context, HttpStatusCode.Forbidden, "HTTPS is required.").ConfigureAwait(false);
                    return;
                }

                var url = context.Request.Url;
                if (url == null || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(context, HttpStatusCode.Forbidden, "HTTPS is required.").ConfigureAwait(false);
                    return;
                }
            }

            if (!_options.IsMethodAllowed(context.Request.HttpMethod))
            {
                await WriteResponseAsync(context, HttpStatusCode.MethodNotAllowed, "HTTP method not allowed.").ConfigureAwait(false);
                return;
            }

            string body;
            try
            {
                body = await ReadRequestBodyAsync(context.Request).ConfigureAwait(false);
            }
            catch (InvalidOperationException sizeError)
            {
                await WriteResponseAsync(context, HttpStatusCode.RequestEntityTooLarge, sizeError.Message).ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrEmpty(body) && !_options.AllowEmptyBody)
            {
                await WriteResponseAsync(context, HttpStatusCode.BadRequest, "Request body was empty.").ConfigureAwait(false);
                return;
            }

            var payload = _parser.Parse(context.Request, body, _options);
            if (payload == null)
            {
                await WriteResponseAsync(context, HttpStatusCode.BadRequest, "Failed to parse Nanopack payload.").ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(_options.SharedSecret))
            {
                var token = payload.Token;
                if (string.IsNullOrEmpty(token) || !ConstantTimeEquals(token, _options.SharedSecret))
                {
                    await WriteResponseAsync(context, HttpStatusCode.Unauthorized, "Invalid shared secret.").ConfigureAwait(false);
                    return;
                }
            }

            var remoteAddress = context.Request.RemoteEndPoint?.ToString();
            var headers = CopyHeaders(context.Request.Headers);
            var eventArgs = new LicensePlateReceivedEventArgs(
                payload,
                remoteAddress,
                headers,
                context.Request.HttpMethod ?? "POST",
                context.Request.Url ?? new Uri("https://localhost/"),
                DateTimeOffset.UtcNow);

            try
            {
                LicensePlateReceived?.Invoke(this, eventArgs);
            }
            catch (Exception callbackError)
            {
                _options.Logger?.Invoke($"LicensePlateReceived handler threw an exception: {callbackError}");
            }

            await WriteResponseAsync(
                    context,
                    (HttpStatusCode)_options.SuccessStatusCode,
                    _options.SuccessResponseMessage,
                    _options.ResponseContentType)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _options.Logger?.Invoke($"ServerGetLpr encountered an unexpected error: {ex}");
            try
            {
                await WriteResponseAsync(context, HttpStatusCode.InternalServerError, "Internal server error.").ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
        finally
        {
            try
            {
                context.Response.OutputStream.Close();
            }
            catch
            {
            }

            try
            {
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return string.Empty;
        }

        var encoding = request.ContentEncoding ?? _options.ResponseEncoding ?? Encoding.UTF8;

        if (_options.MaxRequestBodySize > 0 && request.ContentLength64 > _options.MaxRequestBodySize && request.ContentLength64 != -1)
        {
            throw new InvalidOperationException(
                $"Request body exceeded the allowed size of {_options.MaxRequestBodySize} bytes.");
        }

        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;

        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (_options.MaxRequestBodySize > 0 && total > _options.MaxRequestBodySize)
            {
                throw new InvalidOperationException(
                    $"Request body exceeded the allowed size of {_options.MaxRequestBodySize} bytes.");
            }

            memory.Write(buffer, 0, read);
        }

        return encoding.GetString(memory.ToArray());
    }

    private Task WriteResponseAsync(
        HttpListenerContext context,
        HttpStatusCode statusCode,
        string message,
        string? contentType = null)
    {
        var encoding = _options.ResponseEncoding ?? Encoding.UTF8;
        var payload = encoding.GetBytes(message ?? string.Empty);

        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = contentType ?? "text/plain";
        context.Response.ContentEncoding = encoding;
        context.Response.ContentLength64 = payload.Length;

        return context.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
    }

    private static IReadOnlyDictionary<string, string> CopyHeaders(WebHeaderCollection headers)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in headers)
        {
            if (key == null)
            {
                continue;
            }

            dictionary[key] = headers[key] ?? string.Empty;
        }

        return dictionary;
    }

    private static bool ConstantTimeEquals(string left, string right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length)
        {
            return false;
        }

        var result = 0;
        for (var i = 0; i < leftBytes.Length; i++)
        {
            result |= leftBytes[i] ^ rightBytes[i];
        }

        return result == 0;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ServerGetLpr));
        }
    }
}