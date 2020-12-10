using HealthChecks;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection
{
    public class FileHealthCheckPublisher : IHealthCheckPublisher
    {
        private readonly string _path;

        public FileHealthCheckPublisher(string path)
        {
            _path = path;
        }

        public async Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
        {
            var message = JsonConvert.SerializeObject(report, new JsonSerializerSettings()
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Converters = new List<JsonConverter>()
            {
                new StringEnumConverter()
            }
            });
            await File.AppendAllTextAsync(_path, message + "\n", cancellationToken);
        }
    }


    public static class FilePathWriterHealthChecksBuilderExtensions
    {
        public static IHealthChecksBuilder AddFilePathWriter(this IHealthChecksBuilder builder, string filePath,
            HealthStatus failureStatus, IEnumerable<string> tags = default)
        {
            if (filePath == null) throw new ArgumentNullException(nameof(filePath));
            return builder.Add(
                new HealthCheckRegistration(
                    "File Path Health Check",
                    new FilePathWriterHealthCheck(filePath),
                    failureStatus,
                    tags));
        }
    }
}

namespace HealthChecks
{
    public class FilePathWriterHealthCheck : IHealthCheck
    {
        private readonly string _filePath;
        private readonly IReadOnlyDictionary<string, object> _healthCheckData;

        public FilePathWriterHealthCheck(string filePath)
        {
            _filePath = filePath;
            _healthCheckData = new Dictionary<string, object>()
        {
            {"FilePath", _filePath}
        };
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
        {
            try
            {
                await File.WriteAllTextAsync(_filePath, nameof(FilePathWriterHealthCheck), cancellationToken);
                File.Delete(_filePath);
                return HealthCheckResult.Healthy();
            }
            catch (Exception e)
            {
                return context.Registration.FailureStatus switch
                {
                    HealthStatus.Degraded => HealthCheckResult.Degraded("Issues writing to file path", e, _healthCheckData),
                    _ => HealthCheckResult.Unhealthy("Issues writing to file path", e, _healthCheckData),
                };
            }
        }
    }

    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddHealthChecks();
            services.AddHealthChecks()
                .AddSqlServer("Server=.;Database=master;Trusted_Connection=True;")
                .AddUrlGroup(
                    new Uri("http://www.mocky.io/v2/5e2601892f00007c00ce2de6?mocky-delay=3000ms"),
                    "mocky",
                    failureStatus: HealthStatus.Degraded,
                    timeout: new TimeSpan(0, 0, 2))
                .AddFilePathWriter(Path.Join(Directory.GetCurrentDirectory(), "test"), HealthStatus.Unhealthy, tags: new[] { "ready" });

            services
                .AddHealthChecksUI()
                .AddInMemoryStorage();

            //.AddCheck(
            //    name: "File Path Health Check", // "File Path Health Check" aparecerá como Health Check en la respuesta
            //    instance: new FilePathWriterHealthCheck(Path.Join(Directory.GetCurrentDirectory(), "test")), // With Directory.GetCurrentDirectory() will be Unhealthy
            //    failureStatus: HealthStatus.Unhealthy,
            //    tags: new[] { "ready" });
            services.Configure<HealthCheckPublisherOptions>(options =>
{
    options.Delay = TimeSpan.FromSeconds(30); // publicar 30 segundos después de iniciar la aplicación, por defecto 5 segundos, lo que tarden nuestras dependencias (readiness) en levantar
    options.Period = TimeSpan.FromSeconds(30); // publicar cada 30 segundos, por defecto 10 segundos
    options.Predicate = check =>
    {
        // filtrar healthchecks
        return true;
    };
    options.Timeout = TimeSpan.FromSeconds(5); // ¿cuánto esperar a que respondan nuestros healtchecks?, por defecto 30
});
            services.AddSingleton<IHealthCheckPublisher>(new FileHealthCheckPublisher(@"C:\Temp\health.txt")); // No pasa por app.UseEndpoints, usa directamente los servicios registrados de tipo IHealthCheck
        }

        private Task CustomResponse(HttpContext httpContext, HealthReport result)
        {
            httpContext.Response.ContentType = "application/json";

            var dependencies = result.Entries.ToDictionary(entry => new KeyValuePair<string, object>(
                entry.Key,
                new
                {
                    entry.Value.Status,
                    Duration = entry.Value.Duration.TotalSeconds,
                    Failure = entry.Value.Exception != null && !(entry.Value.Exception is TaskCanceledException) ? entry.Value.Exception : null
                }));
            var value = new
            {
                result.Status,
                result.TotalDuration,
                Dependencies = dependencies
            };
            return httpContext.Response.WriteAsync(JsonConvert.SerializeObject(value, new Newtonsoft.Json.Converters.StringEnumConverter()));
        }


        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                var healthCheckOptions = new HealthCheckOptions()
                {
                    ResultStatusCodes = // by default
                    {
                        [HealthStatus.Healthy] = StatusCodes.Status200OK,
                        [HealthStatus.Degraded] = StatusCodes.Status200OK,
                        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
                    },
                    //ResponseWriter = CustomResponse,
                    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse, // for MapHealthChecksUI
                    Predicate = (HealthCheckRegistration check) =>
                    {
                        // Before healthcheck execution
                        logger.LogInformation(check.FailureStatus.ToString());
                        logger.LogInformation(check.Name);
                        logger.LogInformation(check.Tags.Count.ToString());
                        logger.LogInformation(check.Timeout.ToString());
                        return true;
                    },
                    AllowCachingResponses = false //bydefault
                };

                endpoints.MapHealthChecks("/health", healthCheckOptions);

                endpoints.MapHealthChecksUI(); // por defecto, /healthchecks-ui
            });
        }
    }
}
