using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using PrevisaoPrecosImoveis.Api.Contracts;
using PrevisaoPrecosImoveis.Api.Services;

namespace PrevisaoPrecosImoveis.Api.Endpoints;

public static class PredictionEndpoints
{
    public static void MapPredictionEndpoints(this WebApplication app)
    {
        var previsoes = app.MapGroup("/api/v1")
            .WithTags("Previsões");

        previsoes.MapPost("/previsoes", Results<Ok<PrevisaoResponse>, ValidationProblem, ProblemHttpResult> (
                CreatePrevisaoRequest request,
                IPredictionService service,
                CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var erros = Validar(request);
                if (erros.Count > 0)
                {
                    return TypedResults.ValidationProblem(erros);
                }

                if (!service.ModeloCarregado)
                {
                    return TypedResults.Problem(
                        title: "Modelo indisponível",
                        detail: "Execute o pipeline de treinamento para gerar o arquivo do modelo.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return TypedResults.Ok(service.Prever(request));
            })
            .WithName("PreverPrecoImovel")
            .WithSummary("Estima o preço de uma transação imobiliária")
            .WithDescription(
                "Recebe características do imóvel e distâncias espaciais para gerar uma estimativa. " +
                "No Swagger, use o seletor **Examples**: Apartamento em Pinheiros, Casa em Moema, " +
                "apartamento compacto em Santana, imóvel em Curitiba ou o caso sem coordenadas. " +
                "Distâncias em quilômetros; a estimativa para Curitiba depende de dados reais da cidade no treino.")
            .Produces<PrevisaoResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi();

        previsoes.MapGet("/previsoes/exemplos", () => TypedResults.Ok(ExemplosPrevisao.Todos))
            .WithName("ListarExemplosPrevisao")
            .WithSummary("Lista corpos de requisição prontos e válidos")
            .WithDescription(
                "Devolve os mesmos exemplos do Swagger para copiar, testar ou integrar um cliente.")
            .Produces<IReadOnlyList<ExemploPrevisaoDocumentado>>(StatusCodes.Status200OK)
            .WithOpenApi();

        previsoes.MapGet("/modelo", (IPredictionService service) =>
                TypedResults.Ok(new ModeloInfoResponse(
                    service.ModeloCarregado,
                    service.CaminhoModelo,
                    service.ModeloCarregado
                        ? "Modelo de regressão carregado e pronto para POST /api/v1/previsoes."
                        : "Arquivo do modelo não encontrado. Execute o pipeline de treino do console.",
                    [
                        "cidade", "bairro", "areaConstruidaM2", "areaTerrenoM2", "dormitorios",
                        "banheiros", "vagasGaragem", "anoConstrucao", "tipoImovel",
                        "padraoConstrutivo", "valorVenal", "ipcaAcumulado12m", "selicMeta",
                        "indiceFipeZapCidade", "distanciaMetroKm", "distanciaEscolaKm",
                        "distanciaParqueKm", "possuiCoordenadas", "dataTransacao"
                    ])))
            .WithName("ObterInfoModelo")
            .WithTags("Modelo")
            .WithSummary("Mostra se o modelo treinado está carregado")
            .WithDescription(
                "Informe o caminho do arquivo e os campos esperados em POST /api/v1/previsoes.")
            .Produces<ModeloInfoResponse>(StatusCodes.Status200OK)
            .WithOpenApi();
    }

    private static Dictionary<string, string[]> Validar(CreatePrevisaoRequest request)
    {
        var resultados = new List<ValidationResult>();
        var contexto = new ValidationContext(request);
        Validator.TryValidateObject(request, contexto, resultados, validateAllProperties: true);
        return resultados
            .SelectMany(resultado => resultado.MemberNames.DefaultIfEmpty(string.Empty)
                .Select(membro => (Membro: membro, Erro: resultado.ErrorMessage ?? "Valor inválido.")))
            .GroupBy(item => item.Membro, StringComparer.Ordinal)
            .ToDictionary(
                grupo => grupo.Key,
                grupo => grupo.Select(item => item.Erro).Distinct().ToArray(),
                StringComparer.Ordinal);
    }
}
