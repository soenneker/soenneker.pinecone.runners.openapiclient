using Microsoft.Extensions.DependencyInjection;
using Soenneker.Kiota.Util.Registrars;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.OpenApi.Fixer.Registrars;
using Soenneker.OpenApi.Merger.Registrars;
using Soenneker.Pinecone.Runners.OpenApiClient.Utils;
using Soenneker.Pinecone.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Yaml.Registrars;

namespace Soenneker.Pinecone.Runners.OpenApiClient;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddSingleton<IFileOperationsUtil, FileOperationsUtil>()
                .AddRunnersManagerAsSingleton()
                .AddOpenApiMergerAsSingleton()
                .AddOpenApiFixerAsSingleton()
                .AddKiotaUtilAsSingleton()
                .AddYamlUtilAsSingleton();

        return services;
    }
}
