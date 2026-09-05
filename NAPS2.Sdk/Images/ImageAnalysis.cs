using NAPS2.Images.Bitwise;

namespace NAPS2.Images;

/// <summary>
/// Exposes the image analysis operations used by the scanning pipeline.
/// </summary>
public static class ImageAnalysis
{
    /// <summary>
    /// Gets the detected skew angle in degrees for the supplied image.
    /// </summary>
    public static double GetSkewAngle(IMemoryImage image)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        return Deskewer.GetSkewAngle(image);
    }

    /// <summary>
    /// Analyzes how much non-white content is present in an image.
    /// </summary>
    /// <param name="image">The image to inspect.</param>
    /// <param name="whiteThreshold">The white threshold, from 0 to 100.</param>
    /// <param name="coverageThreshold">The non-white coverage threshold, from 0 to 100.</param>
    public static BlankPageAnalysis AnalyzeBlankPage(IMemoryImage image, int whiteThreshold, int coverageThreshold)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));

        var operation = new BlankDetectionImageOp(whiteThreshold, coverageThreshold);
        operation.Perform(image);
        return new BlankPageAnalysis(operation.IsBlank, operation.Coverage);
    }
}

/// <summary>
/// The result of a blank-page analysis.
/// </summary>
public sealed record BlankPageAnalysis(bool IsBlank, double Coverage);
