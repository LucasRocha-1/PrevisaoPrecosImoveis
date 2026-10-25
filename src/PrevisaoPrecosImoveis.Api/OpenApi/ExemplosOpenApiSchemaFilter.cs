using Microsoft.OpenApi.Models;
using PrevisaoPrecosImoveis.Api.Contracts;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PrevisaoPrecosImoveis.Api.OpenApi;

public sealed class ExemplosOpenApiSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(CreatePrevisaoRequest))
        {
            schema.Example = OpenApiJson.From(ExemplosPrevisao.ApartamentoPinheiros);
            schema.Description =
                "Características do imóvel. Use um dos exemplos prontos do POST /api/v1/previsoes; " +
                "não envie campos vazios ou zerados onde o modelo espera valores reais.";
        }
        else if (context.Type == typeof(PrevisaoResponse))
        {
            schema.Example = OpenApiJson.From(new PrevisaoResponse(
                1_120_000,
                980_000,
                1_260_000,
                new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.Zero)));
        }
        else if (context.Type == typeof(EstadoApiResponse))
        {
            schema.Example = OpenApiJson.From(new EstadoApiResponse(true, true));
        }
        else if (context.Type == typeof(ExemploPrevisaoDocumentado))
        {
            schema.Example = OpenApiJson.From(ExemplosPrevisao.Todos[0]);
        }
        else if (context.Type == typeof(ModeloInfoResponse))
        {
            schema.Example = OpenApiJson.From(new ModeloInfoResponse(
                true,
                "data/models/modelo_preco_imovel.zip",
                "Modelo de regressão persistido pelo pipeline de treino.",
                ["cidade", "bairro", "areaConstruidaM2", "tipoImovel"]));
        }
    }
}
