// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Tript.Detection;

namespace Tript.App.Models;

internal static class GameModelPackage
{
    internal const long MaximumPackageBytes = 1024L * 1024 * 1024;

    private const long MaximumExtractedBytes = 2L * 1024 * 1024 * 1024;

    internal static bool IsCompatible(GameModelRelease release, Version appVersion)
    {
        if (release.ModelApiVersion != ModelApiV1Compatibility.Version || release.Revision <= 0 ||
            release.SizeBytes <= 0 || release.SizeBytes > MaximumPackageBytes ||
            !Uri.TryCreate(release.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)) ||
            release.Sha256.Length != 64)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(release.MinimumAppVersion) ||
            (Version.TryParse(release.MinimumAppVersion, out var minimum) && appVersion >= minimum);
    }

    internal static async Task DownloadAsync(HttpClient http, GameModelRelease release, string archivePath,
        Action<long> reportProgress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.SizeBytes)
            throw new InvalidDataException("The model package size does not match the manifest.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long received = 0;
        long lastReported = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            received += read;
            if (received > release.SizeBytes || received > MaximumPackageBytes)
                throw new InvalidDataException("The model package exceeded its declared size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (received - lastReported >= 256 * 1024 || received == release.SizeBytes)
            {
                lastReported = received;
                reportProgress(received);
            }
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await output.DisposeAsync().ConfigureAwait(false);
        if (received != release.SizeBytes)
            throw new InvalidDataException("The downloaded model package was incomplete.");
        var archiveHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(archiveHash), Convert.FromHexString(release.Sha256)))
        {
            throw new InvalidDataException("The model package checksum did not match the manifest.");
        }
    }

    internal static GameModelPackageMetadata ExtractAndVerify(string archivePath, string stagingPath,
        string gameId, GameModelRelease release)
    {
        Directory.CreateDirectory(stagingPath);
        ExtractPackage(archivePath, stagingPath);
        var package = ModelJsonFiles.Read<GameModelPackageMetadata>(Path.Combine(stagingPath, "package.json"))
            ?? throw new InvalidDataException("The model package has no package.json.");
        ValidatePackageIdentity(gameId, release, package);
        ValidatePackageFiles(stagingPath, package);

        var definitions = JsonSerializer.Deserialize<List<EventDefinition>>(
            File.ReadAllText(Path.Combine(stagingPath, "events.json")), ModelJsonFiles.Options) ?? [];
        var hasObjectEvents = definitions.Any(definition => definition.DetectionKind == DetectionKind.Object);
        if (hasObjectEvents)
        {
            var objectModelPath = Path.Combine(stagingPath, "model.onnx");
            if (!File.Exists(objectModelPath))
                throw new InvalidDataException("The model package has object events but no object model.");
            var metadata = OnnxModelInspector.Inspect(objectModelPath);
            var mismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
            if (mismatch is not null)
                throw new InvalidDataException($"The model package is incompatible: {mismatch}");
        }
        var hasOcrEvents = definitions.Any(definition => definition.DetectionKind == DetectionKind.Ocr);
        if (hasOcrEvents && (!File.Exists(Path.Combine(stagingPath, "ocr_model.onnx"))
            || !File.Exists(Path.Combine(stagingPath, "ocr_dict.txt"))))
        {
            throw new InvalidDataException("The model package has OCR events but no OCR model and dictionary.");
        }

        return package;
    }

    internal static InstalledGameModel InstalledRecord(string gameId, GameModelRelease release,
        GameModelPackageMetadata package, DateTimeOffset installedAt) =>
        new()
        {
            GameId = gameId,
            ModelApiVersion = release.ModelApiVersion,
            Revision = release.Revision,
            PackageSha256 = release.Sha256,
            ModelSha256 = package.Files.GetValueOrDefault("model.onnx")?.Sha256,
            EventsSha256 = package.Files["events.json"].Sha256,
            OcrModelSha256 = package.Files.GetValueOrDefault("ocr_model.onnx")?.Sha256,
            OcrDetectorSha256 = package.Files.GetValueOrDefault("ocr_detector.onnx")?.Sha256,
            OcrDictionarySha256 = package.Files.GetValueOrDefault("ocr_dict.txt")?.Sha256,
            InstalledAt = installedAt,
        };

    internal static bool IsInstalledHealthy(string modelsRoot, string gameId, InstalledGameModel installed)
    {
        if (!string.Equals(installed.GameId, gameId, StringComparison.OrdinalIgnoreCase) ||
            installed.ModelApiVersion != ModelApiV1Compatibility.Version || installed.Revision <= 0 ||
            (installed.ModelSha256 is not null && installed.ModelSha256.Length != 64)
            || installed.EventsSha256.Length != 64)
        {
            return false;
        }

        var gamePath = Path.Combine(modelsRoot, gameId);
        var modelPath = Path.Combine(gamePath, "model.onnx");
        var eventsPath = Path.Combine(gamePath, "events.json");
        var ocrModelPath = Path.Combine(gamePath, "ocr_model.onnx");
        var ocrDetectorPath = Path.Combine(gamePath, "ocr_detector.onnx");
        var ocrDictionaryPath = Path.Combine(gamePath, "ocr_dict.txt");
        var ocrHealthy = installed.OcrModelSha256 is null && installed.OcrDictionarySha256 is null
            || installed.OcrModelSha256 is not null && installed.OcrDictionarySha256 is not null
                && File.Exists(ocrModelPath) && File.Exists(ocrDictionaryPath)
                && string.Equals(HashFile(ocrModelPath), installed.OcrModelSha256,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(HashFile(ocrDictionaryPath), installed.OcrDictionarySha256,
                    StringComparison.OrdinalIgnoreCase);
        var ocrDetectorHealthy = installed.OcrDetectorSha256 is null
            || File.Exists(ocrDetectorPath) && string.Equals(HashFile(ocrDetectorPath), installed.OcrDetectorSha256,
                StringComparison.OrdinalIgnoreCase);
        var objectHealthy = installed.ModelSha256 is null
            || File.Exists(modelPath) && string.Equals(HashFile(modelPath), installed.ModelSha256,
                StringComparison.OrdinalIgnoreCase);
        return File.Exists(eventsPath) && objectHealthy && ocrHealthy && ocrDetectorHealthy &&
            string.Equals(HashFile(eventsPath), installed.EventsSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractPackage(string archivePath, string stagingPath)
    {
        var required = new HashSet<string>(["events.json", "package.json"], StringComparer.Ordinal);
        var allowed = new HashSet<string>(required, StringComparer.Ordinal)
        {
            "model.onnx",
            "ocr_detector.onnx",
            "ocr_model.onnx",
            "ocr_dict.txt",
        };

        var modelGraphs = new HashSet<string>(
            ["model.onnx", "ocr_model.onnx", "ocr_detector.onnx"], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(archivePath);
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (!allowed.Contains(entry.FullName) || entry.Name != entry.FullName)
                throw new InvalidDataException($"Unexpected model package entry '{entry.FullName}'.");
            if (!seen.Add(entry.FullName))
                throw new InvalidDataException($"Duplicate model package entry '{entry.FullName}'.");
            required.Remove(entry.FullName);
            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaximumExtractedBytes ||
                (!modelGraphs.Contains(entry.Name) && entry.Length > 10 * 1024 * 1024))
            {
                throw new InvalidDataException("The model package expands beyond its allowed size.");
            }
            entry.ExtractToFile(Path.Combine(stagingPath, entry.Name));
        }
        if (required.Count > 0)
            throw new InvalidDataException($"The model package is missing {string.Join(", ", required)}.");
    }

    private static void ValidatePackageIdentity(string gameId, GameModelRelease release,
        GameModelPackageMetadata package)
    {
        if (package.PackageFormatVersion != 1 ||
            !string.Equals(package.GameId, gameId, StringComparison.OrdinalIgnoreCase) ||
            package.ModelApiVersion != release.ModelApiVersion || package.Revision != release.Revision)
        {
            throw new InvalidDataException("The model package identity does not match the manifest.");
        }
    }

    private static void ValidatePackageFiles(string stagingPath, GameModelPackageMetadata package)
    {
        var actual = Directory.EnumerateFiles(stagingPath)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name != "package.json")
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(package.Files.Keys))
            throw new InvalidDataException("The model package file metadata does not match its payload entries.");
        if (!package.Files.ContainsKey("events.json"))
            throw new InvalidDataException("The model package has no events.json metadata.");
        foreach (var name in package.Files.Keys)
        {
            if (name is not ("model.onnx" or "events.json" or "ocr_detector.onnx" or "ocr_model.onnx" or "ocr_dict.txt"))
                throw new InvalidDataException($"The model package contains unsupported metadata for {name}.");
            if (!package.Files.TryGetValue(name, out var expected) || expected.SizeBytes <= 0 ||
                expected.Sha256.Length != 64)
            {
                throw new InvalidDataException($"The model package has no valid metadata for {name}.");
            }
            var path = Path.Combine(stagingPath, name);
            var info = new FileInfo(path);
            if (info.Length != expected.SizeBytes ||
                !string.Equals(HashFile(path), expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The model package file {name} failed verification.");
            }
        }
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
