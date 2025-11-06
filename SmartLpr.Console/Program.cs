using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SmartLpr.Core;

namespace SmartLpr.ConsoleApp;

internal static class Program
{
    private static int Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        if (!string.IsNullOrEmpty(options.Error))
        {
            PrintError(options.Error);
            PrintUsage();
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        var detectorOptions = new LicensePlateDetectorOptions
        {
            DirectorySearchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly,
            UseSidecarText = options.UseSidecarMetadata,
            UseJsonMetadata = options.UseSidecarMetadata,
            UseFileNameHeuristics = options.UseFileNameHeuristics
        };

        if (options.MaxPerFile.HasValue)
        {
            detectorOptions.MaxResultsPerFile = options.MaxPerFile.Value;
        }

        var detector = new SmartLprDetector(detectorOptions);
        List<LicensePlateDetectionResult> results;
        try
        {
            results = detector.Detect(options.InputPath!).ToList();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException)
        {
            PrintError(ex.Message);
            return 2;
        }

        if (results.Count == 0)
        {
            Console.WriteLine("No license plates detected.");
        }
        else
        {
            switch (options.OutputFormat)
            {
                case OutputFormat.Json:
                    Console.WriteLine(SerializeToJson(results));
                    break;
                default:
                    PrintTextResults(results);
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            try
            {
                var json = SerializeToJson(results);
                File.WriteAllText(options.OutputPath!, json, Encoding.UTF8);
                Console.WriteLine($"Results saved to '{options.OutputPath}'.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PrintError($"Failed to save output file: {ex.Message}");
                return 3;
            }
        }

        return 0;
    }

    private static void PrintTextResults(IReadOnlyCollection<LicensePlateDetectionResult> results)
    {
        var groups = results
            .GroupBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var culture = CultureInfo.InvariantCulture;
        foreach (var group in groups)
        {
            Console.WriteLine(group.Key);
            foreach (var result in group.OrderByDescending(r => r.Confidence))
            {
                Console.WriteLine(
                    "  - {0} (confidence {1}, origin {2})",
                    result.Plate,
                    result.Confidence.ToString("P1", culture),
                    result.Origin);
            }

            Console.WriteLine();
        }

        Console.WriteLine("Total: {0} plate(s) detected.", results.Count);
    }

    private static string SerializeToJson(IEnumerable<LicensePlateDetectionResult> results)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        var firstResult = true;
        foreach (var result in results)
        {
            if (!firstResult)
            {
                sb.Append(',');
            }

            firstResult = false;
            sb.Append('{');
            var firstProperty = true;
            AppendJsonString(sb, "source", result.SourcePath, ref firstProperty);
            AppendJsonString(sb, "plate", result.Plate, ref firstProperty);
            AppendJsonNumber(sb, "confidence", result.Confidence, ref firstProperty);
            AppendJsonString(sb, "origin", result.Origin, ref firstProperty);

            if (!firstProperty)
            {
                sb.Append(',');
            }

            if (result.Bounds.HasValue)
            {
                sb.Append("\"bounds\":{");
                var bounds = result.Bounds.Value;
                var boundsFirst = true;
                AppendJsonNumber(sb, "x", bounds.X, ref boundsFirst);
                AppendJsonNumber(sb, "y", bounds.Y, ref boundsFirst);
                AppendJsonNumber(sb, "width", bounds.Width, ref boundsFirst);
                AppendJsonNumber(sb, "height", bounds.Height, ref boundsFirst);
                sb.Append('}');
            }
            else
            {
                sb.Append("\"bounds\":null");
            }

            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static void AppendJsonString(StringBuilder sb, string propertyName, string value, ref bool firstProperty)
    {
        if (!firstProperty)
        {
            sb.Append(',');
        }

        firstProperty = false;
        sb.Append('"').Append(propertyName).Append("\":\"").Append(EscapeJson(value)).Append('"');
    }

    private static void AppendJsonNumber(StringBuilder sb, string propertyName, double value, ref bool firstProperty)
    {
        if (!firstProperty)
        {
            sb.Append(',');
        }

        firstProperty = false;
        sb.Append('"').Append(propertyName).Append("\":").Append(value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static string EscapeJson(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\"':
                    sb.Append("\\\"");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SmartLpr console application");
        Console.WriteLine("Usage:");
        Console.WriteLine("  SmartLpr.Console --input <path> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -i, --input <path>         Path to an image file or directory.");
        Console.WriteLine("  -o, --output <path>        Optional path to store results as JSON.");
        Console.WriteLine("  -f, --format <text|json>   Controls console output format. Default is text.");
        Console.WriteLine("  -r, --recursive            Recursively process directories.");
        Console.WriteLine("      --max-per-file <n>     Limit plates returned per file (default {0}).", LicensePlateDetectorOptions.DefaultMaxResultsPerFile);
        Console.WriteLine("      --no-sidecar           Ignore sidecar metadata files (.txt/.json).");
        Console.WriteLine("      --no-filename          Disable filename heuristics.");
        Console.WriteLine("  -h, --help                 Display this help message.");
        Console.WriteLine();
    }

    private static void PrintError(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
