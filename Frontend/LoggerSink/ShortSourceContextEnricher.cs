using System.Collections.Concurrent;

using Serilog.Core;
using Serilog.Events;

namespace Frontend;

public sealed class ShortSourceContextEnricher : ILogEventEnricher
{
    /// <summary>The property it adds: the source context's last segment, the class name.</summary>
    public const string PropertyName = "ShortSource";

    private static readonly ConcurrentDictionary<string, LogEventProperty> Cache = [];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? value) &&
            value is ScalarValue { Value: string sourceContext } &&
            sourceContext.Length > 0)
        {
            logEvent.AddPropertyIfAbsent(
                Cache.GetOrAdd(sourceContext, static (ctx, factory) =>
                {
                    int lastDot = ctx.LastIndexOf('.');
                    string shortName = lastDot >= 0 ? ctx[(lastDot + 1)..] : ctx;
                    return factory.CreateProperty(PropertyName, shortName);
                }, propertyFactory));
        }
    }
}
