using Microsoft.Extensions.Logging;
using PrevisaoPrecosImoveis.Api.Contracts;
using PrevisaoPrecosImoveis.Core.ML;
using PrevisaoPrecosImoveis.Core.Models;

namespace PrevisaoPrecosImoveis.Api.Services;

public sealed class PredictionService : IPredictionService
{
    private readonly ModeloRegressaoPreco _modelo;
    private readonly ILogger<PredictionService> _logger;
    private readonly object _predictionLock = new();

    public bool ModeloCarregado { get; }

    public string CaminhoModelo { get; }

    public PredictionService(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory,
        ILogger<PredictionService> logger)
    {
        _logger = logger;
        _modelo = new ModeloRegressaoPreco(
            loggerFactory.CreateLogger<ModeloRegressaoPreco>());

        var caminhoConfigurado = configuration["ModelPath"]
            ?? Path.Combine("data", "models", "modelo_preco_imovel.zip");
        var raizProjeto = EncontrarRaizProjeto(environment.ContentRootPath);
        var caminhoModelo = Path.IsPathRooted(caminhoConfigurado)
            ? caminhoConfigurado
            : Path.GetFullPath(caminhoConfigurado, raizProjeto);

        CaminhoModelo = caminhoModelo;
        if (!File.Exists(caminhoModelo))
        {
            _logger.LogWarning(
                "Modelo de predição não encontrado em {Caminho}. Execute o pipeline de treino antes de chamar a API.",
                caminhoModelo);
            return;
        }

        _modelo.CarregarModelo(caminhoModelo);
        ModeloCarregado = true;
    }

    public PrevisaoResponse Prever(CreatePrevisaoRequest request)
    {
        if (!ModeloCarregado)
        {
            throw new InvalidOperationException("O modelo de predição não está carregado.");
        }

        var imovel = new ImovelTransacao
        {
            Cidade = request.Cidade.Trim(),
            Bairro = request.Bairro.Trim(),
            AreaConstruidaM2 = request.AreaConstruidaM2,
            AreaTerrenoM2 = request.AreaTerrenoM2,
            Dormitorios = request.Dormitorios,
            Banheiros = request.Banheiros,
            VagasGaragem = request.VagasGaragem,
            AnoConstrucao = request.AnoConstrucao,
            TipoImovel = request.TipoImovel.Trim(),
            PadraoConstrutivo = request.PadraoConstrutivo.Trim(),
            ValorVenal = request.ValorVenal,
            IpcaAcumulado12m = request.IpcaAcumulado12m,
            SelicMeta = request.SelicMeta,
            IndiceFipeZapCidade = request.IndiceFipeZapCidade,
            DistanciaMetroKm = request.DistanciaMetroKm,
            DistanciaEscolaKm = request.DistanciaEscolaKm,
            DistanciaParqueKm = request.DistanciaParqueKm,
            PossuiCoordenadas = request.PossuiCoordenadas ? 1f : 0f,
            DataTransacao = request.DataTransacao?.UtcDateTime ?? DateTime.UtcNow
        };

        PredicaoPreco predicao;
        lock (_predictionLock)
        {
            predicao = _modelo.Prever(imovel);
        }

        return new PrevisaoResponse(
            predicao.PrecoPrevisto,
            predicao.IntervaloConfiancaInferior,
            predicao.IntervaloConfiancaSuperior,
            DateTimeOffset.UtcNow);
    }

    private static string EncontrarRaizProjeto(string contentRoot)
    {
        var candidatos = new[] { contentRoot, Directory.GetCurrentDirectory() };
        foreach (var candidato in candidatos)
        {
            var diretorio = new DirectoryInfo(candidato);
            while (diretorio is not null)
            {
                if (File.Exists(Path.Combine(diretorio.FullName, "PrevisaoPrecosImoveis.sln")))
                {
                    return diretorio.FullName;
                }

                diretorio = diretorio.Parent;
            }
        }

        return contentRoot;
    }
}
