using OVS.Rollback.Utils;
using Serilog.Events;
using Serilog.Expressions;
using Serilog.Formatting;
using Serilog.Templates.Themes;

namespace Serilog.Templates
{
    /// <summary>
    /// An <see cref="ExpressionTemplate"/> whose output goes through <see cref="AddressMask"/> once the P2P node has turned
    /// masking on. Until then, and always in the relay, it writes exactly what the template writes. serilog-config.json
    /// names it for every sink, with the arguments an ExpressionTemplate takes.
    /// </summary>
    public sealed class AddressMaskingTemplate : ITextFormatter
    {
        private readonly ExpressionTemplate _template;

        public AddressMaskingTemplate(string template, IFormatProvider? formatProvider = null, NameResolver? nameResolver = null, TemplateTheme? theme = null, bool applyThemeWhenOutputIsRedirected = false)
        {
            _template = new ExpressionTemplate(template, formatProvider, nameResolver, theme, applyThemeWhenOutputIsRedirected);
        }

        public void Format(LogEvent logEvent, TextWriter output)
        {
            if (!AddressMask.Enabled)
            {
                _template.Format(logEvent, output);
                return;
            }
            var line = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            _template.Format(logEvent, line);
            output.Write(AddressMask.Mask(line.ToString()));
        }
    }
}
