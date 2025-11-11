using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace SmartLpr.Core;

/// <summary>
/// Configurable options for the <see cref="SmartLprDetector"/>.
/// </summary>
public sealed class LicensePlateDetectorOptions
{
    public const int DefaultMaxResultsPerFile = 5;

    public static readonly IReadOnlyCollection<string> DefaultSupportedExtensions =
        new ReadOnlyCollection<string>(new[]
        {
            ".bmp",
            ".gif",
            ".jpg",
            ".jpeg",
            ".png",
            ".tif",
            ".tiff"
        });

    private int _maxResultsPerFile = DefaultMaxResultsPerFile;
    private IReadOnlyCollection<string> _supportedExtensions = DefaultSupportedExtensions;

    /// <summary>
    /// Gets or sets the search option to use when a directory is provided.
    /// </summary>
    public SearchOption DirectorySearchOption { get; set; } = SearchOption.TopDirectoryOnly;

    /// <summary>
    /// Gets or sets a value indicating whether sidecar metadata (for example .txt files) should be used.
    /// </summary>
    public bool UseSidecarText { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether .json sidecar files are parsed for detections.
    /// </summary>
    public bool UseJsonMetadata { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether filename heuristics should be evaluated.
    /// </summary>
    public bool UseFileNameHeuristics { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether duplicate plate values should be removed.
    /// </summary>
    public bool DeduplicateResults { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of plates returned per file.
    /// </summary>
    public int MaxResultsPerFile
    {
        get => _maxResultsPerFile;
        set => _maxResultsPerFile = value <= 0 ? DefaultMaxResultsPerFile : value;
    }

    /// <summary>
    /// Gets or sets the supported file extensions.
    /// </summary>
    public IReadOnlyCollection<string> SupportedExtensions
    {
        get => _supportedExtensions;
        set => _supportedExtensions = value == null
            ? DefaultSupportedExtensions
            : new ReadOnlyCollection<string>(
                value.Select(v => v?.Trim().ToLowerInvariant())
                     .Where(v => !string.IsNullOrWhiteSpace(v))
                     .Distinct()
                     .Select(v => v.StartsWith(".") ? v : "." + v)
                     .ToArray());
    }

    internal bool IsSupportedExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        return _supportedExtensions.Contains(extension.ToLowerInvariant());
    }
}
