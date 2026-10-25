using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using PrevisaoPrecosImoveis.Core.Models;

namespace PrevisaoPrecosImoveis.Core.DataCollection;

/// <summary>
/// Coleta dados públicos de ITBI da Prefeitura de São Paulo.
/// Fonte oficial: https://prefeitura.sp.gov.br/web/fazenda/w/acesso_a_informacao/31501
/// Arquivos Excel/ODS mensais e anuais com transações reais (valor declarado).
/// 
/// Técnica: download HTTP + parsing tabular (pré-processamento de dados oficiais).
/// </summary>
public class ColetorItbiSaoPaulo : IColetorDados
{
    private const string PaginaItbi =
        "https://prefeitura.sp.gov.br/web/fazenda/w/acesso_a_informacao/31501";
    private const string DiretorioArquivosItbi =
        "/cidade/secretarias/upload/fazenda/arquivos/itbi/";

    private readonly HttpClient _http;
    private readonly ILogger<ColetorItbiSaoPaulo> _logger;
    private readonly string _pastaDownload;
    private readonly List<RegistroIgnorado> _registrosIgnorados = [];

    public string NomeFonte => "ITBI_SP";
    public IReadOnlyList<RegistroIgnorado> RegistrosIgnorados => _registrosIgnorados;

    public ColetorItbiSaoPaulo(HttpClient http, ILogger<ColetorItbiSaoPaulo> logger, string pastaDownload = "data/raw/itbi_sp")
    {
        _http = http;
        _logger = logger;
        _pastaDownload = pastaDownload;
        Directory.CreateDirectory(_pastaDownload);
    }

    public async Task<IReadOnlyList<ImovelTransacao>> ColetarAsync(CancellationToken ct = default)
    {
        _registrosIgnorados.Clear();
        _logger.LogInformation("Iniciando coleta ITBI São Paulo...");

        try
        {
            await BaixarArquivosPublicadosAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Não foi possível consultar a página oficial do ITBI. " +
                "Serão usados os arquivos XLSX que já estiverem em {Pasta}.",
                _pastaDownload);
        }

        var resultados = new List<ImovelTransacao>();

        var arquivosLocais = Directory.GetFiles(_pastaDownload, "*.xlsx");
        foreach (var arquivo in arquivosLocais)
        {
            if (!ArquivoFontePublica.EhXlsxUtilizavel(arquivo))
            {
                _registrosIgnorados.Add(new RegistroIgnorado(
                    Path.GetFileName(arquivo),
                    "Arquivo XLSX local",
                    "Arquivo incompleto, corrompido ou não reconhecido como planilha XLSX."));
                _logger.LogWarning(
                    "Arquivo ITBI incompleto ou inválido ignorado: {Arquivo}. " +
                    "Ele será removido para que a próxima coleta tente baixá-lo de novo.",
                    arquivo);
                ArquivoFontePublica.TentarExcluir(arquivo);
                continue;
            }

            try
            {
                resultados.AddRange(ParsearExcel(arquivo));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _registrosIgnorados.Add(new RegistroIgnorado(
                    Path.GetFileName(arquivo),
                    "Arquivo XLSX local",
                    $"Falha ao ler a planilha: {ex.Message}"));
                _logger.LogWarning(
                    ex,
                    "Falha ao ler o arquivo ITBI {Arquivo}. O arquivo será ignorado.",
                    arquivo);
            }
        }

        _logger.LogInformation("Coleta ITBI SP finalizada. {Count} registros.", resultados.Count);
        return resultados;
    }

    private async Task BaixarArquivosPublicadosAsync(CancellationToken ct)
    {
        using var resposta = await _http.GetAsync(PaginaItbi, ct);
        resposta.EnsureSuccessStatusCode();
        var html = await resposta.Content.ReadAsStringAsync(ct);
        var arquivos = DescobrirArquivosExcel(html);

        _logger.LogInformation(
            "Encontrados {Count} arquivos XLSX na página oficial do ITBI.",
            arquivos.Count);

        foreach (var url in arquivos)
        {
            ct.ThrowIfCancellationRequested();

            var nomeArquivo = Uri.UnescapeDataString(url.Segments[^1]);
            if (string.IsNullOrWhiteSpace(nomeArquivo) ||
                nomeArquivo != Path.GetFileName(nomeArquivo))
            {
                _logger.LogWarning("Nome de arquivo ITBI inválido na URL {Url}.", url);
                continue;
            }

            var destino = Path.Combine(_pastaDownload, nomeArquivo);
            if (File.Exists(destino))
            {
                if (ArquivoFontePublica.EhXlsxUtilizavel(destino))
                {
                    _logger.LogDebug("Arquivo ITBI já existe: {Arquivo}", destino);
                    continue;
                }

                _logger.LogWarning(
                    "Arquivo ITBI local incompleto ou corrompido será baixado novamente: {Arquivo}",
                    destino);
                ArquivoFontePublica.TentarExcluir(destino);
            }

            try
            {
                await BaixarXlsxPublicadoAsync(url, destino, ct);
                _logger.LogInformation("Arquivo ITBI salvo em {Destino}", destino);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Falha ao baixar arquivo ITBI {Url}.", url);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Falha de I/O ao gravar o arquivo ITBI {Url}.", url);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "O conteúdo do link ITBI {Url} não é um XLSX válido ou está incompleto.", url);
            }
        }
    }

    private static IReadOnlyList<Uri> DescobrirArquivosExcel(string html)
    {
        const string anchorPattern =
            """<a\b[^>]*\bhref\s*=\s*(?<quote>["'])(?<url>.*?)\k<quote>[^>]*>.*?</a>""";

        return Regex.Matches(html, anchorPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value))
            .Select(link => Uri.TryCreate(new Uri(PaginaItbi), link, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsArquivoItbiPermitido(uri))
            .Select(uri => uri!)
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task BaixarXlsxPublicadoAsync(Uri url, string destino, CancellationToken ct)
    {
        const int maxTentativas = 3;
        Exception? ultimoErro = null;

        for (var tentativa = 1; tentativa <= maxTentativas; tentativa++)
        {
            var arquivoTemporario = $"{destino}.{Guid.NewGuid():N}.tmp";
            try
            {
                using var resposta = await _http.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
                resposta.EnsureSuccessStatusCode();

                var urlFinal = resposta.RequestMessage?.RequestUri;
                if (urlFinal is null || !IsArquivoItbiPermitido(urlFinal))
                {
                    throw new InvalidDataException(
                        $"O download ITBI redirecionou para um endereço não permitido: {urlFinal}");
                }

                await using (var stream = new FileStream(
                    arquivoTemporario,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    await resposta.Content.CopyToAsync(stream, ct);
                    await stream.FlushAsync(ct);

                    var tamanhoEsperado = resposta.Content.Headers.ContentLength;
                    if (tamanhoEsperado is > 0 &&
                        resposta.Content.Headers.ContentEncoding.Count == 0 &&
                        stream.Length < tamanhoEsperado)
                    {
                        throw new InvalidDataException(
                            $"Download incompleto: recebidos {stream.Length} de {tamanhoEsperado} bytes.");
                    }
                }

                if (!ArquivoFontePublica.EhXlsxUtilizavel(arquivoTemporario))
                {
                    throw new InvalidDataException(
                        "A resposta não é um XLSX completo (arquivo truncado, HTML de erro ou ZIP inválido).");
                }

                File.Move(arquivoTemporario, destino, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
            {
                ultimoErro = ex;
                ArquivoFontePublica.TentarExcluir(arquivoTemporario);
                if (tentativa == maxTentativas)
                {
                    break;
                }

                _logger.LogWarning(
                    ex,
                    "Tentativa {Tentativa}/{Max} de baixar {Url} falhou. Nova tentativa em {Delay}s.",
                    tentativa,
                    maxTentativas,
                    url,
                    tentativa);
                await Task.Delay(TimeSpan.FromSeconds(tentativa), ct);
            }
            finally
            {
                ArquivoFontePublica.TentarExcluir(arquivoTemporario);
            }
        }

        throw ultimoErro ?? new InvalidDataException($"Não foi possível baixar um XLSX válido de {url}.");
    }

    private static bool IsArquivoItbiPermitido(Uri uri)
    {
        var hostPermitido =
            uri.Host.Equals("prefeitura.sp.gov.br", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("www.prefeitura.sp.gov.br", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("drive.prefeitura.sp.gov.br", StringComparison.OrdinalIgnoreCase);

        return uri.Scheme == Uri.UriSchemeHttps &&
               hostPermitido &&
               uri.AbsolutePath.StartsWith(DiretorioArquivosItbi, StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parseia todas as planilhas mensais do arquivo oficial de ITBI-SP.
    /// </summary>
    private IEnumerable<ImovelTransacao> ParsearExcel(string caminho)
    {
        using var workbook = new XLWorkbook(caminho);
        var transacoes = new List<ImovelTransacao>();
        var linhasInvalidas = 0;

        foreach (var planilha in workbook.Worksheets)
        {
            try
            {
                foreach (var linha in planilha.RowsUsed())
                {
                    if (EhCabecalho(linha))
                    {
                        continue;
                    }

                    var dataValida = TryLerData(linha.Cell(10), out var dataTransacao);
                    var valorNumerico = TryLerNumero(linha.Cell(9), out var valorTransacao);
                    var motivo = !dataValida
                        ? "Data da transação ausente ou inválida."
                        : !valorNumerico
                            ? "Valor da transação ausente ou não numérico."
                            : valorTransacao <= 0
                                ? "Valor da transação deve ser maior que zero."
                                : null;

                    if (motivo is not null)
                    {
                        linhasInvalidas++;
                        _registrosIgnorados.Add(new RegistroIgnorado(
                            $"{Path.GetFileName(caminho)} / {planilha.Name} / linha {linha.RowNumber()}",
                            $"Data={linha.Cell(10).GetString()}; " +
                            $"ValorTransacao={linha.Cell(9).GetString()}; " +
                            $"AreaConstruidaM2={linha.Cell(23).GetString()}; " +
                            $"Bairro={linha.Cell(5).GetString()}",
                            motivo));
                        continue;
                    }

                    TryLerNumero(linha.Cell(11), out var valorVenal);
                    TryLerNumero(linha.Cell(20), out var areaTerreno);
                    TryLerNumero(linha.Cell(23), out var areaConstruida);
                    TryLerNumero(linha.Cell(28), out var anoConstrucaoRaw);

                    var uso = linha.Cell(25).GetString();
                    var padrao = linha.Cell(27).GetString();
                    var numero = linha.Cell(3).GetString();
                    var logradouro = linha.Cell(2).GetString();

                    transacoes.Add(new ImovelTransacao
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Fonte = NomeFonte,
                        Cidade = "São Paulo",
                        DataTransacao = dataTransacao,
                        Logradouro = string.IsNullOrWhiteSpace(numero)
                            ? logradouro
                            : $"{logradouro}, {numero}",
                        Bairro = linha.Cell(5).GetString(),
                        Cep = linha.Cell(7).GetString(),
                        AreaTerrenoM2 = areaTerreno,
                        AreaConstruidaM2 = areaConstruida,
                        AnoConstrucao = anoConstrucaoRaw is >= 1600 and <= 2200
                            ? (int)anoConstrucaoRaw
                            : 0,
                        TipoImovel = InferirTipoImovel($"{uso} {padrao}"),
                        PadraoConstrutivo = padrao,
                        ValorTransacao = valorTransacao,
                        ValorVenal = valorVenal
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _registrosIgnorados.Add(new RegistroIgnorado(
                    $"{Path.GetFileName(caminho)} / planilha {planilha.Name}",
                    "Planilha ITBI",
                    $"Planilha ilegível e ignorada: {ex.Message}"));
                _logger.LogWarning(
                    ex,
                    "Planilha '{Planilha}' do arquivo ITBI {Arquivo} está ilegível e será ignorada.",
                    planilha.Name,
                    caminho);
            }
        }

        if (linhasInvalidas > 0)
        {
            _logger.LogWarning(
                "Ignoradas {Count} linhas sem data válida ou valor de transação positivo no arquivo {Arquivo}.",
                linhasInvalidas, caminho);
        }

        return transacoes;
    }

    private static bool EhCabecalho(IXLRow linha)
    {
        return linha.CellsUsed()
            .Any(celula =>
                celula.GetString().Contains("Data de Transação", StringComparison.OrdinalIgnoreCase) ||
                celula.GetString().Contains("Data de Transacao", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryLerData(IXLCell celula, out DateTime data)
    {
        if (celula.TryGetValue<DateTime>(out data))
        {
            return true;
        }

        if (TryLerNumero(celula, out var serialExcel))
        {
            try
            {
                data = DateTime.FromOADate(serialExcel);
                return true;
            }
            catch (ArgumentException)
            {
                data = default;
                return false;
            }
        }

        var texto = celula.GetString();
        return DateTime.TryParse(
            texto,
            CultureInfo.GetCultureInfo("pt-BR"),
            DateTimeStyles.None,
            out data);
    }

    private static bool TryLerNumero(IXLCell celula, out float valor)
    {
        if (celula.TryGetValue<double>(out var numero) &&
            double.IsFinite(numero) &&
            numero is >= float.MinValue and <= float.MaxValue)
        {
            valor = (float)numero;
            return true;
        }

        var texto = celula.GetString();
        if (float.TryParse(texto, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out valor) ||
            float.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out valor))
        {
            return float.IsFinite(valor);
        }

        valor = 0;
        return false;
    }

    private static string InferirTipoImovel(string uso)
    {
        if (uso.Contains("VERTICAL", StringComparison.OrdinalIgnoreCase) ||
            uso.Contains("APARTAMENTO", StringComparison.OrdinalIgnoreCase))
        {
            return "Apartamento";
        }

        if (uso.Contains("HORIZONTAL", StringComparison.OrdinalIgnoreCase) ||
            uso.Contains("RESIDÊNCIA", StringComparison.OrdinalIgnoreCase) ||
            uso.Contains("RESIDENCIA", StringComparison.OrdinalIgnoreCase))
        {
            return "Casa";
        }

        return "Comercial";
    }

    /// <summary>
    /// Baixa um arquivo específico da Prefeitura (implementar com URL real).
    /// </summary>
    public async Task BaixarArquivoAsync(string url, string nomeArquivo, CancellationToken ct = default)
    {
        var destino = Path.Combine(_pastaDownload, nomeArquivo);
        if (File.Exists(destino) && ArquivoFontePublica.EhXlsxUtilizavel(destino))
        {
            _logger.LogInformation("Arquivo já existe: {Arquivo}", destino);
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsArquivoItbiPermitido(uri))
        {
            throw new InvalidDataException($"URL de download ITBI não permitida: {url}");
        }

        if (File.Exists(destino))
        {
            _logger.LogWarning("Arquivo local inválido será substituído: {Arquivo}", destino);
            ArquivoFontePublica.TentarExcluir(destino);
        }

        _logger.LogInformation("Baixando {Url} ...", url);
        await BaixarXlsxPublicadoAsync(uri, destino, ct);
        _logger.LogInformation("Salvo em {Destino}", destino);
    }
}
