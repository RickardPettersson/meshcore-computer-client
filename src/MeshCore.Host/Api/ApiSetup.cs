using System.Text.Json.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace MeshCore.Host.Api;

public static class ApiSetup
{
    public static IServiceCollection AddMeshCoreApi(this IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddOpenApi("v1", o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo
                {
                    Title = "MeshCore Computer Client API",
                    Version = "v1",
                    Description = "Read and send MeshCore messages and manage the companion radio. " +
                                  "Get a token from POST /api/v1/auth/token (or /setup on first run) and send it as 'Authorization: Bearer <token>'.",
                };
                return Task.CompletedTask;
            });
            o.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        });
        return services;
    }

    /// <summary>Serves the OpenAPI document and the Scalar API explorer at <c>/scalar</c>.</summary>
    public static WebApplication MapMeshCoreApiDocs(this WebApplication app)
    {
        app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
        app.MapScalarApiReference("/scalar", o => o
                .WithTitle("MeshCore Computer Client API")
                .AddPreferredSecuritySchemes("Bearer"))
            .AllowAnonymous();
        return app;
    }

    private sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Access token from /api/v1/auth/token",
            };
            document.Security ??= [];
            document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] });
            return Task.CompletedTask;
        }
    }
}
