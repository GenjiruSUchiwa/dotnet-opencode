namespace OpenCode.Server.Http;

using System.Text.Json;

/// <summary>Framework binding failures occur before endpoint filters; normalize them without replacing domain errors.</summary>
public static class RequestValidation
{
    public static IServiceCollection AddNativeRequestValidation(this IServiceCollection services)
    {
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.RespectNullableAnnotations = true;
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
            options.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
        });
        return services;
    }

    public static IApplicationBuilder UseNativeRequestValidation(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        try { await next(context); }
        catch (BadHttpRequestException error) when (!context.Response.HasStarted)
        {
            // Unsupported media/body-size statuses are transport errors, not a
            // successful response or an invented schema kind.
            if (error.StatusCode != StatusCodes.Status400BadRequest)
            {
                context.Response.StatusCode = error.StatusCode;
                return;
            }
            var reason = error.InnerException is JsonException json ? json.Message : error.Message;
            var message = reason.Length <= 1024 ? reason : reason[..1024] + $"... ({reason.Length - 1024} more chars)";
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(RequestValidation));
            if (error.InnerException is JsonException)
            {
                logger.LogWarning("Schema rejection ({Kind}): {Reason}", "Payload", message);
                await Results.Json(new { _tag = "InvalidRequestError", message, kind = "Payload" }, statusCode: 400).ExecuteAsync(context);
                return;
            }
            // BadHttpRequestException does not expose the failed parameter's
            // source. Do not guess Params/Query/Headers by parsing its wording.
            logger.LogWarning("Request binding rejection: {Reason}", message);
            await Results.Json(new { _tag = "InvalidRequestError", message }, statusCode: 400).ExecuteAsync(context);
        }
    });
}
