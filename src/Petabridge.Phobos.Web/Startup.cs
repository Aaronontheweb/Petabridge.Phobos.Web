// -----------------------------------------------------------------------
// <copyright file="Startup.cs" company="Petabridge, LLC">
//      Copyright (C) 2015 - 2021 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using System.Net;
using Akka.Actor;
using Akka.Bootstrap.Docker;
using Akka.Configuration;
using App.Metrics;
using App.Metrics.Reporting.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTracing;
using Petabridge.Tracing.ApplicationInsights;
using Phobos.Actor;
using Phobos.Actor.Configuration;
using Phobos.Tracing;
using Phobos.Tracing.Scopes;

namespace Petabridge.Phobos.Web
{
    public class Startup
    {
        public const string APP_INSIGHTS_INSTRUMENTATION_KEY = "APP_INSIGHTS_INSTRUMENTATION_KEY";
        
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            // enables OpenTracing for ASP.NET Core
            services.AddOpenTracing(o =>
            {
                o.ConfigureAspNetCore(a =>
                {
                    a.Hosting.OperationNameResolver = context => $"{context.Request.Method} {context.Request.Path}";

                    // skip Prometheus HTTP /metrics collection from appearing in our tracing system
                    a.Hosting.IgnorePatterns.Add(x => x.Request.Path.StartsWithSegments(new PathString("/metrics")));
                });
                o.ConfigureGenericDiagnostics(c => { });
            });

            // sets up AppInsights metrics
            ConfigureAppMetrics(services);

            // sets up tracing
            ConfigureTracing(services);

            // sets up Akka.NET
            ConfigureAkka(services);
        }


        public static void ConfigureAppMetrics(IServiceCollection services)
        {
            services.AddMetricsTrackingMiddleware();
            services.AddMetrics(b =>
            {
                var metrics = b.Configuration.Configure(o =>
                    {
                        o.GlobalTags.Add("host", Dns.GetHostName());
                        o.DefaultContextLabel = "akka.net";
                        o.Enabled = true;
                        o.ReportingEnabled = true;
                    })
                    .Report.ToApplicationInsights(opts =>
                    {
                        opts.InstrumentationKey = Environment.GetEnvironmentVariable(APP_INSIGHTS_INSTRUMENTATION_KEY);
                        opts.ItemsAsCustomDimensions = true;
                        opts.DefaultCustomDimensionName = "item";
                    }).Build();
            });
            services.AddMetricsReportingHostedService();
        }

        public static void ConfigureTracing(IServiceCollection services)
        {
            services.AddSingleton<ITracer>(sp => new ApplicationInsightsTracer(
                new TelemetryConfiguration(Environment.GetEnvironmentVariable(APP_INSIGHTS_INSTRUMENTATION_KEY)),
                new Tracing.ApplicationInsights.Endpoint(typeof(Startup).Assembly.GetName().Name, Dns.GetHostName(), 4055))
                .WithScopeManager(new ActorScopeManager()) // need this for correct actor correlation
            );
        }

        public static void ConfigureAkka(IServiceCollection services)
        {
            services.AddSingleton(sp =>
            {
                var metrics = sp.GetRequiredService<IMetricsRoot>();
                var tracer = sp.GetRequiredService<ITracer>();

                var config = ConfigurationFactory.ParseString(File.ReadAllText("app.conf"))
                    .BootstrapFromDocker()
                    .UseSerilog();

                var phobosSetup = PhobosSetup.Create(new PhobosConfigBuilder()
                        .WithMetrics(m =>
                            m.SetMetricsRoot(metrics)) // binds Phobos to same IMetricsRoot as ASP.NET Core
                        .WithTracing(t => t.SetTracer(tracer))) // binds Phobos to same tracer as ASP.NET Core
                    .WithSetup(BootstrapSetup.Create()
                        .WithConfig(config) // passes in the HOCON for Akka.NET to the ActorSystem
                        .WithActorRefProvider(PhobosProviderSelection
                            .Cluster)); // last line activates Phobos inside Akka.NET

                var sys = ActorSystem.Create("ClusterSys", phobosSetup);

                // create actor "container" and bind it to DI, so it can be used by ASP.NET Core
                return new AkkaActors(sys);
            });

            // this will manage Akka.NET lifecycle
            services.AddHostedService<AkkaService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment()) app.UseDeveloperExceptionPage();

            app.UseRouting();

            // enable App.Metrics routes
            app.UseMetricsAllMiddleware();

            app.UseEndpoints(endpoints =>
            {
                var actors = endpoints.ServiceProvider.GetService<AkkaActors>();
                var tracer = endpoints.ServiceProvider.GetService<ITracer>();
                endpoints.MapGet("/", async context =>
                {
                    using (var s = tracer.BuildSpan("Cluster.Ask").StartActive())
                    {
                        // router actor will deliver message randomly to someone in cluster
                        var resp = await actors.RouterForwarderActor.Ask<string>($"hit from {context.TraceIdentifier}",
                            TimeSpan.FromSeconds(5));
                        await context.Response.WriteAsync(resp);
                    }
                });
            });
        }
    }
}