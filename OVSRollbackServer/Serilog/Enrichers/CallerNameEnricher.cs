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

            // DiagnosticMethodInfo instead of StackFrame.GetMethod(): the latter returns null
            // under Native AOT because method reflection metadata is trimmed, while
            // DiagnosticMethodInfo reads the stack-trace metadata AOT keeps for diagnostics.
            DiagnosticMethodInfo? firstUserMethod = null;
            foreach (StackFrame frame in new StackTrace(fNeedFileInfo: false).GetFrames())
            {
                DiagnosticMethodInfo? methodInfo = DiagnosticMethodInfo.Create(frame);
                string? declaringTypeName = methodInfo?.DeclaringTypeName;
                if (!string.IsNullOrEmpty(declaringTypeName) &&
                    !declaringTypeName.Contains("System.") &&
                    !declaringTypeName.Contains("Serilog.") &&
                    !declaringTypeName.Contains("Microsoft.Extensions"))
                {
                    firstUserMethod = methodInfo;
                    break;
                }
            }

            if (null != firstUserMethod)
            {
                callerName = firstUserMethod.Name;

                if (callerName == ".ctor")
                {
                    string declaringTypeName = firstUserMethod.DeclaringTypeName ?? "Unknown";
                    int lastDotIndex = declaringTypeName.LastIndexOf('.');
                    callerName = lastDotIndex >= 0 ? declaringTypeName[(lastDotIndex + 1)..] : declaringTypeName;
                }
            }

            _callerProperty = propertyFactory.CreateProperty(
                CallerNamePropertyName,
                callerName);

            logEvent.AddPropertyIfAbsent(_callerProperty);
        }
    }
}
