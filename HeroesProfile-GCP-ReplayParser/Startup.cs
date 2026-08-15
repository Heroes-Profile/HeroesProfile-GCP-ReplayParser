using Google.Cloud.Functions.Hosting;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace HeroesProfile_GCP_ReplayParser
{
    public class Startup : FunctionsStartup
    {
        public override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =>
            services.AddSingleton(StorageClient.Create());
    }
}
