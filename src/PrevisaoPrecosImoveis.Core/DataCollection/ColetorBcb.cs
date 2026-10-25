using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrevisaoPrecosImoveis.Core.Models;

namespace PrevisaoPrecosImoveis.Core.DataCollection;

/// <summary>
/// Coletor de séries temporais do Banco Central do Brasil (SGS) e Mercado Imobiliário.
/// 
/// Fontes oficiais:
/// - SGS: https://api.bcb.gov.br/dados/serie/bcdata.sgs.{codigo}/dados
/// - Mercado Imobiliário OData: https://olinda.bcb.gov.br/olinda/servico/MercadoImobiliario/versao/v1/odata/
/// 
/// Técnicas: consumo de API REST pública, parsing JSON/CSV, feature engineering temporal.
/// </summary>
public class ColetorBcb : IColetorIndicadores
{
    private readonly HttpClient _http;
    private readonly ILogger<ColetorBcb> _logger;

    private const string SgsBaseUrl = "https://api.bcb.gov.br/dados/serie/bcdata.sgs.{0}/dados";
    private const int MaxTentativas = 3;
    private const string MercadoImobiliarioUrl =
        "https://olinda.bcb.gov.br/olinda/servico/MercadoImobiliario/versao/v1/odata/mercadoimobiliario?$format=text/csv&$select=Data,Info,Valor";

    public ColetorBcb(HttpClient http, ILogger<ColetorBcb> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IndicadorMacro>> ColetarSerieAsync(
        int codigoSerie,
        DateTime inicio,
        DateTime fim,
        CancellationToken ct = default)
    {
        if (inicio > fim)
        {
            throw new ArgumentException("A data inicial deve ser anterior ou igual à data final.");
        }

        _logger.LogInformation("Coletando série BCB {Codigo} de {Inicio} a {Fim}", codigoSerie, inicio, fim);

        var pontos = new List<SgsPonto>();
        var inicioPeriodo = inicio.Date;
        while (inicioPeriodo <= fim.Date)
        {
            var fimAno = new DateTime(inicioPeriodo.Year, 12, 31);
            var fimPeriodo = fimAno < fim.Date ? fimAno : fim.Date;

            var pontosPeriodo = await ColetarPeriodoAsync(
                codigoSerie,
                inicioPeriodo,
                fimPeriodo,
                ct);
            pontos.AddRange(pontosPeriodo);
            if (fimPeriodo == fim.Date)
            {
                break;
            }

            inicioPeriodo = fimPeriodo.AddDays(1);
        }

        var indicadores = new List<IndicadorMacro>();
        var pontosInvalidos = 0;
        foreach (var ponto in pontos)
        {
            if (!TryMapearPonto(ponto, codigoSerie, out var indicador))
            {
                pontosInvalidos++;
                continue;
            }

            indicadores.Add(indicador);
        }

        if (pontosInvalidos > 0)
        {
            _logger.LogWarning(
                "Ignorados {Invalidos} pontos incompletos ou inválidos na série BCB {Codigo}.",
                pontosInvalidos,
                codigoSerie);
        }

        return indicadores;
    }

    private async Task<List<SgsPonto>> ColetarPeriodoAsync(
        int codigoSerie,
        DateTime inicio,
        DateTime fim,
        CancellationToken ct)
    {
        var dataInicial = Uri.EscapeDataString(inicio.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        var dataFinal = Uri.EscapeDataString(fim.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        var url = string.Format(SgsBaseUrl, codigoSerie) +
                  $"?formato=json&dataInicial={dataInicial}&dataFinal={dataFinal}";

        _logger.LogDebug(
            "Consultando série BCB {Codigo} no período {Inicio} a {Fim}.",
            codigoSerie, inicio, fim);

        for (var tentativa = 1; tentativa <= MaxTentativas; tentativa++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.ParseAdd("PrevisaoPrecosImoveis/1.0");

                using var response = await _http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var resposta = await response.Content.ReadAsStringAsync(ct);
                    try
                    {
                        if (!ArquivoFontePublica.EhJsonUtilizavel(resposta))
                        {
                            throw new JsonException(
                                "A resposta não é JSON completo (vazia, truncada ou HTML de erro).");
                        }

                        var pontos = JsonSerializer.Deserialize<List<SgsPonto>>(resposta);
                        if (pontos is null)
                        {
                            throw new JsonException("A resposta JSON da série não contém uma lista.");
                        }

                        return pontos;
                    }
                    catch (JsonException ex)
                    {
                        var tipoConteudo = response.Content.Headers.ContentType?.MediaType ?? "desconhecido";
                        var inicioResposta = ObterInicioResposta(resposta);
                        if (tentativa == MaxTentativas)
                        {
                            _logger.LogWarning(
                                "O BCB retornou conteúdo inválido para a série {Codigo} no período " +
                                "{Inicio} a {Fim} após {Tentativas} tentativas. " +
                                "Tipo de conteúdo: {ContentType}. Início da resposta: {ResponsePreview}. " +
                                "Este período será ignorado. Erro de parsing: {ParseError}",
                                codigoSerie, inicio, fim, tentativa, tipoConteudo, inicioResposta, ex.Message);
                            return new List<SgsPonto>();
                        }

                        _logger.LogWarning(
                            "O BCB retornou conteúdo que não é JSON válido para a série {Codigo} no período " +
                            "{Inicio} a {Fim} " +
                            "(tipo {ContentType}, início: {ResponsePreview}). Nova tentativa em " +
                            "{DelaySeconds}s ({Tentativa}/{MaxTentativas}). Erro: {ParseError}",
                            codigoSerie, inicio, fim, tipoConteudo, inicioResposta, tentativa, tentativa + 1,
                            MaxTentativas, ex.Message);
                    }
                }
                else
                {
                    if (!IsTransientStatusCode(response.StatusCode))
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    if (tentativa == MaxTentativas)
                    {
                        _logger.LogError(
                            "Falha ao coletar série BCB {Codigo} no período {Inicio} a {Fim}: " +
                            "HTTP {StatusCode} após {Tentativas} tentativas. Este período será ignorado.",
                            codigoSerie, inicio, fim, (int)response.StatusCode, tentativa);
                        return new List<SgsPonto>();
                    }

                    _logger.LogWarning(
                        "Erro temporário HTTP {StatusCode} ao coletar série BCB {Codigo} no período " +
                        "{Inicio} a {Fim}. " +
                        "Nova tentativa em {DelaySeconds}s ({Tentativa}/{MaxTentativas}).",
                        (int)response.StatusCode, codigoSerie, inicio, fim, tentativa, tentativa + 1,
                        MaxTentativas);
                }
            }
            catch (HttpRequestException ex) when (IsTransientStatusCode(ex.StatusCode))
            {
                if (tentativa == MaxTentativas)
                {
                    _logger.LogError(
                        ex,
                        "Falha ao conectar ao BCB para coletar a série {Codigo} no período {Inicio} a {Fim} " +
                        "após {Tentativas} tentativas. Este período será ignorado.",
                        codigoSerie, inicio, fim, tentativa);
                    return new List<SgsPonto>();
                }

                _logger.LogWarning(
                    ex,
                    "Erro temporário ao conectar ao BCB para a série {Codigo} no período {Inicio} a {Fim}. " +
                    "Nova tentativa em {DelaySeconds}s ({Tentativa}/{MaxTentativas}).",
                    codigoSerie, inicio, fim, tentativa, tentativa + 1, MaxTentativas);
            }

            await Task.Delay(TimeSpan.FromSeconds(tentativa), ct);
        }

        return new List<SgsPonto>();
    }

    private static bool IsTransientStatusCode(HttpStatusCode? statusCode)
    {
        if (statusCode is null)
        {
            return true;
        }

        var code = (int)statusCode.Value;
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
               code >= 500;
    }

    private static string ObterInicioResposta(string resposta)
    {
        const int tamanhoMaximo = 160;
        var inicio = resposta.AsSpan().TrimStart();
        return inicio.Length <= tamanhoMaximo
            ? inicio.ToString()
            : $"{inicio[..tamanhoMaximo].ToString()}...";
    }

    /// <summary>
    /// Baixa o CSV do Mercado Imobiliário (crédito, estoque, etc.).
    /// </summary>
    public async Task<string> BaixarMercadoImobiliarioCsvAsync(string destino, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        _logger.LogInformation("Baixando Mercado Imobiliário BCB...");

        const int maxTentativas = MaxTentativas;
        Exception? ultimoErro = null;
        for (var tentativa = 1; tentativa <= maxTentativas; tentativa++)
        {
            var temporario = $"{destino}.{Guid.NewGuid():N}.tmp";
            try
            {
                using var resposta = await _http.GetAsync(
                    MercadoImobiliarioUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
                resposta.EnsureSuccessStatusCode();

                await using (var stream = new FileStream(
                    temporario,
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
                            $"CSV incompleto: recebidos {stream.Length} de {tamanhoEsperado} bytes.");
                    }
                }

                if (!ArquivoFontePublica.EhCsvUtilizavel(temporario))
                {
                    throw new InvalidDataException(
                        "O BCB devolveu um CSV vazio, truncado ou uma página de erro.");
                }

                File.Move(temporario, destino, overwrite: true);
                return destino;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
            {
                ultimoErro = ex;
                ArquivoFontePublica.TentarExcluir(temporario);
                if (tentativa == maxTentativas)
                {
                    break;
                }

                _logger.LogWarning(
                    ex,
                    "Tentativa {Tentativa}/{Max} de baixar o CSV do Mercado Imobiliário falhou.",
                    tentativa,
                    maxTentativas);
                await Task.Delay(TimeSpan.FromSeconds(tentativa), ct);
            }
            finally
            {
                ArquivoFontePublica.TentarExcluir(temporario);
            }
        }

        throw ultimoErro ?? new InvalidDataException(
            "Não foi possível baixar o CSV do Mercado Imobiliário do BCB.");
    }

    private static bool TryMapearPonto(SgsPonto ponto, int codigoSerie, out IndicadorMacro indicador)
    {
        indicador = null!;
        if (string.IsNullOrWhiteSpace(ponto.data) || string.IsNullOrWhiteSpace(ponto.valor))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                ponto.data,
                "dd/MM/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var data) &&
            !DateTime.TryParse(
                ponto.data,
                CultureInfo.GetCultureInfo("pt-BR"),
                DateTimeStyles.None,
                out data))
        {
            return false;
        }

        if (!float.TryParse(ponto.valor, NumberStyles.Float, CultureInfo.InvariantCulture, out var valor) &&
            !float.TryParse(ponto.valor, NumberStyles.Float, CultureInfo.GetCultureInfo("pt-BR"), out valor))
        {
            return false;
        }

        if (!float.IsFinite(valor))
        {
            return false;
        }

        indicador = new IndicadorMacro
        {
            Data = data,
            CodigoSerie = codigoSerie.ToString(),
            Nome = $"Série {codigoSerie}",
            Valor = valor
        };
        return true;
    }

    private sealed class SgsPonto
    {
        public string data { get; set; } = "";
        public string valor { get; set; } = "";
    }
}
