using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrevisaoPrecosImoveis.Core.DataCollection;
using PrevisaoPrecosImoveis.Core.ML;
using PrevisaoPrecosImoveis.Core.Models;
using PrevisaoPrecosImoveis.Core.Preprocessing;

// ============================================================
// Esqueleto – Previsão de Preços de Imóveis no Brasil
// Técnicas: coleta de dados públicos, pré-processamento,
// regressão com ML.NET (compatível com currículo de IA/ML)
// ============================================================

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddHttpClient();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

        services.AddSingleton<ColetorItbiSaoPaulo>();
        services.AddSingleton<ColetorBcb>();
        services.AddSingleton<ColetorFipeZap>();
        services.AddSingleton<PreprocessadorDados>();
        services.AddSingleton<EnriquecedorEspacial>();
        services.AddSingleton<ModeloRegressaoPreco>();
    })
    .Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Main");
var coletorItbi = host.Services.GetRequiredService<ColetorItbiSaoPaulo>();
var coletorBcb = host.Services.GetRequiredService<ColetorBcb>();
var preprocessador = host.Services.GetRequiredService<PreprocessadorDados>();
var enriquecedorEspacial = host.Services.GetRequiredService<EnriquecedorEspacial>();
var modelo = host.Services.GetRequiredService<ModeloRegressaoPreco>();

logger.LogInformation("=== Pipeline de Previsão de Preços de Imóveis (Brasil) ===");

// -------------------------------------------------------
// 1. COLETA DE DADOS PÚBLICOS
// -------------------------------------------------------
logger.LogInformation("1) Coletando dados de fontes governamentais...");

// Exemplo: séries macro do BCB (IPCA e Selic)
var inicio = new DateTime(2018, 1, 1);
var fim = DateTime.Today;

var serieIpca = await coletorBcb.ColetarSerieAsync(SeriesBcb.IpcaAcumulado12Meses, inicio, fim);
var serieSelic = await coletorBcb.ColetarSerieAsync(SeriesBcb.SelicMeta, inicio, fim);

logger.LogInformation("   IPCA (12m): {Count} pontos | Selic: {Count2} pontos",
    serieIpca.Count, serieSelic.Count);

// ITBI São Paulo (colocar arquivos Excel em data/raw/itbi_sp)
var transacoesItbi = await coletorItbi.ColetarAsync();
logger.LogInformation("   Transações ITBI-SP carregadas: {Count}", transacoesItbi.Count);
var registrosIgnorados = coletorItbi.RegistrosIgnorados.ToList();

// FipeZap (colocar planilha em data/raw/fipezap)
var indicesFipe = await host.Services.GetRequiredService<ColetorFipeZap>().ColetarAsync();
logger.LogInformation("   Pontos FipeZap: {Count}", indicesFipe.Count);

// -------------------------------------------------------
// 2. PRÉ-PROCESSAMENTO
// -------------------------------------------------------
logger.LogInformation("2) Pré-processamento e feature engineering...");

var transacoesEspaciais = await enriquecedorEspacial.EnriquecerAsync(transacoesItbi);
var enriquecidas = preprocessador.EnriquecerComMacro(transacoesEspaciais, serieIpca, serieSelic);
var df = preprocessador.ConverterParaDataFrame(enriquecidas);
df = preprocessador.Limpar(df, registrosIgnorados);
df = preprocessador.CriarFeatures(df);
var transacoesValidas = enriquecidas
    .Where(transacao =>
        float.IsFinite(transacao.AreaConstruidaM2) &&
        transacao.AreaConstruidaM2 > 0f &&
        float.IsFinite(transacao.ValorTransacao) &&
        transacao.ValorTransacao > 0f)
    .ToList();

var caminhoProcessado = Path.Combine("data", "processed", "imoveis_processados.csv");
preprocessador.SalvarCsv(df, caminhoProcessado);

// -------------------------------------------------------
// 3. TREINAMENTO DO MODELO DE REGRESSÃO
// -------------------------------------------------------
logger.LogInformation("3) Selecionando algoritmo e treinando modelo de regressão com ML.NET AutoML...");
var pastaVisualizacoes = "visualizacoes";
IReadOnlyList<ImovelTransacao> dadosParaTreinoRelatorio = transacoesValidas;
var usouDadosSinteticos = false;

if (transacoesValidas.Count >= 50) // mínimo razoável para treino
{
    modelo.Treinar(
        transacoesValidas,
        proporcaoTeste: 0.2f,
        pastaVisualizacoes: pastaVisualizacoes);
    modelo.SalvarModelo(Path.Combine("data", "models", "modelo_preco_imovel.zip"));
}
else
{
    logger.LogWarning(
        "Poucos dados reais carregados ({Count}). " +
        "Baixe os arquivos ITBI de https://prefeitura.sp.gov.br/... e coloque em data/raw/itbi_sp/",
        transacoesValidas.Count);

    // Demonstração com dados sintéticos para o esqueleto funcionar
    var sinteticos = GerarDadosSinteticos(200);
    var sinteticosEnriquecidos = preprocessador.EnriquecerComMacro(sinteticos, serieIpca, serieSelic);
    dadosParaTreinoRelatorio = sinteticosEnriquecidos;
    usouDadosSinteticos = true;
    modelo.Treinar(
        sinteticosEnriquecidos,
        proporcaoTeste: 0.2f,
        pastaVisualizacoes: pastaVisualizacoes);
    modelo.SalvarModelo(Path.Combine("data", "models", "modelo_preco_imovel.zip"));
}

// -------------------------------------------------------
// 4. EXEMPLO DE PREDIÇÃO
// -------------------------------------------------------
logger.LogInformation("4) Exemplo de predição...");

var imovelExemplo = new ImovelTransacao
{
    Cidade = "São Paulo",
    Bairro = "Pinheiros",
    AreaConstruidaM2 = 85f,
    AreaTerrenoM2 = 0f,
    Dormitorios = 2,
    Banheiros = 2,
    VagasGaragem = 1,
    AnoConstrucao = 2015,
    TipoImovel = "Apartamento",
    PadraoConstrutivo = "Medio",
    ValorVenal = 650_000f,
    IpcaAcumulado12m = 4.5f,
    SelicMeta = 10.5f,
    IndiceFipeZapCidade = 1.05f
};

var predicao = modelo.Prever(imovelExemplo);
logger.LogInformation(
    "Preço previsto: R$ {Preco:N0}  (intervalo aprox. R$ {Inf:N0} – R$ {Sup:N0})",
    predicao.PrecoPrevisto,
    predicao.IntervaloConfiancaInferior,
    predicao.IntervaloConfiancaSuperior);

var caminhoRelatorio = Path.Combine(pastaVisualizacoes, "relatorio_modelo.pdf");
RelatorioPdfModelo.Gerar(new RelatorioPdfDados(
    caminhoRelatorio,
    pastaVisualizacoes,
    dadosParaTreinoRelatorio,
    transacoesItbi.Count,
    serieIpca.Count,
    serieSelic.Count,
    indicesFipe.Count,
    (int)df.Rows.Count,
    usouDadosSinteticos,
    Directory.Exists(Path.Combine("data", "raw", "itbi_sp"))
        ? Directory.GetFiles(Path.Combine("data", "raw", "itbi_sp"), "*.xlsx")
            .Select(Path.GetFileName)
            .Where(nome => nome is not null)
            .Select(nome => nome!)
            .ToList()
        : [],
    Directory.Exists(Path.Combine("data", "raw", "fipezap"))
        ? Directory.GetFiles(Path.Combine("data", "raw", "fipezap"), "*.xlsx")
            .Select(Path.GetFileName)
            .Where(nome => nome is not null)
            .Select(nome => nome!)
            .ToList()
        : [],
    registrosIgnorados,
    modelo));
logger.LogInformation("Relatório PDF de dados e modelo salvo em {Caminho}", caminhoRelatorio);

logger.LogInformation("=== Pipeline finalizado ===");
return;

// -------------------------------------------------------
// Helper: dados sintéticos apenas para demonstração do esqueleto
// -------------------------------------------------------
static List<ImovelTransacao> GerarDadosSinteticos(int n)
{
    var rnd = new Random(42);
    var bairros = new[] { "Pinheiros", "Moema", "Santana", "Tatuapé", "Brooklin", "Vila Mariana" };
    var lista = new List<ImovelTransacao>();

    for (int i = 0; i < n; i++)
    {
        var area = 40f + rnd.NextSingle() * 160f;
        var precoM2 = 8_000f + rnd.NextSingle() * 12_000f;
        lista.Add(new ImovelTransacao
        {
            Id = Guid.NewGuid().ToString("N"),
            Fonte = "Sintetico",
            Cidade = "São Paulo",
            Bairro = bairros[rnd.Next(bairros.Length)],
            DataTransacao = new DateTime(2019 + rnd.Next(6), rnd.Next(1, 13), 1),
            AreaConstruidaM2 = area,
            Dormitorios = rnd.Next(1, 5),
            Banheiros = rnd.Next(1, 4),
            VagasGaragem = rnd.Next(0, 3),
            AnoConstrucao = 1990 + rnd.Next(35),
            TipoImovel = "Apartamento",
            PadraoConstrutivo = rnd.Next(3) switch { 0 => "Baixo", 1 => "Medio", _ => "Alto" },
            ValorTransacao = area * precoM2,
            ValorVenal = area * precoM2 * 0.7f
        });
    }
    return lista;
}
