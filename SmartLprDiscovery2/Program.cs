using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SmartLprDiscovery2
{

    internal static class Program
    {
        private static readonly Regex SerialSuffixPattern = new Regex("^[0-9]{1,2}$", RegexOptions.Compiled);

        private static async Task<int> Main(string[] args)
        {
            Options options;
            try
            {
                options = ParseOptions(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                PrintUsage();
                return 1;
            }

            if (options.SerialSuffixes.Count == 0)
            {
                Console.WriteLine("Zadajte posledné dve číslice sériového čísla jednotlivých SmartLPR kamier (oddelené medzerou):");
                var input = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(input))
                {
                    options.SerialSuffixes.AddRange(
                        input.Split(new[] { ' ', '\t', ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                }
            }

            if (options.SerialSuffixes.Count == 0)
            {
                Console.Error.WriteLine("Nie sú zadané žiadne sériové čísla.");
                PrintUsage();
                return 1;
            }

            using (var handler = new HttpClientHandler())
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

                using (var httpClient = new HttpClient(handler))
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

                    Console.WriteLine(
                        $"Vyhľadávam SmartLPR kamery na https://192.168.1.2XX:8080/api/v2/status (timeout {options.TimeoutSeconds} s)...");

                    using (var cancellationSource = new CancellationTokenSource())
                    {
                        foreach (var suffix in options.SerialSuffixes)
                        {
                            await QueryCameraAsync(httpClient, suffix, options, cancellationSource.Token);
                        }
                    }
                }
            }

            return 0;
        }



        private static Options ParseOptions(string[] args)
        {
            var options = new Options();

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument)
                {
                    case "--statistics":
                        options.IncludeStatistics = true;
                        break;
                    case "--alarms":
                        options.IncludeAlarms = true;
                        break;
                    case "--timeout":
                        if (index + 1 >= args.Length)
                        {
                            throw new ArgumentException("Prepínač --timeout vyžaduje hodnotu v sekundách.");
                        }

                        var timeoutArgument = args[++index];
                        if (!double.TryParse(timeoutArgument, NumberStyles.Float, CultureInfo.InvariantCulture, out var timeoutSeconds) || timeoutSeconds <= 0)
                        {
                            throw new ArgumentException("Hodnota prepínača --timeout musí byť kladné číslo.");
                        }

                        options.TimeoutSeconds = timeoutSeconds;
                        break;
                    case "--help":
                    case "-h":
                    case "-?":
                        throw new ArgumentException(string.Empty);
                    default:
                        options.SerialSuffixes.Add(argument);
                        break;
                }
            }

            return options;
        }

        private static async Task QueryCameraAsync(HttpClient httpClient, string suffix, Options options, CancellationToken cancellationToken)
        {
            var trimmedSuffix = suffix.Trim();
            if (!SerialSuffixPattern.IsMatch(trimmedSuffix))
            {
                Console.WriteLine($"[{suffix}] Preskočené – očakávané sú 1 až 2 číslice sériového čísla.");
                return;
            }

            var ipSuffix = trimmedSuffix.PadLeft(2, '0');
            var baseAddress = $"https://192.168.1.2{ipSuffix}:8080/";
            var statusUri = new Uri(new Uri(baseAddress), "api/v2/status");

            Console.WriteLine($"\n[{ipSuffix}] Kontrolujem {statusUri}...");
            var statusOutcome = await QueryEndpointAsync(httpClient, statusUri, cancellationToken);

            if (!statusOutcome.Success)
            {
                Console.WriteLine($"[{ipSuffix}] Nepodarilo sa načítať status: {statusOutcome.Message}");
                if (!string.IsNullOrWhiteSpace(statusOutcome.RawContent))
                {
                    Console.WriteLine($"[{ipSuffix}] Odpoveď:\n{statusOutcome.RawContent}");
                }
                return;
            }

            if (!LooksLikeSmartLpr(statusOutcome.Payload))
            {
                Console.WriteLine($"[{ipSuffix}] Odpoveď nevyzerá ako SmartLPR status. JSON:\n{FormatJson(statusOutcome.Payload)}");
                return;
            }

            Console.WriteLine($"[{ipSuffix}] SmartLPR kamera nájdená na {baseAddress}");
            PrintStatusSummary(ipSuffix, statusOutcome.Payload);

            var versionUri = new Uri(new Uri(baseAddress), "api/v2/version");
            var versionOutcome = await QueryEndpointAsync(httpClient, versionUri, cancellationToken);
            if (versionOutcome.Success)
            {
                PrintVersionSummary(ipSuffix, versionOutcome.Payload);
            }
            else
            {
                Console.WriteLine($"[{ipSuffix}] Nepodarilo sa načítať verziu: {versionOutcome.Message}");
                if (!string.IsNullOrWhiteSpace(versionOutcome.RawContent))
                {
                    Console.WriteLine($"[{ipSuffix}] Odpoveď:\n{versionOutcome.RawContent}");
                }
            }

            if (options.IncludeStatistics)
            {
                await PrintOptionalEndpointAsync(httpClient, baseAddress, "api/v2/statistics", ipSuffix, cancellationToken);
            }

            if (options.IncludeAlarms)
            {
                await PrintOptionalEndpointAsync(httpClient, baseAddress, "api/v2/alarms", ipSuffix, cancellationToken);
            }
        }

        private static async Task PrintOptionalEndpointAsync(HttpClient httpClient, string baseAddress, string relativePath, string ipSuffix, CancellationToken cancellationToken)
        {
            var endpointUri = new Uri(new Uri(baseAddress), relativePath);
            var outcome = await QueryEndpointAsync(httpClient, endpointUri, cancellationToken);
            if (outcome.Success)
            {
                Console.WriteLine($"[{ipSuffix}] {relativePath} odpoveď:\n{FormatJson(outcome.Payload)}");
            }
            else
            {
                Console.WriteLine($"[{ipSuffix}] Nepodarilo sa načítať {relativePath}: {outcome.Message}");
                if (!string.IsNullOrWhiteSpace(outcome.RawContent))
                {
                    Console.WriteLine($"[{ipSuffix}] Odpoveď:\n{outcome.RawContent}");
                }
            }
        }

        private static async Task<QueryOutcome> QueryEndpointAsync(HttpClient httpClient, Uri uri, CancellationToken cancellationToken)
        {
            try
            {
                using (var response = await httpClient.GetAsync(uri, cancellationToken))
                {
                    var message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    var rawContent = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        return new QueryOutcome(false, default, message, response.StatusCode, rawContent);
                    }

                    try
                    {
                        using (var document = JsonDocument.Parse(rawContent))
                        {
                            return new QueryOutcome(
                                true,
                                document.RootElement.Clone(),
                                message,
                                response.StatusCode,
                                rawContent);
                        }
                    }
                    catch (JsonException jsonException)
                    {
                        var errorMessage = $"{message} – odpoveď nie je platný JSON ({jsonException.Message}).";
                        return new QueryOutcome(false, default, errorMessage, response.StatusCode, rawContent);
                    }
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new QueryOutcome(false, default, "Vypršal časový limit požiadavky.", null, null);
            }
            catch (HttpRequestException ex)
            {
                return new QueryOutcome(false, default, ex.Message, null, null);
            }
        }


        private static void PrintStatusSummary(string ipSuffix, JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine($"[{ipSuffix}] Neočakávaná štruktúra statusu:\n{FormatJson(payload)}");
                return;
            }

            if (payload.TryGetProperty("status", out var statusProperty))
            {
                switch (statusProperty.ValueKind)
                {
                    case JsonValueKind.String:
                        Console.WriteLine($"[{ipSuffix}] Stav: {statusProperty.GetString()}");
                        break;
                    case JsonValueKind.Object when statusProperty.TryGetProperty("state", out var stateProperty) && stateProperty.ValueKind == JsonValueKind.String:
                        Console.WriteLine($"[{ipSuffix}] Stav: {stateProperty.GetString()}");
                        break;
                    default:
                        Console.WriteLine($"[{ipSuffix}] Status JSON:\n{FormatJson(statusProperty)}");
                        break;
                }
            }

            if (payload.TryGetProperty("device", out var deviceProperty) && deviceProperty.ValueKind == JsonValueKind.Object)
            {
                if (deviceProperty.TryGetProperty("serialNumber", out var serial) && serial.ValueKind == JsonValueKind.String)
                {
                    Console.WriteLine($"[{ipSuffix}] Sériové číslo: {serial.GetString()}");
                }

                if (deviceProperty.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    Console.WriteLine($"[{ipSuffix}] Názov: {name.GetString()}");
                }
            }
        }

        private static void PrintVersionSummary(string ipSuffix, JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine($"[{ipSuffix}] Verzia – neznáma štruktúra:\n{FormatJson(payload)}");
                return;
            }

            PrintStringProperty(ipSuffix, payload, "firmware", "Verzia firmvéru");
            PrintStringProperty(ipSuffix, payload, "software", "Verzia softvéru");
            PrintStringProperty(ipSuffix, payload, "module", "Modul");

            JsonElement modules;
            if (payload.TryGetProperty("modules", out modules) && modules.ValueKind == JsonValueKind.Array)
            {
                Console.WriteLine($"[{ipSuffix}] Moduly:");
                foreach (var module in modules.EnumerateArray())
                {
                    Console.WriteLine(FormatModule(ipSuffix, module));
                }
            }
        }

        private static void PrintStringProperty(string suffix, JsonElement obj, string propertyName, string label)
        {
            JsonElement property;
            if (obj.TryGetProperty(propertyName, out property) && property.ValueKind == JsonValueKind.String)
            {
                Console.WriteLine($"[{suffix}] {label}: {property.GetString()}");
            }
        }


        private static string FormatModule(string ipSuffix, JsonElement module)
        {
            if (module.ValueKind != JsonValueKind.Object)
            {
                return $"[{ipSuffix}]  - {module}";
            }

            var name = module.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == JsonValueKind.String
                ? nameProperty.GetString()
                : null;
            var version = module.TryGetProperty("version", out var versionProperty) && versionProperty.ValueKind == JsonValueKind.String
                ? versionProperty.GetString()
                : null;

            return name is null && version is null
                ? $"[{ipSuffix}]  - {FormatJson(module)}"
                : $"[{ipSuffix}]  - {name ?? "(bez názvu)"} {version}";
        }

        private static bool LooksLikeSmartLpr(JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return payload.TryGetProperty("status", out _)
                || payload.TryGetProperty("device", out _)
                || payload.TryGetProperty("unit", out _)
                || payload.TryGetProperty("version", out _);
        }

        private static string FormatJson(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Undefined
                ? "(prázdna odpoveď)"
                : JsonSerializer.Serialize(element, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Použitie: SmartLprDiscovery [možnosti] [XX ...]

Možnosti:
  --statistics        Načíta aj diagnostické dáta z /api/v2/statistics.
  --alarms            Načíta alarmy z /api/v2/alarms.
  --timeout <sek>     Nastaví HTTP timeout (predvolene 5 sekúnd).
  --help              Zobrazí túto nápovedu.

Argumenty:
  XX                  Posledné dve číslice IP adresy (napr. 05 pre 192.168.1.205).
");
        }

        private sealed class Options
        {
            public List<string> SerialSuffixes { get; } = new List<string>();

            public bool IncludeStatistics { get; set; }

            public bool IncludeAlarms { get; set; }

            public double TimeoutSeconds { get; set; } = 5;
        }

        private sealed class QueryOutcome
        {
            public QueryOutcome(bool success, JsonElement payload, string message, HttpStatusCode? statusCode, string rawContent)
            {
                Success = success;
                Payload = payload;
                Message = message;
                StatusCode = statusCode;
                RawContent = rawContent;
            }

            public bool Success { get; }

            public JsonElement Payload { get; }

            public string Message { get; }

            public HttpStatusCode? StatusCode { get; }

            public string RawContent { get; }
        }
    }
}