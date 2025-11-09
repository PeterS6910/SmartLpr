using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartLprDiscovery2
{
    internal static class Program
    {
        private const string AddressPrefix = "192.168.1.2";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(500);

        private static async Task Main()
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = RequestTimeout;

                Console.WriteLine("Zacina prehladavanie SmartLpr kamier v rozsahu 192.168.1.200-192.168.1.255...");

                for (var suffix = 0; suffix <= 99; suffix++)
                {
                    var address = string.Format("{0}{1:D2}", AddressPrefix, suffix);

                    IPAddress ip;
                    if (!IPAddress.TryParse(address, out ip))
                    {
                        continue;
                    }

                    var handled = await TryHandleAddressAsync(httpClient, address);
                    if (handled)
                    {
                        return;
                    }
                }

                Console.WriteLine("SmartLpr kamera sa nenasla.");
            }
        }

        private static async Task<bool> TryHandleAddressAsync(HttpClient httpClient, string address)
        {
            try
            {
                using (var cts = new CancellationTokenSource(RequestTimeout))
                using (var response = await httpClient.GetAsync("https://" + address + "/status", cts.Token))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return false;
                    }

                    // V .NET 4.7.2 nie je overload s CancellationToken
                    var payload = await response.Content.ReadAsStringAsync();

                    string formattedPayload;
                    if (!IsSmartLprResponse(payload, out formattedPayload))
                    {
                        return false;
                    }

                    Console.WriteLine("SmartLpr kamera bola detegovana na adrese {0}:", address);
                    Console.WriteLine(formattedPayload);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout - kamera neodpovedala do 0.5 sekundy.
                return false;
            }
            catch (HttpRequestException)
            {
                // Ziadna odpoved alebo sietovy problem - pokracujeme v hladani.
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
