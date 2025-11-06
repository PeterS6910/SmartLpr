using System;
using System.Drawing;

namespace SmartLpr.Core;

/// <summary>
/// Represents a single detection result produced by the SmartLpr detector.
/// </summary>
public sealed class LicensePlateDetectionResult
{
    public LicensePlateDetectionResult(string sourcePath, string plate, double confidence, Rectangle? bounds, string origin)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(sourcePath));
        }

        if (string.IsNullOrWhiteSpace(plate))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(plate));
        }

        SourcePath = sourcePath;
        Plate = plate;
        Confidence = Math.Max(0d, Math.Min(1d, confidence));
        Bounds = bounds;
        Origin = string.IsNullOrWhiteSpace(origin) ? "unknown" : origin;
    }

    /// <summary>
    /// Gets the path of the input (image or video frame) that produced the detection.
    /// </summary>
    public string SourcePath { get; }

    /// <summary>
    /// Gets the normalised plate text (upper case alphanumeric string).
    /// </summary>
    public string Plate { get; }

    /// <summary>
    /// Gets a confidence metric between 0 and 1.
    /// </summary>
    public double Confidence { get; }

    /// <summary>
    /// Gets the bounding box of the detected plate if available.
    /// </summary>
    public Rectangle? Bounds { get; }

    /// <summary>
    /// Gets the origin / strategy that produced the detection (filename heuristic, metadata, OCR, ...).
    /// </summary>
    public string Origin { get; }

    public override string ToString()
    {
        return $"{Plate} ({Confidence:P0}) from {Origin}";
    }
}
