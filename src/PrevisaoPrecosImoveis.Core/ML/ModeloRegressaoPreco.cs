using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.Extensions.Logging;
using Microsoft.ML.AutoML;
using PrevisaoPrecosImoveis.Core.Models;
using System.Globalization;

namespace PrevisaoPrecosImoveis.Core.ML;

/// <summary>
/// Pipeline de Machine Learning com ML.NET para regressão de preços de imóveis.
/// 
/// Técnicas aplicadas (compatíveis com certificações de ML e projetos hands-on):
/// - Regressão (FastTree / SDCA / LightGBM via AutoML)
/// - Pré-processamento integrado no pipeline (OneHotEncoding, Normalize)
/// - Avaliação: R², RMSE, MAE
/// - Persistência do modelo treinado
/// </summary>
public class ModeloRegressaoPreco
{
    private const int MaximoAmostrasImportancia = 5000;
    private readonly MLContext _mlContext;
    private readonly ILogger<ModeloRegressaoPreco> _logger;
    private ITransformer? _modelo;
    private PredictionEngine<ImovelInput, ImovelOutput>? _engine;

    public string? AlgoritmoSelecionado { get; private set; }

    public IReadOnlyList<ImportanciaFeature> ImportanciasFeatures { get; private set; } =
        Array.Empty<ImportanciaFeature>();

    public MetricasHoldoutModelo? MetricasHoldout { get; private set; }

    public int QuantidadeTreino { get; private set; }

    public int QuantidadeTeste { get; private set; }

    public DateTime? DataInicioTeste { get; private set; }

    public DateTime? DataFimTeste { get; private set; }

    public ModeloRegressaoPreco(ILogger<ModeloRegressaoPreco> logger, int seed = 42)
    {
        _mlContext = new MLContext(seed);
        _logger = logger;
    }

    /// <summary>
    /// Classe de entrada para o ML.NET (features numéricas + categóricas).
    /// </summary>
    public class ImovelInput
    {
        public float AreaConstruidaM2 { get; set; }
        public float AreaTerrenoM2 { get; set; }
        public float Dormitorios { get; set; }
        public float Banheiros { get; set; }
        public float VagasGaragem { get; set; }
        public float AnoConstrucao { get; set; }
        public float IdadeImovel { get; set; }
        public float IpcaAcumulado12m { get; set; }
        public float SelicMeta { get; set; }
        public float IndiceFipeZapCidade { get; set; }
        public float ValorVenal { get; set; }
        public float DistanciaMetroKm { get; set; }
        public float DistanciaEscolaKm { get; set; }
        public float DistanciaParqueKm { get; set; }
        public float PossuiCoordenadas { get; set; }

        public string Cidade { get; set; } = "";
        public string Bairro { get; set; } = "";
        public string TipoImovel { get; set; } = "";
        public string PadraoConstrutivo { get; set; } = "";

        [ColumnName("Label")]
        public float ValorTransacao { get; set; }
    }

    public class ImovelOutput
    {
        [ColumnName("Score")]
        public float PrecoPrevisto { get; set; }
    }

    /// <summary>
    /// Treina e seleciona automaticamente um algoritmo de regressão.
    /// </summary>
    public void Treinar(
        IEnumerable<ImovelTransacao> dados,
        float proporcaoTeste = 0.2f,
        uint duracaoAutoMLSegundos = 30,
        string? pastaVisualizacoes = null)
    {
        if (duracaoAutoMLSegundos == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duracaoAutoMLSegundos),
                "A duração do experimento AutoML deve ser maior que zero.");
        }

        var transacoes = dados
            .Where(RegistroTreinavel)
            .OrderBy(t => t.DataTransacao)
            .ToList();
        var inputs = transacoes.Select(Mapear).ToList();
        if (inputs.Count < 3)
        {
            throw new ArgumentException(
                "São necessários ao menos três registros válidos para separar treino e teste temporal.",
                nameof(dados));
        }

        _logger.LogInformation(
            "Treinamento temporal com {Validos} registros válidos.",
            inputs.Count);

        if (proporcaoTeste <= 0f || proporcaoTeste >= 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(proporcaoTeste),
                "A proporção de teste deve estar entre zero e um.");
        }

        var quantidadeTeste = Math.Max(1, (int)Math.Ceiling(inputs.Count * proporcaoTeste));
        var quantidadeTreino = inputs.Count - quantidadeTeste;
        while (quantidadeTreino > 0 &&
               quantidadeTreino < transacoes.Count &&
               transacoes[quantidadeTreino - 1].DataTransacao ==
               transacoes[quantidadeTreino].DataTransacao)
        {
            quantidadeTreino--;
        }

        if (quantidadeTreino < 2)
        {
            throw new ArgumentException(
                "A proporção temporal deve reservar ao menos dois registros para treino.",
                nameof(proporcaoTeste));
        }

        var dadosTreino = inputs.Take(quantidadeTreino).ToList();
        var dadosTeste = inputs.Skip(quantidadeTreino).ToList();
        QuantidadeTreino = dadosTreino.Count;
        QuantidadeTeste = dadosTeste.Count;
        DataInicioTeste = transacoes[quantidadeTreino].DataTransacao;
        DataFimTeste = transacoes[^1].DataTransacao;
        ExecutarValidacaoCruzadaTemporal(
            dadosTreino,
            transacoes.Take(quantidadeTreino).Select(t => t.DataTransacao).ToList(),
            pastaVisualizacoes);
        var conjuntoTreino = _mlContext.Data.LoadFromEnumerable(dadosTreino);
        var conjuntoTeste = _mlContext.Data.LoadFromEnumerable(dadosTeste);
        var informacoesColunas = new ColumnInformation
        {
            LabelColumnName = "Label"
        };
        informacoesColunas.CategoricalColumnNames.Add(nameof(ImovelInput.Cidade));
        informacoesColunas.CategoricalColumnNames.Add(nameof(ImovelInput.Bairro));
        informacoesColunas.CategoricalColumnNames.Add(nameof(ImovelInput.TipoImovel));
        informacoesColunas.CategoricalColumnNames.Add(nameof(ImovelInput.PadraoConstrutivo));

        SelecionarEReajustarModelo(
            dadosTreino,
            transacoes.Take(quantidadeTreino).Select(t => t.DataTransacao).ToList(),
            conjuntoTreino,
            informacoesColunas,
            duracaoAutoMLSegundos);

        // Avaliação
        var modeloTreinado = _modelo
            ?? throw new InvalidOperationException("O treinamento não produziu um modelo.");
        var predicoes = modeloTreinado.Transform(conjuntoTeste);
        var metricas = _mlContext.Regression.Evaluate(predicoes, labelColumnName: "Label", scoreColumnName: "Score");
        MetricasHoldout = new MetricasHoldoutModelo(
            metricas.RSquared,
            metricas.RootMeanSquaredError,
            metricas.MeanAbsoluteError);

        _logger.LogInformation(
            "Métricas no conjunto de teste → Algoritmo: {Algoritmo} | R²: {R2:F4} | RMSE: {Rmse:F2} | MAE: {Mae:F2}",
            AlgoritmoSelecionado,
            metricas.RSquared, metricas.RootMeanSquaredError, metricas.MeanAbsoluteError);

        _engine = _mlContext.Model.CreatePredictionEngine<ImovelInput, ImovelOutput>(modeloTreinado);

        var dadosParaImportancia = _mlContext.Data
            .CreateEnumerable<ImovelInput>(conjuntoTeste, reuseRowObject: false)
            .Take(MaximoAmostrasImportancia)
            .Select(CopiarInput)
            .ToList();
        ImportanciasFeatures = CalcularImportancias(dadosParaImportancia);

        var residuos = _mlContext.Data
            .CreateEnumerable<ResiduoModelo>(
                predicoes,
                reuseRowObject: false)
            .ToList();

        if (!string.IsNullOrWhiteSpace(pastaVisualizacoes))
        {
            GeradorVisualizacoesModelo.Salvar(
                pastaVisualizacoes,
                ImportanciasFeatures,
                residuos);
            _logger.LogInformation(
                "Gráficos de importância de features e resíduos salvos em {Pasta}.",
                pastaVisualizacoes);
        }

    }

    /// <summary>
    /// Predição individual.
    /// </summary>
    public PredicaoPreco Prever(ImovelTransacao imovel)
    {
        if (_engine is null)
            throw new InvalidOperationException("Modelo não treinado. Chame Treinar() antes.");

        var input = Mapear(imovel);
        var output = _engine.Predict(input);

        // Intervalo de confiança simplificado (em produção usar quantiles ou bootstrap)
        var margem = output.PrecoPrevisto * 0.10f;

        return new PredicaoPreco
        {
            PrecoPrevisto = output.PrecoPrevisto,
            IntervaloConfiancaInferior = output.PrecoPrevisto - margem,
            IntervaloConfiancaSuperior = output.PrecoPrevisto + margem
        };
    }

    public void SalvarModelo(string caminho)
    {
        if (_modelo is null) throw new InvalidOperationException("Nenhum modelo treinado.");
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        _mlContext.Model.Save(_modelo, null, caminho);
        _logger.LogInformation("Modelo salvo em {Caminho}", caminho);
    }

    public void CarregarModelo(string caminho)
    {
        _modelo = _mlContext.Model.Load(caminho, out _);
        _engine = _mlContext.Model.CreatePredictionEngine<ImovelInput, ImovelOutput>(_modelo);
        _logger.LogInformation("Modelo carregado de {Caminho}", caminho);
    }

    private static ImovelInput Mapear(ImovelTransacao t) => new()
    {
        AreaConstruidaM2 = t.AreaConstruidaM2,
        AreaTerrenoM2 = t.AreaTerrenoM2,
        Dormitorios = t.Dormitorios,
        Banheiros = t.Banheiros,
        VagasGaragem = t.VagasGaragem,
        AnoConstrucao = t.AnoConstrucao,
        IdadeImovel = (t.DataTransacao == default ? DateTime.Today.Year : t.DataTransacao.Year) -
                      t.AnoConstrucao,
        IpcaAcumulado12m = t.IpcaAcumulado12m,
        SelicMeta = t.SelicMeta,
        IndiceFipeZapCidade = t.IndiceFipeZapCidade,
        ValorVenal = t.ValorVenal,
        DistanciaMetroKm = t.DistanciaMetroKm,
        DistanciaEscolaKm = t.DistanciaEscolaKm,
        DistanciaParqueKm = t.DistanciaParqueKm,
        PossuiCoordenadas = t.PossuiCoordenadas,
        Cidade = t.Cidade,
        Bairro = t.Bairro,
        TipoImovel = t.TipoImovel,
        PadraoConstrutivo = t.PadraoConstrutivo,
        ValorTransacao = t.ValorTransacao
    };

    private void TreinarModeloFallback(IDataView dadosTreino)
    {
        _logger.LogWarning("Treinando modelo FastTree de fallback.");
        _modelo = CriarPipelineFallback().Fit(dadosTreino);
        AlgoritmoSelecionado = "FastTree (fallback)";
    }

    private IEstimator<ITransformer> CriarPipelineFallback()
    {
        return _mlContext.Transforms.Categorical.OneHotEncoding(
                new[]
                {
                    new InputOutputColumnPair("CidadeEncoded", nameof(ImovelInput.Cidade)),
                    new InputOutputColumnPair("BairroEncoded", nameof(ImovelInput.Bairro)),
                    new InputOutputColumnPair("TipoEncoded", nameof(ImovelInput.TipoImovel)),
                    new InputOutputColumnPair("PadraoEncoded", nameof(ImovelInput.PadraoConstrutivo))
                })
            .Append(_mlContext.Transforms.Concatenate(
                "Features",
                nameof(ImovelInput.AreaConstruidaM2),
                nameof(ImovelInput.AreaTerrenoM2),
                nameof(ImovelInput.Dormitorios),
                nameof(ImovelInput.Banheiros),
                nameof(ImovelInput.VagasGaragem),
                nameof(ImovelInput.AnoConstrucao),
                nameof(ImovelInput.IdadeImovel),
                nameof(ImovelInput.IpcaAcumulado12m),
                nameof(ImovelInput.SelicMeta),
                nameof(ImovelInput.IndiceFipeZapCidade),
                nameof(ImovelInput.ValorVenal),
                nameof(ImovelInput.DistanciaMetroKm),
                nameof(ImovelInput.DistanciaEscolaKm),
                nameof(ImovelInput.DistanciaParqueKm),
                nameof(ImovelInput.PossuiCoordenadas),
                "CidadeEncoded",
                "BairroEncoded",
                "TipoEncoded",
                "PadraoEncoded"))
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.Regression.Trainers.FastTree(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 20,
                numberOfTrees: 100,
                minimumExampleCountPerLeaf: 10));
    }

    private void SelecionarEReajustarModelo(
        IReadOnlyList<ImovelInput> dadosTreino,
        IReadOnlyList<DateTime> datasTreino,
        IDataView conjuntoTreinoCompleto,
        ColumnInformation informacoesColunas,
        uint duracaoAutoMLSegundos)
    {
        var fracaoValidacao = 0.2f;
        var quantidadeValidacao = Math.Max(
            1,
            (int)Math.Ceiling(dadosTreino.Count * fracaoValidacao));
        var quantidadeTreinoSelecao = dadosTreino.Count - quantidadeValidacao;

        while (quantidadeTreinoSelecao > 0 &&
               quantidadeTreinoSelecao < dadosTreino.Count &&
               datasTreino[quantidadeTreinoSelecao - 1] ==
               datasTreino[quantidadeTreinoSelecao])
        {
            quantidadeTreinoSelecao--;
        }

        if (quantidadeTreinoSelecao < 2 || quantidadeTreinoSelecao >= dadosTreino.Count)
        {
            _logger.LogWarning(
                "Dados insuficientes para a seleção AutoML com validação cronológica; usando FastTree.");
            TreinarModeloFallback(conjuntoTreinoCompleto);
            return;
        }

        var dadosTreinoSelecao = dadosTreino.Take(quantidadeTreinoSelecao).ToList();
        var dadosValidacaoSelecao = dadosTreino.Skip(quantidadeTreinoSelecao).ToList();
        _logger.LogInformation(
            "Seleção AutoML com validação temporal: {Treino} registros anteriores e {Validacao} registros posteriores.",
            dadosTreinoSelecao.Count,
            dadosValidacaoSelecao.Count);

        try
        {
            var experimento = _mlContext.Auto().CreateRegressionExperiment(
                new RegressionExperimentSettings
                {
                    MaxExperimentTimeInSeconds = duracaoAutoMLSegundos,
                    OptimizingMetric = RegressionMetric.RootMeanSquaredError
                });
            var resultado = experimento.Execute(
                _mlContext.Data.LoadFromEnumerable(dadosTreinoSelecao),
                _mlContext.Data.LoadFromEnumerable(dadosValidacaoSelecao),
                informacoesColunas);
            var melhorExecucao = resultado.BestRun;

            if (melhorExecucao?.Estimator is null)
            {
                _logger.LogWarning(
                    "O AutoML não selecionou um pipeline válido; usando FastTree de fallback.");
                TreinarModeloFallback(conjuntoTreinoCompleto);
                return;
            }

            _modelo = melhorExecucao.Estimator.Fit(conjuntoTreinoCompleto);
            AlgoritmoSelecionado = melhorExecucao.TrainerName;
            _logger.LogInformation(
                "AutoML selecionou {Algoritmo} (RMSE da validação temporal: {Rmse:F2}); " +
                "pipeline reajustado nos {Amostras} registros anteriores ao holdout.",
                AlgoritmoSelecionado,
                melhorExecucao.ValidationMetrics?.RootMeanSquaredError,
                dadosTreino.Count);
        }
        catch (AggregateException ex) when (SomenteTimeoutsAutoMl(ex))
        {
            _logger.LogWarning(
                ex,
                "O AutoML esgotou o limite de {Duracao}s sem selecionar um modelo. Usando FastTree.",
                duracaoAutoMLSegundos);
            TreinarModeloFallback(conjuntoTreinoCompleto);
        }
    }

    private void ExecutarValidacaoCruzadaTemporal(
        IReadOnlyList<ImovelInput> dadosTreino,
        IReadOnlyList<DateTime> datasTreino,
        string? pastaVisualizacoes)
    {
        const int quantidadeFolds = 3;
        var datasOrdenadas = datasTreino.ToList();
        var datasUnicas = datasOrdenadas.Distinct().ToList();
        var inicioDataValidacao = datasUnicas.Count / 2;
        if (datasUnicas.Count - inicioDataValidacao < quantidadeFolds ||
            inicioDataValidacao < 1)
        {
            _logger.LogWarning(
                "Validação cruzada temporal ignorada: datas distintas insuficientes no período de treino.");
            return;
        }

        var larguraFold = (datasUnicas.Count - inicioDataValidacao) / quantidadeFolds;
        var resultados = new List<ResultadoFoldTemporal>(quantidadeFolds);
        var fimAnterior = datasOrdenadas.IndexOf(datasUnicas[inicioDataValidacao]);

        for (var fold = 0; fold < quantidadeFolds; fold++)
        {
            var fimDataValidacao = fold == quantidadeFolds - 1
                ? datasUnicas.Count
                : inicioDataValidacao + (fold + 1) * larguraFold;
            var fimValidacao = fimDataValidacao == datasUnicas.Count
                ? dadosTreino.Count
                : datasOrdenadas.IndexOf(datasUnicas[fimDataValidacao]);
            var treinamentoFold = dadosTreino.Take(fimAnterior).ToList();
            var validacaoFold = dadosTreino.Skip(fimAnterior)
                .Take(fimValidacao - fimAnterior)
                .ToList();

            var modeloFold = CriarPipelineFallback()
                .Fit(_mlContext.Data.LoadFromEnumerable(treinamentoFold));
            var predicoesFold = modeloFold.Transform(
                _mlContext.Data.LoadFromEnumerable(validacaoFold));
            var metricas = _mlContext.Regression.Evaluate(
                predicoesFold,
                labelColumnName: "Label",
                scoreColumnName: "Score");
            resultados.Add(new ResultadoFoldTemporal(
                fold + 1,
                treinamentoFold.Count,
                validacaoFold.Count,
                metricas.RootMeanSquaredError,
                metricas.MeanAbsoluteError,
                metricas.RSquared,
                datasTreino[fimAnterior],
                datasTreino[fimValidacao - 1]));
            _logger.LogInformation(
                "Validação temporal fold {Fold}/{Total}: treino={Treino}, validação={Validacao} ({Inicio:yyyy-MM-dd} a {Fim:yyyy-MM-dd}), RMSE={Rmse:F2}, MAE={Mae:F2}, R²={R2:F4}.",
                fold + 1,
                quantidadeFolds,
                treinamentoFold.Count,
                validacaoFold.Count,
                datasTreino[fimAnterior],
                datasTreino[fimValidacao - 1],
                metricas.RootMeanSquaredError,
                metricas.MeanAbsoluteError,
                metricas.RSquared);
            fimAnterior = fimValidacao;
        }

        if (!string.IsNullOrWhiteSpace(pastaVisualizacoes))
        {
            SalvarResultadosValidacaoTemporal(
                Path.Combine(pastaVisualizacoes, "validacao_cruzada_temporal.csv"),
                resultados);
        }
    }

    private static void SalvarResultadosValidacaoTemporal(
        string caminho,
        IReadOnlyList<ResultadoFoldTemporal> resultados)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        var linhas = new List<string>(resultados.Count + 1)
        {
            "Fold,AmostrasTreino,AmostrasValidacao,DataInicioValidacao,DataFimValidacao,RMSE,MAE,RSquared"
        };
        linhas.AddRange(resultados.Select(resultado => string.Join(",",
            resultado.Fold.ToString(CultureInfo.InvariantCulture),
            resultado.AmostrasTreino.ToString(CultureInfo.InvariantCulture),
            resultado.AmostrasValidacao.ToString(CultureInfo.InvariantCulture),
            resultado.DataInicioValidacao.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            resultado.DataFimValidacao.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            resultado.Rmse.ToString("R", CultureInfo.InvariantCulture),
            resultado.Mae.ToString("R", CultureInfo.InvariantCulture),
            resultado.RSquared.ToString("R", CultureInfo.InvariantCulture))));
        File.WriteAllLines(caminho, linhas);
    }

    private static bool SomenteTimeoutsAutoMl(AggregateException exception)
    {
        var erros = exception.Flatten().InnerExceptions;
        return erros.Count > 0 && erros.All(erro => erro is TimeoutException);
    }

    private static bool RegistroTreinavel(ImovelTransacao transacao)
    {
        return transacao.ValorTransacao > 0 &&
               float.IsFinite(transacao.ValorTransacao) &&
               float.IsFinite(transacao.AreaConstruidaM2) &&
               float.IsFinite(transacao.AreaTerrenoM2) &&
               float.IsFinite(transacao.ValorVenal) &&
               float.IsFinite(transacao.IpcaAcumulado12m) &&
               float.IsFinite(transacao.SelicMeta) &&
               float.IsFinite(transacao.IndiceFipeZapCidade) &&
               float.IsFinite(transacao.DistanciaMetroKm) &&
               float.IsFinite(transacao.DistanciaEscolaKm) &&
               float.IsFinite(transacao.DistanciaParqueKm) &&
               float.IsFinite(transacao.PossuiCoordenadas);
    }

    private IReadOnlyList<ImportanciaFeature> CalcularImportancias(
        IReadOnlyList<ImovelInput> dadosTeste)
    {
        if (dadosTeste.Count == 0 || _modelo is null)
        {
            return Array.Empty<ImportanciaFeature>();
        }

        var baselineData = _mlContext.Data.LoadFromEnumerable(dadosTeste);
        var baselineMetrics = _mlContext.Regression.Evaluate(
            _modelo.Transform(baselineData),
            labelColumnName: "Label",
            scoreColumnName: "Score");
        var random = new Random(42);

        var importancias = new List<ImportanciaFeature>
        {
            MedirImportancia("Area construída (m²)", dadosTeste, x => x.AreaConstruidaM2, (x, v) => x.AreaConstruidaM2 = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Área do terreno (m²)", dadosTeste, x => x.AreaTerrenoM2, (x, v) => x.AreaTerrenoM2 = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Dormitórios", dadosTeste, x => x.Dormitorios, (x, v) => x.Dormitorios = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Banheiros", dadosTeste, x => x.Banheiros, (x, v) => x.Banheiros = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Vagas de garagem", dadosTeste, x => x.VagasGaragem, (x, v) => x.VagasGaragem = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Ano de construção", dadosTeste, x => x.AnoConstrucao, (x, v) => x.AnoConstrucao = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Idade do imóvel", dadosTeste, x => x.IdadeImovel, (x, v) => x.IdadeImovel = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("IPCA 12 meses", dadosTeste, x => x.IpcaAcumulado12m, (x, v) => x.IpcaAcumulado12m = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Meta Selic", dadosTeste, x => x.SelicMeta, (x, v) => x.SelicMeta = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Índice FipeZap", dadosTeste, x => x.IndiceFipeZapCidade, (x, v) => x.IndiceFipeZapCidade = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Valor venal", dadosTeste, x => x.ValorVenal, (x, v) => x.ValorVenal = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Distância ao metrô (km)", dadosTeste, x => x.DistanciaMetroKm, (x, v) => x.DistanciaMetroKm = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Distância a escolas (km)", dadosTeste, x => x.DistanciaEscolaKm, (x, v) => x.DistanciaEscolaKm = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Distância a parques (km)", dadosTeste, x => x.DistanciaParqueKm, (x, v) => x.DistanciaParqueKm = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Possui coordenadas", dadosTeste, x => x.PossuiCoordenadas, (x, v) => x.PossuiCoordenadas = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Cidade", dadosTeste, x => x.Cidade, (x, v) => x.Cidade = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Bairro", dadosTeste, x => x.Bairro, (x, v) => x.Bairro = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Tipo do imóvel", dadosTeste, x => x.TipoImovel, (x, v) => x.TipoImovel = v, random, baselineMetrics.RootMeanSquaredError),
            MedirImportancia("Padrão construtivo", dadosTeste, x => x.PadraoConstrutivo, (x, v) => x.PadraoConstrutivo = v, random, baselineMetrics.RootMeanSquaredError)
        };

        return importancias
            .OrderByDescending(item => item.AumentoRmse)
            .ToList();
    }

    private ImportanciaFeature MedirImportancia<T>(
        string nome,
        IReadOnlyList<ImovelInput> dadosTeste,
        Func<ImovelInput, T> obter,
        Action<ImovelInput, T> atribuir,
        Random random,
        double rmseBaseline)
    {
        var permutados = dadosTeste.Select(CopiarInput).ToList();
        var valores = permutados.Select(obter).ToArray();
        var indices = Enumerable.Range(0, valores.Length).ToArray();
        for (var indice = indices.Length - 1; indice > 0; indice--)
        {
            var outroIndice = random.Next(indice + 1);
            (indices[indice], indices[outroIndice]) = (indices[outroIndice], indices[indice]);
        }

        for (var indice = 0; indice < permutados.Count; indice++)
        {
            atribuir(permutados[indice], valores[indices[indice]]);
        }

        var dadosPermutados = _mlContext.Data.LoadFromEnumerable(permutados);
        var metricasPermutadas = _mlContext.Regression.Evaluate(
            _modelo!.Transform(dadosPermutados),
            labelColumnName: "Label",
            scoreColumnName: "Score");

        return new ImportanciaFeature(nome, metricasPermutadas.RootMeanSquaredError - rmseBaseline);
    }

    private static ImovelInput CopiarInput(ImovelInput input) => new()
    {
        AreaConstruidaM2 = input.AreaConstruidaM2,
        AreaTerrenoM2 = input.AreaTerrenoM2,
        Dormitorios = input.Dormitorios,
        Banheiros = input.Banheiros,
        VagasGaragem = input.VagasGaragem,
        AnoConstrucao = input.AnoConstrucao,
        IdadeImovel = input.IdadeImovel,
        IpcaAcumulado12m = input.IpcaAcumulado12m,
        SelicMeta = input.SelicMeta,
        IndiceFipeZapCidade = input.IndiceFipeZapCidade,
        ValorVenal = input.ValorVenal,
        DistanciaMetroKm = input.DistanciaMetroKm,
        DistanciaEscolaKm = input.DistanciaEscolaKm,
        DistanciaParqueKm = input.DistanciaParqueKm,
        PossuiCoordenadas = input.PossuiCoordenadas,
        Cidade = input.Cidade,
        Bairro = input.Bairro,
        TipoImovel = input.TipoImovel,
        PadraoConstrutivo = input.PadraoConstrutivo,
        ValorTransacao = input.ValorTransacao
    };
}

internal sealed record ResultadoFoldTemporal(
    int Fold,
    int AmostrasTreino,
    int AmostrasValidacao,
    double Rmse,
    double Mae,
    double RSquared,
    DateTime DataInicioValidacao,
    DateTime DataFimValidacao);

public sealed record ImportanciaFeature(string Nome, double AumentoRmse);

public sealed record MetricasHoldoutModelo(double RSquared, double Rmse, double Mae);

public sealed class ResiduoModelo
{
    [ColumnName("Label")]
    public float ValorReal { get; set; }

    [ColumnName("Score")]
    public float ValorPrevisto { get; set; }

    public float Residuo => ValorReal - ValorPrevisto;
}
