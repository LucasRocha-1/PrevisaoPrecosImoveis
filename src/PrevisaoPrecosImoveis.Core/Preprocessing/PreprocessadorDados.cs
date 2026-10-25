using Microsoft.Data.Analysis;
using Microsoft.Extensions.Logging;
using PrevisaoPrecosImoveis.Core.Models;
using System.Globalization;

namespace PrevisaoPrecosImoveis.Core.Preprocessing;

/// <summary>
/// Pipeline de pré-processamento alinhado às certificações:
/// Análise de Dados, Pré-processamento de Dados e Visualização.
/// 
/// Etapas clássicas de Data Science:
/// 1. Limpeza (nulos, outliers)
/// 2. Feature engineering
/// 3. Encoding categórico
/// 4. Normalização / scaling
/// 5. Preparação para divisão temporal treino/teste
/// </summary>
public class PreprocessadorDados
{
    private readonly ILogger<PreprocessadorDados> _logger;

    public PreprocessadorDados(ILogger<PreprocessadorDados> logger)
    {
        _logger = logger;
    }

    public DataFrame ConverterParaDataFrame(IEnumerable<ImovelTransacao> transacoes)
    {
        var lista = transacoes.ToList();
        _logger.LogInformation("Convertendo {Count} registros para DataFrame...", lista.Count);

        var df = new DataFrame(
            new StringDataFrameColumn("Id", lista.Select(t => t.Id)),
            new PrimitiveDataFrameColumn<DateTime>("DataTransacao", lista.Select(t => t.DataTransacao)),
            new StringDataFrameColumn("Cidade", lista.Select(t => t.Cidade)),
            new StringDataFrameColumn("Bairro", lista.Select(t => t.Bairro)),
            new PrimitiveDataFrameColumn<float>("AreaConstruidaM2", lista.Select(t => t.AreaConstruidaM2)),
            new PrimitiveDataFrameColumn<float>("AreaTerrenoM2", lista.Select(t => t.AreaTerrenoM2)),
            new PrimitiveDataFrameColumn<int>("Dormitorios", lista.Select(t => t.Dormitorios)),
            new PrimitiveDataFrameColumn<int>("Banheiros", lista.Select(t => t.Banheiros)),
            new PrimitiveDataFrameColumn<int>("VagasGaragem", lista.Select(t => t.VagasGaragem)),
            new PrimitiveDataFrameColumn<int>("AnoConstrucao", lista.Select(t => t.AnoConstrucao)),
            new StringDataFrameColumn("TipoImovel", lista.Select(t => t.TipoImovel)),
            new StringDataFrameColumn("PadraoConstrutivo", lista.Select(t => t.PadraoConstrutivo)),
            new PrimitiveDataFrameColumn<float>("ValorTransacao", lista.Select(t => t.ValorTransacao)),
            new PrimitiveDataFrameColumn<float>("ValorVenal", lista.Select(t => t.ValorVenal)),
            new PrimitiveDataFrameColumn<float>("IpcaAcumulado12m", lista.Select(t => t.IpcaAcumulado12m)),
            new PrimitiveDataFrameColumn<float>("SelicMeta", lista.Select(t => t.SelicMeta)),
            new PrimitiveDataFrameColumn<float>("IndiceFipeZapCidade", lista.Select(t => t.IndiceFipeZapCidade)),
            new PrimitiveDataFrameColumn<float>("DistanciaMetroKm", lista.Select(t => t.DistanciaMetroKm)),
            new PrimitiveDataFrameColumn<float>("DistanciaEscolaKm", lista.Select(t => t.DistanciaEscolaKm)),
            new PrimitiveDataFrameColumn<float>("DistanciaParqueKm", lista.Select(t => t.DistanciaParqueKm)),
            new PrimitiveDataFrameColumn<float>("PossuiCoordenadas", lista.Select(t => t.PossuiCoordenadas))
        );

        return df;
    }

    /// <summary>
    /// Remove registros sem área construída e valor de transação válidos.
    /// </summary>
    public DataFrame Limpar(DataFrame df, ICollection<RegistroIgnorado>? registrosIgnorados = null)
    {
        _logger.LogInformation("Iniciando limpeza. Linhas iniciais: {Rows}", df.Rows.Count);

        var linhas = df.Rows.ToList();
        foreach (var linha in linhas)
        {
            var areaConstruida = (float)linha["AreaConstruidaM2"]!;
            var valorTransacao = (float)linha["ValorTransacao"]!;
            var motivos = new List<string>();

            if (!float.IsFinite(areaConstruida) || areaConstruida <= 0f)
            {
                motivos.Add("Área construída ausente, não finita, zerada ou negativa.");
            }

            if (!float.IsFinite(valorTransacao) || valorTransacao <= 0f)
            {
                motivos.Add("Valor da transação ausente, não finito, zerado ou negativo.");
            }

            if (motivos.Count > 0)
            {
                registrosIgnorados?.Add(new RegistroIgnorado(
                    $"ITBI-SP / registro {linha["Id"]}",
                    $"Data={((DateTime)linha["DataTransacao"]!).ToString("O", CultureInfo.InvariantCulture)}; " +
                    $"Cidade={linha["Cidade"]}; Bairro={linha["Bairro"]}; " +
                    $"AreaConstruidaM2={areaConstruida.ToString(CultureInfo.InvariantCulture)}; " +
                    $"ValorTransacao={valorTransacao.ToString(CultureInfo.InvariantCulture)}",
                    string.Join(" ", motivos)));
            }
        }

        var mascara = new PrimitiveDataFrameColumn<bool>(
            "FiltroValido",
            linhas.Select(linha =>
                float.IsFinite((float)linha["AreaConstruidaM2"]!) &&
                (float)linha["AreaConstruidaM2"]! > 0f &&
                float.IsFinite((float)linha["ValorTransacao"]!) &&
                (float)linha["ValorTransacao"]! > 0f));
        var filtro = df.Filter(mascara);

        _logger.LogInformation(
            "Após limpeza: {Rows} linhas; {Ignored} registros ignorados.",
            filtro.Rows.Count,
            linhas.Count - filtro.Rows.Count);
        return filtro;
    }

    /// <summary>
    /// Feature engineering: cria variáveis derivadas úteis para regressão.
    /// </summary>
    public DataFrame CriarFeatures(DataFrame df)
    {
        // A idade é calculada na data da transação, nunca no ano corrente.
        var idade = new PrimitiveDataFrameColumn<int>("IdadeImovel",
            df.Rows.Select(r =>
            {
                var data = (DateTime)r["DataTransacao"]!;
                var anoReferencia = data == default ? DateTime.Today.Year : data.Year;
                return anoReferencia - (int)r["AnoConstrucao"]!;
            }));

        // Densidade construtiva (área construída / terreno)
        var densidade = new PrimitiveDataFrameColumn<float>("DensidadeConstrutiva",
            df.Rows.Select(r =>
            {
                var terreno = (float)r["AreaTerrenoM2"];
                var construida = (float)r["AreaConstruidaM2"];
                return terreno > 0 ? construida / terreno : 0f;
            }));

        df.Columns.Add(idade);
        df.Columns.Add(densidade);

        _logger.LogInformation("Features derivadas criadas: IdadeImovel, DensidadeConstrutiva");
        return df;
    }

    /// <summary>
    /// Enriquece as transações com indicadores macro do período.
    /// Técnica de join temporal (feature externa).
    /// </summary>
    public IReadOnlyList<ImovelTransacao> EnriquecerComMacro(
        IEnumerable<ImovelTransacao> transacoes,
        IReadOnlyList<IndicadorMacro> ipca,
        IReadOnlyList<IndicadorMacro> selic)
    {
        var ipcaDict = ipca
            .GroupBy(i => new DateTime(i.Data.Year, i.Data.Month, 1))
            .ToDictionary(g => g.Key, g => g.Last().Valor);

        var selicDict = selic
            .GroupBy(i => new DateTime(i.Data.Year, i.Data.Month, 1))
            .ToDictionary(g => g.Key, g => g.Last().Valor);

        foreach (var t in transacoes)
        {
            // O mês de referência do IPCA/Selic ainda pode não estar publicado no momento da transação.
            var chave = new DateTime(t.DataTransacao.Year, t.DataTransacao.Month, 1).AddMonths(-2);
            if (ipcaDict.TryGetValue(chave, out var ipcaVal)) t.IpcaAcumulado12m = ipcaVal;
            if (selicDict.TryGetValue(chave, out var selicVal)) t.SelicMeta = selicVal;
        }

        return transacoes.ToList();
    }

    public void SalvarCsv(DataFrame df, string caminho)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        DataFrame.SaveCsv(df, caminho, ',', true, System.Text.Encoding.UTF8, CultureInfo.InvariantCulture);
        _logger.LogInformation("DataFrame salvo em {Caminho}", caminho);
    }
}
