using Google.Cloud.Functions.Hosting;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HeroesProfile_GCP_ReplayParser
{
    public class Startup : FunctionsStartup
    {
        public override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =>
            services.AddSingleton(StorageClient.Create());

        // Request start/finish lines duplicate Cloud Run's own request log.
        public override void ConfigureLogging(WebHostBuilderContext context, ILoggingBuilder logging) =>
            logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    }
}
