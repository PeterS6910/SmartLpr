using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Headers;
using System.Text;
using System.Collections.Generic;

namespace SmartLprDiscovery2
{
    internal static class Program
    {
        private const string AddressPrefix = "192.168.1.2";
        private const int RestPort = 8080;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(200);

        private static async Task Main()
        {
            // TLS 1.2
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

            var handler = new HttpClientHandler();
            // kamera má self-signed certifikát – povolíme ho
            handler.ServerCertificateCustomValidationCallback =
                (msg, cert, chain, errors) => true;

            using (var httpClient = new HttpClient(handler))
            {
                httpClient.Timeout = RequestTimeout;

                // Basic auth – rovnaké meno/heslo ako máš v Postmane
                var bytes = Encoding.ASCII.GetBytes("admin:quercus2");
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));

                Console.WriteLine("Zacina prehladavanie SmartLpr kamier v rozsahu 192.168.1.200-192.168.1.255...");

                // sem budeme pridávať všetky nájdené IP kamery
                var foundCameras = new List<string>();

                for (var suffix = 0; suffix <= 54; suffix++)
                {
                    var address = string.Format("{0}{1:D2}", AddressPrefix, suffix);

                    IPAddress ip;
                    if (!IPAddress.TryParse(address, out ip))
                        continue;

                    var isCamera = await TryHandleAddressAsync(httpClient, address);
                    if (isCamera)
                    {
                        foundCameras.Add(address);
                    }
                }

                if (foundCameras.Count == 0)
                {
                    Console.WriteLine("SmartLpr kamera sa nenasla.");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("Najdene SmartLpr kamery ({0}):", foundCameras.Count);
                    foreach (var camIp in foundCameras)
                    {
                        Console.WriteLine(" - {0}", camIp);
                    }
                }
            }
        }

        private static async Task<bool> TryHandleAddressAsync(HttpClient httpClient, string address)
        {
            // presne to, čo voláš v Postmane:
            var url = string.Format("https://{0}:{1}/api/v2/status", address, RestPort);

            try
            {
                using (var response = await httpClient.GetAsync(url))
                {
                    Console.WriteLine("[{0}] HTTP {1} {2}",
                                      address, (int)response.StatusCode, response.ReasonPhrase);

                    if (!response.IsSuccessStatusCode)
                        return false;

                    var payload = await response.Content.ReadAsStringAsync();

                    string formattedPayload;
                    if (!IsSmartLprResponse(payload, out formattedPayload))
                        return false;

                    Console.WriteLine("SmartLpr kamera bola detegovana na adrese {0}:", address);
                    Console.WriteLine(formattedPayload);
                    return true;
                }
            }
            catch (TaskCanceledException)
            {
                // vypršal httpClient.Timeout
                Console.WriteLine("[{0}] Timeout po {1} ms", address, RequestTimeout.TotalMilliseconds);
                return false;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine("[{0}] HttpRequestException: {1}", address, ex.Message);
                return false;
            }
        }

        private static bool IsSmartLprResponse(string payload, out string formattedPayload)
        {
            try
            {
                using (var document = JsonDocument.Parse(payload))
                {
                    var root = document.RootElement;

                    JsonElement globalElement;
                    JsonElement lampElement;
                    JsonElement temperatureElement;

                    if (!root.TryGetProperty("global", out globalElement) ||
                        !root.TryGetProperty("lamp", out lampElement) ||
                        !root.TryGetProperty("temperature", out temperatureElement))
                    {
                        formattedPayload = string.Empty;
                        return false;
                    }

                    if ((globalElement.ValueKind != JsonValueKind.True && globalElement.ValueKind != JsonValueKind.False) ||
                        (lampElement.ValueKind != JsonValueKind.True && lampElement.ValueKind != JsonValueKind.False) ||
                        temperatureElement.ValueKind != JsonValueKind.Number)
                    {
                        formattedPayload = string.Empty;
                        return false;
                    }

                    formattedPayload = payload;
                    return true;
                }
            }
            catch (JsonException)
            {
                formattedPayload = string.Empty;
                return false;
            }
        }
    }
}
