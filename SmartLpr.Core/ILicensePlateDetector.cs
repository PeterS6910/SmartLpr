using System.Collections.Generic;
using System.Threading;

namespace SmartLpr.Core;

/// <summary>
/// Represents a detector capable of extracting license plate values from inputs.
/// </summary>
public interface ILicensePlateDetector
{
    /// <summary>
    /// Detects license plate values from a single path (image file or directory).
    /// </summary>
    IEnumerable<LicensePlateDetectionResult> Detect(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects license plate values from multiple independent paths.
    /// </summary>
    IEnumerable<LicensePlateDetectionResult> Detect(IEnumerable<string> paths, CancellationToken cancellationToken = default);
}
