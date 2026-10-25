using Microsoft.AspNetCore.Http.HttpResults;
using PrevisaoPrecosImoveis.Api.Contracts;
using PrevisaoPrecosImoveis.Api.Services;

namespace PrevisaoPrecosImoveis.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", (
                IPredictionService service,
                CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return TypedResults.Ok(new EstadoApiResponse(true, service.ModeloCarregado));
            })
            .WithName("Health")
            .WithTags("Saúde")
            .WithSummary("Verifica se a API está em execução")
            .WithDescription("Verifica a disponibilidade do processo da API, independentemente de o modelo estar carregado.")
            .Produces<EstadoApiResponse>(StatusCodes.Status200OK)
            .WithOpenApi();

        app.MapGet("/health/ready", Results<Ok<EstadoApiResponse>, ProblemHttpResult> (
                IPredictionService service,
                CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return service.ModeloCarregado
                    ? TypedResults.Ok(new EstadoApiResponse(true, true))
                    : TypedResults.Problem(
                        title: "Modelo indisponível",
                        detail: "Execute o pipeline de treinamento para gerar o arquivo do modelo.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("Readiness")
            .WithTags("Saúde")
            .WithSummary("Verifica se a API está pronta para prever")
            .WithDescription("Retorna 503 enquanto o arquivo do modelo treinado não estiver disponível.")
            .Produces<EstadoApiResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi();
    }
}
