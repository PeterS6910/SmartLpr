using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace SmartLpr.Core;

/// <summary>
/// Default implementation of the SmartLpr detector. It supports three strategies:
/// sidecar metadata (.txt / .json files), filename heuristics and optional de-duplication.
/// </summary>
public sealed class SmartLprDetector : ILicensePlateDetector
{
    private static readonly Regex PlatePattern = new(
        @"(?<![A-Z0-9])([A-Z0-9]{2,3}[- _]?[A-Z0-9]{3,4})(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly LicensePlateDetectorOptions _options;

    public SmartLprDetector()
        : this(new LicensePlateDetectorOptions())
    {
    }

    public SmartLprDetector(LicensePlateDetectorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IEnumerable<LicensePlateDetectionResult> Detect(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Input path cannot be null or whitespace.", nameof(path));
        }

        if (Directory.Exists(path))
        {
            foreach (var result in DetectDirectory(path, cancellationToken))
            {
                yield return result;
            }

            yield break;
        }

        if (File.Exists(path))
        {
            foreach (var result in DetectFileInternal(path, cancellationToken))
            {
                yield return result;
            }

            yield break;
        }

        throw new FileNotFoundException($"Input path '{path}' was not found.", path);
    }

    public IEnumerable<LicensePlateDetectionResult> Detect(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths == null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var result in Detect(path, cancellationToken))
            {
                yield return result;
            }
        }
    }

    private IEnumerable<LicensePlateDetectionResult> DetectDirectory(string directory, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.*", _options.DirectorySearchOption))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_options.IsSupportedExtension(Path.GetExtension(file)))
            {
                continue;
            }

            foreach (var result in DetectFileInternal(file, cancellationToken))
            {
                yield return result;
            }
        }
    }

    private IEnumerable<LicensePlateDetectionResult> DetectFileInternal(string filePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath);
        if (!_options.IsSupportedExtension(extension))
        {
            yield break;
        }

        ValidateImage(filePath);

        var emittedCount = 0;
        HashSet<string>? uniquePlates = null;
        if (_options.DeduplicateResults)
        {
            uniquePlates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var result in EnumerateSidecarResults(filePath, cancellationToken))
        {
            if (ShouldEmit(result.Plate, uniquePlates))
            {
                yield return result;
                emittedCount++;
            }

            if (emittedCount >= _options.MaxResultsPerFile)
            {
                yield break;
            }
        }

        foreach (var result in EnumerateFileNameResults(filePath))
        {
            if (ShouldEmit(result.Plate, uniquePlates))
            {
                yield return result;
                emittedCount++;
            }

            if (emittedCount >= _options.MaxResultsPerFile)
            {
                yield break;
            }
        }
    }

    private static void ValidateImage(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is OutOfMemoryException)
        {
            throw new InvalidDataException($"Input '{filePath}' is not a valid image.", ex);
        }
    }

    private IEnumerable<LicensePlateDetectionResult> EnumerateSidecarResults(string filePath, CancellationToken cancellationToken)
    {
        if (_options.UseSidecarText)
        {
            var textPath = Path.ChangeExtension(filePath, ".txt");
            foreach (var result in EnumerateFromTextFile(filePath, textPath, 0.95, "sidecar-text", cancellationToken))
            {
                yield return result;
            }
        }

        if (_options.UseJsonMetadata)
        {
            var jsonPath = Path.ChangeExtension(filePath, ".json");
            foreach (var result in EnumerateFromTextFile(filePath, jsonPath, 0.9, "metadata-json", cancellationToken))
            {
                yield return result;
            }
        }
    }

    private IEnumerable<LicensePlateDetectionResult> EnumerateFromTextFile(
        string sourceImage,
        string metadataPath,
        double confidence,
        string origin,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(metadataPath) || !File.Exists(metadataPath))
        {
            yield break;
        }

        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(metadataPath);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var candidate in ExtractNormalizedCandidates(line))
            {
                yield return CreateResult(sourceImage, candidate, confidence, origin);
            }
        }
    }

    private IEnumerable<LicensePlateDetectionResult> EnumerateFileNameResults(string filePath)
    {
        if (!_options.UseFileNameHeuristics)
        {
            yield break;
        }

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            yield break;
        }

        foreach (var candidate in ExtractNormalizedCandidates(fileName))
        {
            yield return CreateResult(filePath, candidate, 0.6, "file-name");
        }
    }

    private static bool ShouldEmit(string plate, HashSet<string>? uniquePlates)
    {
        if (uniquePlates == null)
        {
            return true;
        }

        return uniquePlates.Add(plate);
    }

    private static IEnumerable<string> ExtractNormalizedCandidates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in PlatePattern.Matches(text))
        {
            var normalized = NormalizePlate(match.Groups[1].Value);
            if (normalized != null)
            {
                yield return normalized;
            }
        }
    }

    private static string? NormalizePlate(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
        {
            return null;
        }

        var filtered = new string(plate.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (filtered.Length < 4 || filtered.Length > 10)
        {
            return null;
        }

        return filtered;
    }

    private static LicensePlateDetectionResult CreateResult(string sourcePath, string plate, double confidence, string origin)
    {
        return new LicensePlateDetectionResult(sourcePath, plate, confidence, bounds: null, origin);
    }
}
