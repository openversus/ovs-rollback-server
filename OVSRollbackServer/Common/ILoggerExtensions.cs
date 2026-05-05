using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Text;

namespace OVS.Rollback.Common
{
    public static class ILoggerExtensions
    {
        extension(ILogger logger)
        {
            public ILogger ForContext<T>() => logger.ForContext(Constants.SourceContextPropertyName, typeof(T).Name);
        }
    }
}
