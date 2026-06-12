using Serilog.Configuration;
using Serilog.Enrichers;

namespace Serilog
{
    public static class CallerNameConfigurationExtension
    {
        extension(LoggerEnrichmentConfiguration enrichmentConfiguration)
        {
            public LoggerConfiguration WithCallerNameEnricher()
            {
                if (null == enrichmentConfiguration)
                {
                    return new LoggerConfiguration().Enrich.With<CallerNameEnricher>();
                }

                return enrichmentConfiguration.With<CallerNameEnricher>();
            }
        }
    }
}
