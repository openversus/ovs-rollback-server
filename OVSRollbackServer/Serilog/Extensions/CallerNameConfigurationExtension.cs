using Microsoft.Extensions.Configuration;
using OVS.Rollback.Common;
using Serilog.Configuration;
using Serilog.Enrichers;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Serilog
{
    public static class CallerNameConfigurationExtension
    {
        extension(LoggerEnrichmentConfiguration enrichmentConfiguration)
        {
            public LoggerConfiguration WithCallerNameEnricher()
            {
                if (enrichmentConfiguration == null)
                {
                    string assmLocation = Assembly.GetExecutingAssembly().Location;
                    string manifestName = Assembly.GetExecutingAssembly().ManifestModule.Name;
                    string basePath = assmLocation.Replace(manifestName, "");

                    var config = new ConfigurationBuilder()
                        .SetBasePath(basePath)
                        .AddJsonFile(basePath / "Configuration" / "Logging" / "Runtime" / "serilog-config.json")
                        .Build();

                    return new LoggerConfiguration()
                        .ReadFrom.Configuration(config)
                        .Enrich.With<CallerNameEnricher>();
                }

                return enrichmentConfiguration.With<CallerNameEnricher>();
            }
        }
        //public static LoggerConfiguration WithCallerNameEnricher(
        //    this LoggerEnrichmentConfiguration enrichmentConfiguration)
        //{
        //    if (enrichmentConfiguration == null) throw new ArgumentNullException(nameof(enrichmentConfiguration));
        //    return enrichmentConfiguration.With<CallerNameEnricher>();
        //}
    }
}
