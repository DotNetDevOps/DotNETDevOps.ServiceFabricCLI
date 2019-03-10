using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.Fabric;
using System.Fabric.Description;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace DotNETDevOps.ServiceFabricCLI
{
    public static class ex
    {
        public static NameValueCollection ToNameValueCollection<TKey, TValue>(
    this IDictionary<TKey, TValue> dict)
        {
            var nameValueCollection = new NameValueCollection();

            foreach (var kvp in dict)
            {
                string value = null;
                if (kvp.Value != null)
                    value = kvp.Value.ToString();

                nameValueCollection.Add(kvp.Key.ToString(), value);
            }

            return nameValueCollection;
        }
    }
    public class DeploymentModel
    {
        public bool DeleteIfExists { get; set; }
        public string RemoteUrl { get; set; }
        public string ApplicationTypeName { get; set; }
        public string ApplicationTypeVersion { get; set; }
        public string ApplicationName { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();

        public ServiceDeploymentModel[] ServiceDeployments { get; set; } = Array.Empty<ServiceDeploymentModel>();
    }
    public class ServiceDeploymentModel
    {
        public string ServiceTypeName { get; set; }
        public string ServiceName { get; set; }
        public byte[] InitializationData { get; set; }
    }

    [Command(Name = "sfcli", Description = "Service Fabric Command Line Interface"), Subcommand(typeof(Deploy))]

    [HelpOption("-?")]
    class Program
    {
        [Command("deploy", Description = "Deploy packages")]
        private class Deploy
        {
            [Required]
            [Option("-r|--remote-url <REMOTEURL>", "The remote url for the package file", CommandOptionType.SingleValue)]
            public string RemoteUrl { get; set; }

            [Required]
            [Option("-a|--application-parameters <PARAMETERS>", "Application Parameters", CommandOptionType.SingleValue)]
            public string ApplicationParamters { get; set; }

            private async Task<int> OnExecuteAsync(IConsole console, ILogger<Deploy> logger, IApplicationLifetime applicationLifetime)
            {
                var fabric = new FabricClient();
                var deploymentModel = new DeploymentModel() { RemoteUrl=RemoteUrl};
                

                using (var stream = await new HttpClient().GetStreamAsync(deploymentModel.RemoteUrl))
                {
                    var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                    var entry = zip.GetEntry("ApplicationManifest.xml");

                    XDocument xDocument = await XDocument.LoadAsync(entry.Open(), LoadOptions.None, applicationLifetime.ApplicationStopping);
                    deploymentModel.ApplicationTypeName = xDocument.Root.Attribute("ApplicationTypeName").Value;
                    deploymentModel.ApplicationTypeVersion = xDocument.Root.Attribute("ApplicationTypeVersion").Value;

                    logger.LogInformation("Updated deployment model {@deploymentModel}", deploymentModel);
                }


                //  Console.WriteLine(app.Package);

                {

                    XDocument xDocument = XDocument.Load(ApplicationParamters);
                    deploymentModel.ApplicationName = xDocument.Root.Attribute("Name").Value;
                   
                    deploymentModel.Parameters = xDocument.Root.Descendants(xDocument.Root.GetDefaultNamespace()+"Parameter").ToDictionary(k => k.Attribute("Name").Value, k => k.Attribute("Value").Value);

                   
                }
                Console.WriteLine(deploymentModel.ApplicationTypeName);
                Console.WriteLine(deploymentModel.ApplicationTypeVersion);
                Console.WriteLine(deploymentModel.ApplicationName);


                var types = await fabric.QueryManager.GetApplicationTypeListAsync(deploymentModel.ApplicationTypeName);

                if (!types.Any(a => a.ApplicationTypeName == deploymentModel.ApplicationTypeName && a.ApplicationTypeVersion == deploymentModel.ApplicationTypeVersion))
                {
                    logger.LogInformation("Starting to provision {@deploymentModel}", deploymentModel);

                    //            fabric.ApplicationManager.CreateApplicationAsync(new System.Fabric.Description.ApplicationDescription{ )
                    await fabric.ApplicationManager.ProvisionApplicationAsync(
                        new ExternalStoreProvisionApplicationTypeDescription(
                            applicationPackageDownloadUri: new Uri(deploymentModel.RemoteUrl),
                            applicationTypeName: deploymentModel.ApplicationTypeName,
                            applicationTypeVersion: deploymentModel.ApplicationTypeVersion
                        ), TimeSpan.FromMinutes(5), applicationLifetime.ApplicationStopping
                    );
                    logger.LogInformation("Completed to provision {@deploymentModel}", deploymentModel);

                
                }

                var applicationName = new Uri($"fabric:/{deploymentModel.ApplicationName.Replace("fabric:/","")}");
                var applications = await fabric.QueryManager.GetApplicationListAsync(applicationName);
                if (!applications.Any(application => application.ApplicationName == applicationName))
                {
                    await CreateApplication(logger, deploymentModel, fabric, applicationName);
                }

                return 0;

            }

            private async Task CreateApplication(ILogger logger, DeploymentModel deploymentModel, FabricClient fabric, Uri applicationName)
            {
                logger.LogInformation("Starting to create application {@deploymentModel}", deploymentModel);
                await fabric.ApplicationManager.CreateApplicationAsync(
                    new ApplicationDescription(
                        applicationName: applicationName,
                        applicationTypeName: deploymentModel.ApplicationTypeName,
                        applicationTypeVersion: deploymentModel.ApplicationTypeVersion,
                        applicationParameters: deploymentModel.Parameters.ToNameValueCollection()
                    )
                );
                logger.LogInformation("Completed to create application {@deploymentModel}", deploymentModel);

                foreach (var serviceDeployment in deploymentModel.ServiceDeployments)
                {
                    var serviceName = new Uri($"{applicationName}/{serviceDeployment.ServiceName}");
                    logger.LogInformation("creating service for {applicationName} {ServiceName}", applicationName, serviceName);
                    var services = await fabric.QueryManager.GetServiceListAsync(applicationName, serviceName);

                    if (!services.Any(s => s.ServiceName == serviceName))
                    {
                        await fabric.ServiceManager.CreateServiceAsync(description: new StatelessServiceDescription
                        {
                            ServiceTypeName = serviceDeployment.ServiceTypeName,
                            ApplicationName = applicationName,
                            ServiceName = serviceName,
                            InitializationData = serviceDeployment.InitializationData,
                            PartitionSchemeDescription = new SingletonPartitionSchemeDescription() { }
                        });
                        logger.LogInformation("Service created for {ServiceName}", serviceName);
                    }
                }
                logger.LogInformation("Completed to create services {@deploymentModel}", deploymentModel);
            }
        }
        static Task<int> Main(string[] args)
            => new HostBuilder()
            .ConfigureHostConfiguration(b =>
            {
                Log.Logger = new LoggerConfiguration()
                  .MinimumLevel.Information()
                  .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                  .Enrich.FromLogContext()
                  .WriteTo.Console()
                  .CreateLogger();

            })
            .UseSerilog()
            .RunCommandLineApplicationAsync<Program>(args);




        private Task<int> OnExecuteAsync(CommandLineApplication app, IConsole console)
        {
            console.WriteLine("You must specify at a subcommand.");
            app.ShowHelp();
           
            return Task.FromResult(1);

        }
    }
}
