using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using PrevisaoPrecosImoveis.Core.Models;

namespace PrevisaoPrecosImoveis.Core.DataCollection;

/// <summary>
/// Coletor do Índice FipeZap (séries históricas de preços pedidos).
/// Fonte: https://www.fipe.org.br/pt-br/indices/fipezap/
/// Download público de planilhas Excel com índices residenciais e comerciais.
/// 
/// Uso: feature de tendência de mercado por cidade (não preço individual de transação).
/// </summary>
public class ColetorFipeZap : IColetorDados
{
    private readonly HttpClient _http;
    private readonly ILogger<ColetorFipeZap> _logger;
    private readonly string _pastaDownload;

    public string NomeFonte => "FipeZap";

    public ColetorFipeZap(HttpClient http, ILogger<ColetorFipeZap> logger, string pastaDownload = "data/raw/fipezap")
    {
        _http = http;
        _logger = logger;
        _pastaDownload = pastaDownload;
        Directory.CreateDirectory(_pastaDownload);
    }

    public async Task<IReadOnlyList<ImovelTransacao>> ColetarAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Coletando séries FipeZap...");

        // O download real é feito manualmente ou via link publicado no site da Fipe.
        // Após baixar o Excel, coloque-o em data/raw/fipezap/ e o parse será executado.

        var resultados = new List<ImovelTransacao>();
        foreach (var arquivo in Directory.GetFiles(_pastaDownload, "*.xlsx"))
        {
            if (!ArquivoFontePublica.EhXlsxUtilizavel(arquivo))
            {
                _logger.LogWarning(
                    "Planilha FipeZap incompleta ou inválida ignorada: {Arquivo}.",
                    arquivo);
                continue;
            }

            try
            {
                resultados.AddRange(ParsearSerie(arquivo));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Falha ao ler a planilha FipeZap {Arquivo}. O arquivo será ignorado.",
                    arquivo);
            }
        }

        _logger.LogInformation("FipeZap: {Count} pontos de índice carregados.", resultados.Count);
        return resultados;
    }

    private IEnumerable<ImovelTransacao> ParsearSerie(string caminho)
    {
        using var wb = new XLWorkbook(caminho);
        var ws = wb.Worksheets.First();

        // Estrutura típica: datas nas colunas, cidades nas linhas (ou vice-versa).
        // Ajuste conforme a planilha oficial baixada.
        foreach (var linha in ws.RowsUsed().Skip(1))
        {
            var cidade = linha.Cell(1).GetString();
            if (string.IsNullOrWhiteSpace(cidade) ||
                !linha.Cell(2).TryGetValue<double>(out var indice) ||
                !double.IsFinite(indice))
            {
                continue;
            }

            yield return new ImovelTransacao
            {
                Id = Guid.NewGuid().ToString("N"),
                Fonte = NomeFonte,
                Cidade = cidade,
                DataTransacao = DateTime.Today, // mapear da planilha
                IndiceFipeZapCidade = (float)indice,
                TipoImovel = "Residencial"
            };
        }
    }
}
