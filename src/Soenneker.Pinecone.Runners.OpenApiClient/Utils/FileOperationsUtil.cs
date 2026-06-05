using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.OpenApi.Merger.Abstract;
using Soenneker.Pinecone.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using System.Collections.Generic;
using Soenneker.OpenApi.Fixer;
using Soenneker.Utils.Yaml.Abstract;

namespace Soenneker.Pinecone.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private const string _pineconeApiGitUrl = "https://github.com/pinecone-io/pinecone-api";

    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IOpenApiMerger _openApiMerger;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IYamlUtil _yamlUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil,
        IOpenApiMerger openApiMerger, IYamlUtil yamlUtil, IKiotaUtil kiotaUtil, IOpenApiFixer openApiFixer)
    {
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _openApiMerger = openApiMerger;
        _openApiFixer = openApiFixer;
        _yamlUtil = yamlUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}",
            cancellationToken: cancellationToken);

        string targetFilePath = Path.Combine(gitDirectory, "openapi.json");

        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string pineconeApiDirectory = await _gitUtil.CloneToTempDirectory(_pineconeApiGitUrl, cancellationToken: cancellationToken);

        string latestSpecDirectory = GetLatestSpecDirectory(pineconeApiDirectory);

        _logger.LogInformation("Using Pinecone OpenAPI directory: {Directory}", latestSpecDirectory);

        string jsonDirectory = await ConvertOasYamlFilesToJson(latestSpecDirectory, cancellationToken);

        OpenApiDocument merged = await _openApiMerger.MergeDirectory(jsonDirectory, cancellationToken).NoSync();
        string json = _openApiMerger.ToJson(merged);

        await _fileUtil.Write(targetFilePath, json, true, cancellationToken);

        string fixedFilePath = Path.Combine(gitDirectory, "fixed.json");
        await _fileUtil.DeleteIfExists(fixedFilePath, cancellationToken: cancellationToken);
        await _openApiFixer.Fix(targetFilePath, fixedFilePath, new OpenApiFixerOptions { StripDateSuffixesFromGeneratedNames = true }, cancellationToken)
                           .NoSync();

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "PineconeOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    private static string GetLatestSpecDirectory(string repositoryDirectory)
    {
        string[] directories = Directory.GetDirectories(repositoryDirectory).Where(IsVersionDirectory)
                                        .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();

        if (directories.Length == 0)
            throw new InvalidOperationException($"No Pinecone OpenAPI version directories were found in '{repositoryDirectory}'.");

        return directories[0];
    }

    private static bool IsVersionDirectory(string directoryPath)
    {
        string? name = Path.GetFileName(directoryPath);

        return name is { Length: 7 } && char.IsDigit(name[0]) && char.IsDigit(name[1]) && char.IsDigit(name[2]) && char.IsDigit(name[3]) && name[4] == '-' &&
               char.IsDigit(name[5]) && char.IsDigit(name[6]);
    }

    private async ValueTask<string> ConvertOasYamlFilesToJson(string sourceDirectory, CancellationToken cancellationToken)
    {
        string jsonDirectory = Path.Combine(Path.GetTempPath(), $"pinecone-openapi-json-{Guid.NewGuid():N}");

        await _directoryUtil.Create(jsonDirectory, false, cancellationToken);

        string[] filePaths = Directory.GetFiles(sourceDirectory, "*.oas.yaml", SearchOption.TopDirectoryOnly)
                                      .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();

        if (filePaths.Length == 0)
            throw new InvalidOperationException($"No .oas.yaml files were found in '{sourceDirectory}'.");

        foreach (string filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string targetJsonPath = Path.Combine(jsonDirectory, Path.ChangeExtension(fileName, ".json"));

            _logger.LogInformation("Converting Pinecone OpenAPI YAML to JSON: {FilePath}", filePath);
            await _yamlUtil.SaveAsJson(filePath, targetJsonPath, true, cancellationToken).NoSync();
        }

        return jsonDirectory;
    }

    /// <summary>
    /// Deletes all except csproj.
    /// </summary>
    /// <param name="directoryPath">The directory path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}