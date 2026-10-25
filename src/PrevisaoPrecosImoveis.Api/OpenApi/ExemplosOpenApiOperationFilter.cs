using Microsoft.OpenApi.Models;
using PrevisaoPrecosImoveis.Api.Contracts;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PrevisaoPrecosImoveis.Api.OpenApi;

public sealed class ExemplosOpenApiOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var caminho = context.ApiDescription.RelativePath ?? string.Empty;
        var metodo = context.ApiDescription.HttpMethod ?? string.Empty;

        if (caminho.Equals("api/v1/previsoes", StringComparison.OrdinalIgnoreCase) &&
            metodo.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            ConfigurarPrevisao(operation);
        }

        if (caminho.Equals("health", StringComparison.OrdinalIgnoreCase) &&
            metodo.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            DefinirResposta(
                operation,
                StatusCodes.Status200OK,
                new EstadoApiResponse(true, true),
                "API no ar, com ou sem modelo carregado.");
        }

        if (caminho.Equals("health/ready", StringComparison.OrdinalIgnoreCase) &&
            metodo.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            DefinirResposta(
                operation,
                StatusCodes.Status200OK,
                new EstadoApiResponse(true, true),
                "Modelo carregado; a API aceita previsões.");
        }
    }

    private static void ConfigurarPrevisao(OpenApiOperation operation)
    {
        operation.RequestBody ??= new OpenApiRequestBody();
        operation.RequestBody.Description =
            "Escolha um exemplo pronto no Swagger (Examples) ou envie o JSON completo. " +
            "Os exemplos abaixo passam na validação e usam as mesmas convenções do treino.";
        operation.RequestBody.Required = true;

        if (!operation.RequestBody.Content.TryGetValue("application/json", out var media))
        {
            media = new OpenApiMediaType();
            operation.RequestBody.Content["application/json"] = media;
        }

        var exemplos = new Dictionary<string, OpenApiExample>(StringComparer.Ordinal);
        foreach (var exemplo in ExemplosPrevisao.Todos)
        {
            exemplos[exemplo.Id] = new OpenApiExample
            {
                Summary = exemplo.Titulo,
                Description = exemplo.Descricao,
                Value = OpenApiJson.From(exemplo.Corpo)
            };
        }

        media.Example = OpenApiJson.From(ExemplosPrevisao.ApartamentoPinheiros);
        media.Examples = exemplos;

        DefinirResposta(
            operation,
            StatusCodes.Status200OK,
            new PrevisaoResponse(
                1_120_000,
                980_000,
                1_260_000,
                new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.Zero)),
            "Estimativa gerada pelo modelo treinado.");
    }

    private static void DefinirResposta(
        OpenApiOperation operation,
        int status,
        object corpo,
        string descricao)
    {
        var chave = status.ToString();
        if (!operation.Responses.TryGetValue(chave, out var resposta))
        {
            resposta = new OpenApiResponse();
            operation.Responses[chave] = resposta;
        }

        resposta.Description = descricao;
        if (!resposta.Content.TryGetValue("application/json", out var media))
        {
            media = new OpenApiMediaType();
            resposta.Content["application/json"] = media;
        }

        media.Example = OpenApiJson.From(corpo);
    }
}
