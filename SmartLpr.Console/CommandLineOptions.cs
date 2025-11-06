using System;
using System.Globalization;

namespace SmartLpr.ConsoleApp;

internal enum OutputFormat
{
    Text,
    Json
}

internal sealed class CommandLineOptions
{
    public string? InputPath { get; private set; }

    public string? OutputPath { get; private set; }

    public OutputFormat OutputFormat { get; private set; } = OutputFormat.Text;

    public bool Recursive { get; private set; }

    public int? MaxPerFile { get; private set; }

    public bool UseSidecarMetadata { get; private set; } = true;

    public bool UseFileNameHeuristics { get; private set; } = true;

    public bool ShowHelp { get; private set; }

    public string? Error { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        if (args == null || args.Length == 0)
        {
            options.ShowHelp = true;
            return options;
        }

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "-h":
                case "--help":
                case "-?":
                    options.ShowHelp = true;
                    return options;
                case "-i":
                case "--input":
                    if (!TryGetValue(args, ref index, out var input))
                    {
                        options.Error = "Missing value for --input.";
                        return options;
                    }

                    options.InputPath = input;
                    break;
                case "-o":
                case "--output":
                    if (!TryGetValue(args, ref index, out var output))
                    {
                        options.Error = "Missing value for --output.";
                        return options;
                    }

                    options.OutputPath = output;
                    break;
                case "-f":
                case "--format":
                    if (!TryGetValue(args, ref index, out var formatValue))
                    {
                        options.Error = "Missing value for --format.";
                        return options;
                    }

                    if (!TryParseFormat(formatValue, out var format))
                    {
                        options.Error = $"Unsupported output format '{formatValue}'.";
                        return options;
                    }

                    options.OutputFormat = format;
                    break;
                case "-r":
                case "--recursive":
                    options.Recursive = true;
                    break;
                case "--max-per-file":
                    if (!TryGetValue(args, ref index, out var maxValue))
                    {
                        options.Error = "Missing value for --max-per-file.";
                        return options;
                    }

                    if (!int.TryParse(maxValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMax) || parsedMax <= 0)
                    {
                        options.Error = "Value for --max-per-file must be a positive integer.";
                        return options;
                    }

                    options.MaxPerFile = parsedMax;
                    break;
                case "--no-sidecar":
                    options.UseSidecarMetadata = false;
                    break;
                case "--no-filename":
                    options.UseFileNameHeuristics = false;
                    break;
                default:
                    options.Error = $"Unknown argument '{argument}'.";
                    return options;
            }
        }

        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            options.Error = "Input path is required. Use --input <path>.";
        }

        return options;
    }

    private static bool TryGetValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool TryParseFormat(string value, out OutputFormat format)
    {
        if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
        {
            format = OutputFormat.Json;
            return true;
        }

        if (string.Equals(value, "text", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "table", StringComparison.OrdinalIgnoreCase))
        {
            format = OutputFormat.Text;
            return true;
        }

        format = OutputFormat.Text;
        return false;
    }
}
