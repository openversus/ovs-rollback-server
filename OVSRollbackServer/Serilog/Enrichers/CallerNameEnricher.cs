using Serilog.Core;
using Serilog.Events;
using System.Diagnostics;

namespace Serilog.Enrichers
{
    sealed class CallerNameEnricher : ILogEventEnricher
    {
        LogEventProperty? _callerProperty;
        const string CallerNamePropertyName = "CallerName";


        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            string callerName = "Unknown";

            StackFrame? firstUserFrame = new StackTrace(fNeedFileInfo: true)
                .GetFrames()
                .Where(f =>
                {
                    var method = f?.GetMethod();
                    var declaringType = method?.DeclaringType;
                    var fullName = declaringType?.FullName;
                    return !string.IsNullOrEmpty(fullName) &&
                           !fullName.Contains("System.") &&
                           !fullName.Contains("Serilog.") &&
                           !fullName.Contains("Microsoft.Extensions");
                })
                .FirstOrDefault();

            if (firstUserFrame != null)
            {
                callerName = firstUserFrame.GetMethod()?.Name ?? "Unknown";

                if (callerName == ".ctor")
                {
                    callerName = firstUserFrame.GetMethod()?.DeclaringType?.Name ?? "Unknown";
                }
            }

            //callerName = "[darkgoldenrod]" + callerName + "[/][fuchsia]()[/]";

            _callerProperty = propertyFactory.CreateProperty(
                CallerNamePropertyName,
                callerName);

            logEvent.AddPropertyIfAbsent(_callerProperty);
            ;
            ;
        }
    }
}
