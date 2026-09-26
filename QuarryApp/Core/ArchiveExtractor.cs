using System.IO;
using System.IO.Compression;

namespace QuarryApp.Core;

/// <summary>
/// Embedded zero-dependency archive extractor for .zip and compressed files.
/// Automatically uncompresses downloaded archives into organized destination folders.
/// </summary>
public static class ArchiveExtractor
{
    public static bool IsSupportedArchive(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".zip" || ext == ".tar" || ext == ".gz";
    }

    public static bool IsArchive(string filePath) => IsSupportedArchive(filePath);

    public static void Extract(string archivePath, string destinationDir)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Archive not found", archivePath);
        Directory.CreateDirectory(destinationDir);
        ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);
    }

    public static bool TryExtractZip(string zipFilePath, string destinationDir, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            Extract(zipFilePath, destinationDir);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
