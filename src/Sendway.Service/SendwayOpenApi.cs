using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Sendway.Core;
using Sendway.Service.Endpoints;

namespace Sendway.Service;

// The OpenAPI description of the API a calling service uses — sending messages and reading their
// outcome. Served at /openapi/v1.json and written to docs/openapi.json at build time, so a contract
// change shows up as a diff. The administration API is for whoever runs the deployment and is
// described in the operations guide instead.
public static class SendwayOpenApi
{
    private const string TenantKeyScheme = "tenantKey";

    private static readonly string[] Statuses =
    [
        nameof(DeliveryStatus.Queued), nameof(DeliveryStatus.Sending), nameof(DeliveryStatus.Sent), nameof(DeliveryStatus.Failed)
    ];

    private static readonly string[] FailureReasons =
    [
        MessageFailureReasons.RecipientRefused,
        MessageFailureReasons.MessageRefused,
        MessageFailureReasons.RecipientRateLimited,
        MessageFailureReasons.Expired,
        MessageFailureReasons.AttemptsExhausted
    ];

    private static readonly string[] ProblemCodes =
    [
        ApiProblem.InvalidRequest,
        ApiProblem.InvalidRecipient,
        ApiProblem.IdempotencyKeyReused,
        ApiProblem.ChannelNotConfigured,
        ApiProblem.RateLimited,
        ApiProblem.DestinationNotAllowed,
        ApiProblem.DailyLimitReached,
        ApiProblem.Unauthorized,
        ApiProblem.NotFound,
        ApiProblem.InternalError
    ];

    public static IServiceCollection AddSendwayOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.ShouldInclude = description =>
                description.RelativePath?.StartsWith("messages", StringComparison.Ordinal) == true;

            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Sendway",
                    Version = "v1",
                    Description =
                        "Send email, SMS and app push through one HTTP API. A send is accepted with 202 and delivered " +
                        "asynchronously; read the outcome from the message status or receive it as a Standard Webhooks event."
                };

                // The address is the deployment's, not the API's: a caller configures its own base URL,
                // and the served description stays identical to the committed one.
                document.Servers = null;

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[TenantKeyScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-Api-Key",
                    Description = "The calling service's tenant key."
                };
                document.Security =
                [
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(TenantKeyScheme, document)] = [] }
                ];

                document.Webhooks = new Dictionary<string, IOpenApiPathItem>
                {
                    ["messageOutcome"] = MessageOutcomeWebhook(document)
                };
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.HttpMethod == HttpMethods.Post)
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "Idempotency-Key",
                        In = ParameterLocation.Header,
                        Required = false,
                        Description =
                            "Up to 255 characters. A repeat of the same request with the same key, within 24 hours, is answered " +
                            "with the original message instead of sending again; the same key with a different request is 422.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 255 }
                    });
                }

                return Task.CompletedTask;
            });

            options.AddSchemaTransformer((schema, context, _) =>
            {
                var type = context.JsonTypeInfo.Type;
                if (type == typeof(MessageStatusResponse) || type == typeof(SendMessageResponse))
                {
                    Restrict(schema, "status", Statuses);
                }

                if (type == typeof(MessageStatusResponse))
                {
                    Restrict(schema, "reason", FailureReasons);
                }

                if (type == typeof(ProblemDetails) || type == typeof(HttpValidationProblemDetails))
                {
                    schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
                    schema.Properties["code"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "What went wrong, for code to branch on.",
                        Enum = [.. ProblemCodes.Select(code => (JsonNode)code)]
                    };
                }

                return Task.CompletedTask;
            });
        });

    // What Sendway POSTs to a tenant's registered endpoint when a message reaches Sent or Failed:
    // a Standard Webhooks request whose `data` is the message status document.
    private static OpenApiPathItem MessageOutcomeWebhook(OpenApiDocument document)
    {
        static OpenApiParameter Header(string name, string description) => new()
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = true,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        };

        var envelope = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string> { "type", "timestamp", "data" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = [(JsonNode)MessageEvent.Sent, (JsonNode)MessageEvent.Failed]
                },
                ["timestamp"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" },
                ["data"] = new OpenApiSchemaReference(nameof(MessageStatusResponse), document)
            }
        };

        return new OpenApiPathItem
        {
            Operations = new Dictionary<HttpMethod, OpenApiOperation>
            {
                [HttpMethod.Post] = new()
                {
                    OperationId = "MessageOutcome",
                    Summary = "A message reached its final outcome (Sent or Failed)",
                    Description =
                        "Signed per Standard Webhooks (https://www.standardwebhooks.com/) with the endpoint's whsec_ secret. " +
                        "Delivery is at least once: deduplicate on webhook-id. Answer 2xx to acknowledge; 410 stops further events.",
                    Parameters =
                    [
                        Header("webhook-id", "The event's id; the same on every redelivery of the event."),
                        Header("webhook-timestamp", "Unix seconds when this delivery was signed."),
                        Header("webhook-signature", "v1,<base64 HMAC-SHA256 of \"{webhook-id}.{webhook-timestamp}.{body}\">")
                    ],
                    RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new() { Schema = envelope }
                        }
                    },
                    Responses = new OpenApiResponses
                    {
                        ["2XX"] = new OpenApiResponse { Description = "Received." },
                        ["410"] = new OpenApiResponse { Description = "The receiver wants no more events." }
                    }
                }
            }
        };
    }

    private static void Restrict(OpenApiSchema schema, string property, string[] values)
    {
        if (schema.Properties?.TryGetValue(property, out var found) == true && found is OpenApiSchema target)
        {
            target.Enum = [.. values.Select(value => (JsonNode)value)];
        }
    }
}
