using System.Text.Json;
using Microsoft.OpenApi.Any;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PrevisaoPrecosImoveis.Api.OpenApi;

internal static class OpenApiJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IOpenApiAny From(object valor) =>
        OpenApiAnyFactory.CreateFromJson(JsonSerializer.Serialize(valor, Json))
        ?? new OpenApiObject();
}
