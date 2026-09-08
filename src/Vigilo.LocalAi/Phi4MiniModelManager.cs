using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Vigilo.Core;

namespace Vigilo.LocalAi;

public sealed class Phi4MiniModelManager(
    ModelOptions options,
    IUserApprovalService approvalService,
    LocalAiActivityTracker activityTracker,
    ILogger<Phi4MiniModelManager> logger) : IModelManager
{
    private string _verifiedStateSignature = "";

    public async Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(options.Backend, ModelProfileCatalog.OpenAiCompatibleBackend, StringComparison.OrdinalIgnoreCase))
        {
            return await GetOpenAiCompatibleStatusAsync(cancellationToken);
        }

        ModelProfileCatalog.EnsureModelRoot(options);
        var missingFiles = options.RequiredFiles
            .Where(file => !File.Exists(Path.Combine(options.ModelRoot, file)))
            .ToList();
        var statePath = Path.Combine(options.ModelRoot, "vigilo-model-state.json");
        ModelState? state = null;
        if (missingFiles.Count == 0 && File.Exists(statePath))
        {
            try
            {
                await using var stateStream = File.OpenRead(statePath);
                state = await JsonSerializer.DeserializeAsync<ModelState>(stateStream, cancellationToken: cancellationToken);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Local model integrity state is corrupt. StatePath={StatePath}", statePath);
            }
        }

        if (missingFiles.Count == 0
            && (state is null
                || !state.Installed
                || !string.Equals(state.Version, options.Version, StringComparison.Ordinal)
                || state.FileSha256 is null
                || options.RequiredFiles.Any(file => !state.FileSha256.ContainsKey(file))))
        {
            missingFiles.Add("vigilo-model-state.json (integrity metadata)");
        }

        if (missingFiles.Count == 0 && state?.FileSha256 is not null)
        {
            var signature = CreateStateSignature(statePath);
            if (!string.Equals(signature, _verifiedStateSignature, StringComparison.Ordinal))
            {
                foreach (var file in options.RequiredFiles)
                {
                    var path = Path.Combine(options.ModelRoot, file);
                    var actualHash = await ComputeHashAsync(path, HashAlgorithmName.SHA256, cancellationToken);
                    if (!string.Equals(actualHash, state.FileSha256[file], StringComparison.OrdinalIgnoreCase))
                    {
                        missingFiles.Add($"{file} (checksum mismatch)");
                    }
                }

                if (missingFiles.Count == 0)
                {
                    _verifiedStateSignature = signature;
                }
            }
        }

        var status = new ModelStatus(
            missingFiles.Count == 0,
            options.ModelRoot,
            options.Version,
            missingFiles.Count == 0
                ? "Model is installed and its checksums are valid."
                : "Model files are missing or failed integrity validation. Use Download / repair model; emails remain pending for retry.",
            missingFiles);

        logger.LogDebug(
            "Local model status checked. Installed={Installed} ModelRoot={ModelRoot} Version={Version} MissingFileCount={MissingFileCount} MissingFiles={MissingFiles}",
            status.IsInstalled,
            status.ModelPath,
            status.Version,
                status.MissingFiles.Count,
                string.Join(';', status.MissingFiles));
        return status;
    }

    public async Task<ModelInstallResult> EnsureModelAsync(
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        if (string.Equals(options.Backend, ModelProfileCatalog.OpenAiCompatibleBackend, StringComparison.OrdinalIgnoreCase))
        {
            var status = await GetStatusAsync(cancellationToken);
            var message = status.IsInstalled
                ? $"{options.DisplayName} is available from the local LiteRT-LM server."
                : $"{options.DisplayName} requires LiteRT-LM running locally. {options.SetupInstructions}";
            return new ModelInstallResult(status.IsInstalled, message, status);
        }

        ModelProfileCatalog.EnsureModelRoot(options);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Local model ensure started. Preset={Preset} ModelRoot={ModelRoot} Version={Version} ManifestBaseUrl={ManifestBaseUrl}",
            options.Preset,
            options.ModelRoot,
            options.Version,
            options.ManifestBaseUrl);
        var current = await GetStatusAsync(cancellationToken);
        if (current.IsInstalled)
        {
            logger.LogInformation(
                "Local model ensure skipped because model is already installed. ModelRoot={ModelRoot} Version={Version}",
                current.ModelPath,
                current.Version);
            return new ModelInstallResult(true, "Model is already installed.", current);
        }

        var approved = await approvalService.ConfirmAsync(
            "Download local model",
            $"Vigilo needs to download the {options.DisplayName} model files to run local classification. This can be several gigabytes. Email contents are not sent anywhere.",
            cancellationToken);

        if (!approved)
        {
            await SaveStateAsync("Download not approved.", false, null, cancellationToken);
            logger.LogWarning(
                "Local model download was not approved. ModelRoot={ModelRoot} Version={Version}",
                options.ModelRoot,
                options.Version);
            return new ModelInstallResult(false, "Model download was not approved.", current);
        }

        Directory.CreateDirectory(options.ModelRoot);

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
            var fileHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in options.RequiredFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(options.ModelRoot, file);
                var url = new Uri(new Uri(options.ManifestBaseUrl), file);
                var remote = await GetRemoteFileMetadataAsync(httpClient, url, cancellationToken);
                if (File.Exists(target) && IsExistingFileUsable(target, remote.Length)
                    && await MatchesRemoteDigestAsync(target, remote, cancellationToken))
                {
                    logger.LogInformation(
                        "Skipping existing model file. File={File} Path={Path} LocalBytes={LocalBytes} RemoteBytes={RemoteBytes}",
                        file,
                        target,
                        new FileInfo(target).Length,
                        remote.Length);
                    fileHashes[file] = await ComputeHashAsync(target, HashAlgorithmName.SHA256, cancellationToken);
                    continue;
                }

                logger.LogInformation(
                    "Downloading model file. File={File} Url={Url} Target={Target} RemoteBytes={RemoteBytes}",
                    file,
                    url,
                    target,
                    remote.Length);
                progress?.Report($"Downloading {file}: 0%");
                var temporaryTarget = target + ".download";
                if (File.Exists(temporaryTarget))
                {
                    File.Delete(temporaryTarget);
                }

                try
                {
                    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var destination = File.Create(temporaryTarget);
                    await CopyWithProgressAsync(source, destination, file, remote.Length, progress, cancellationToken);
                    await destination.FlushAsync(cancellationToken);

                    if (!IsExistingFileUsable(temporaryTarget, remote.Length)
                        || !await MatchesRemoteDigestAsync(temporaryTarget, remote, cancellationToken))
                    {
                        throw new IOException($"Downloaded model file {file} failed integrity validation.");
                    }

                    fileHashes[file] = await ComputeHashAsync(temporaryTarget, HashAlgorithmName.SHA256, cancellationToken);
                    File.Move(temporaryTarget, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryTarget))
                    {
                        File.Delete(temporaryTarget);
                    }
                }
                logger.LogInformation(
                    "Downloaded model file. File={File} Target={Target} LocalBytes={LocalBytes}",
                    file,
                    target,
                    new FileInfo(target).Length);
            }

            await SaveStateAsync("Model is installed and its checksums are valid.", true, fileHashes, cancellationToken);
            _verifiedStateSignature = "";
            var status = await GetStatusAsync(cancellationToken);
            logger.LogInformation(
                "Local model ensure completed. Installed={Installed} ModelRoot={ModelRoot} MissingFileCount={MissingFileCount} ElapsedMilliseconds={ElapsedMilliseconds}",
                status.IsInstalled,
                status.ModelPath,
                status.MissingFiles.Count,
                stopwatch.ElapsedMilliseconds);
            return new ModelInstallResult(status.IsInstalled, status.Message, status);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Local model ensure failed. ModelRoot={ModelRoot} Version={Version} ElapsedMilliseconds={ElapsedMilliseconds}",
                options.ModelRoot,
                options.Version,
                stopwatch.ElapsedMilliseconds);
            await SaveStateAsync(ex.Message, false, null, CancellationToken.None);
            var status = await GetStatusAsync(CancellationToken.None);
            return new ModelInstallResult(false, ex.Message, status);
        }
    }

    private static async Task<RemoteFileMetadata> GetRemoteFileMetadataAsync(HttpClient httpClient, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var rawDigest = response.Headers.TryGetValues("X-Linked-Etag", out var linkedEtags)
            ? linkedEtags.FirstOrDefault()
            : response.Headers.ETag?.Tag;
        var digest = rawDigest?.Trim().TrimStart('W', '/').Trim('"').ToLowerInvariant() ?? "";
        var algorithm = digest.Length switch
        {
            64 when digest.All(Uri.IsHexDigit) => HashAlgorithmName.SHA256,
            40 when digest.All(Uri.IsHexDigit) => HashAlgorithmName.SHA1,
            _ => default
        };
        if (string.IsNullOrWhiteSpace(algorithm.Name))
        {
            throw new InvalidDataException($"The trusted model source did not provide a usable checksum for {url}.");
        }

        return new RemoteFileMetadata(
            response.Content.Headers.ContentLength,
            algorithm,
            digest,
            IsGitBlobSha1: digest.Length == 40 && !response.Headers.Contains("X-Linked-Etag"));
    }

    private static async Task<bool> MatchesRemoteDigestAsync(string path, RemoteFileMetadata metadata, CancellationToken cancellationToken) =>
        string.Equals(
            metadata.IsGitBlobSha1
                ? await ComputeGitBlobSha1Async(path, cancellationToken)
                : await ComputeHashAsync(path, metadata.Algorithm, cancellationToken),
            metadata.Digest,
            StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ComputeGitBlobSha1Async(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"blob {info.Length}\0"));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<string> ComputeHashAsync(string path, HashAlgorithmName algorithm, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var hash = algorithm == HashAlgorithmName.SHA1
            ? await SHA1.HashDataAsync(stream, cancellationToken)
            : await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        string file,
        long? totalBytes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long received = 0;
        var lastPercentage = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (totalBytes is > 0)
            {
                var percentage = (int)Math.Min(100, received * 100 / totalBytes.Value);
                if (percentage != lastPercentage)
                {
                    lastPercentage = percentage;
                    progress?.Report($"Downloading {file}: {percentage}%");
                }
            }
            else if (received == read || received % (16L * 1024 * 1024) < read)
            {
                progress?.Report($"Downloading {file}: {received / (1024 * 1024)} MiB");
            }
        }
    }

    private static bool IsExistingFileUsable(string path, long? expectedLength)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0)
        {
            return false;
        }

        return !expectedLength.HasValue || length == expectedLength.Value;
    }

    private string CreateStateSignature(string statePath) => string.Join('|',
        options.RequiredFiles.Append("vigilo-model-state.json").Select(file =>
        {
            var info = new FileInfo(file == "vigilo-model-state.json" ? statePath : Path.Combine(options.ModelRoot, file));
            return $"{file}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));

    private async Task SaveStateAsync(
        string message,
        bool installed,
        IReadOnlyDictionary<string, string>? fileSha256,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.ModelRoot);
        var state = new ModelState(
            options.Version,
            installed,
            DateTimeOffset.UtcNow,
            message,
            fileSha256);
        var path = Path.Combine(options.ModelRoot, "vigilo-model-state.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
    }

    private sealed record ModelState(
        string Version,
        bool Installed,
        DateTimeOffset LastCheckedAt,
        string Message,
        IReadOnlyDictionary<string, string>? FileSha256);

    private sealed record RemoteFileMetadata(
        long? Length,
        HashAlgorithmName Algorithm,
        string Digest,
        bool IsGitBlobSha1);

    private async Task<ModelStatus> GetOpenAiCompatibleStatusAsync(CancellationToken cancellationToken)
    {
        var endpoint = options.EndpointBaseUrl.TrimEnd('/');
        if (activityTracker.IsInferenceActive)
        {
            logger.LogDebug(
                "Local OpenAI-compatible model status check skipped while inference is active. Endpoint={Endpoint} ChatModel={ChatModel}",
                endpoint,
                options.ChatModel);
            return new ModelStatus(
                true,
                endpoint,
                options.Version,
                $"{options.DisplayName} is running local inference.",
                []);
        }

        var modelsEndpoint = $"{endpoint}/models";
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await httpClient.GetAsync(modelsEndpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelStatus(
                    false,
                    endpoint,
                    options.Version,
                    $"{options.DisplayName} local server returned HTTP {(int)response.StatusCode}.",
                    [options.ChatModel]);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var modelFound = document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                && data.EnumerateArray().Any(model =>
                    model.TryGetProperty("id", out var id)
                    && string.Equals(id.GetString(), options.ChatModel, StringComparison.OrdinalIgnoreCase));

            var status = new ModelStatus(
                modelFound,
                endpoint,
                options.Version,
                modelFound
                    ? $"{options.DisplayName} is available from the local LiteRT-LM server."
                    : $"{options.DisplayName} is not registered with LiteRT-LM. {options.SetupInstructions}",
                modelFound ? [] : [options.ChatModel]);

            logger.LogDebug(
                "Local OpenAI-compatible model status checked. Installed={Installed} Endpoint={Endpoint} ChatModel={ChatModel}",
                status.IsInstalled,
                endpoint,
                options.ChatModel);
            return status;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(
                ex,
                "Local OpenAI-compatible model status check failed. Endpoint={Endpoint} ChatModel={ChatModel}",
                endpoint,
                options.ChatModel);
            return new ModelStatus(
                false,
                endpoint,
                options.Version,
                $"{options.DisplayName} local server is not reachable at {endpoint}. {options.SetupInstructions}",
                [options.ChatModel]);
        }
    }
}
