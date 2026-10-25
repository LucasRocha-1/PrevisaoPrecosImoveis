using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrevisaoPrecosImoveis.Core.DataCollection;
using PrevisaoPrecosImoveis.Core.Models;

namespace PrevisaoPrecosImoveis.Core.Preprocessing;

public sealed class EnriquecedorEspacial
{
    private const string UrlOverpass = "https://overpass-api.de/api/interpreter";
    private const double RaioTerraKm = 6371.0088;
    private const double MargemBuscaGraus = 0.03;

    private readonly HttpClient _http;
    private readonly ILogger<EnriquecedorEspacial> _logger;

    public EnriquecedorEspacial(HttpClient http, ILogger<EnriquecedorEspacial> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PrevisaoPrecosImoveis/1.0 (public-data research)");
    }

    public async Task<IReadOnlyList<ImovelTransacao>> EnriquecerAsync(
        IEnumerable<ImovelTransacao> transacoes,
        string caminhoCoordenadas = "data/raw/geolocalizacao/cep_coordenadas.csv",
        string caminhoCache = "data/raw/openstreetmap/pontos_interesse.json",
        CancellationToken cancellationToken = default)
    {
        var lista = transacoes.ToList();
        var possuiArquivoCoordenadas = File.Exists(caminhoCoordenadas);
        var coordenadasPorCep = possuiArquivoCoordenadas
            ? CarregarCoordenadas(caminhoCoordenadas)
            : new Dictionary<string, Coordenada>(StringComparer.Ordinal);
        if (!possuiArquivoCoordenadas)
        {
            _logger.LogWarning(
                "Arquivo de coordenadas não encontrado em {Caminho}. " +
                "Nenhum CEP será associado a coordenadas.",
                caminhoCoordenadas);
        }

        foreach (var transacao in lista)
        {
            if (CoordenadaValida(transacao.Latitude, transacao.Longitude))
            {
                transacao.PossuiCoordenadas = 1f;
                continue;
            }

            var cep = NormalizarCep(transacao.Cep);
            if (coordenadasPorCep.TryGetValue(cep, out var coordenada))
            {
                transacao.Latitude = coordenada.Latitude;
                transacao.Longitude = coordenada.Longitude;
                transacao.PossuiCoordenadas = 1f;
            }
        }

        var geolocalizadas = lista
            .Where(t => CoordenadaValida(t.Latitude, t.Longitude))
            .ToList();
        if (geolocalizadas.Count == 0)
        {
            _logger.LogWarning(
                "Nenhum imóvel foi associado a coordenadas válidas usando {Caminho}.",
                caminhoCoordenadas);
            return lista;
        }

        var limites = CriarLimites(geolocalizadas);
        IReadOnlyList<PontoInteresse> pontos;
        try
        {
            pontos = await CarregarPontosInteresseAsync(
                limites,
                caminhoCache,
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or IOException)
        {
            _logger.LogWarning(
                ex,
                "Não foi possível obter POIs do OpenStreetMap (resposta incompleta ou indisponível). " +
                "As distâncias espaciais ficarão com o marcador de ausência.");
            return lista;
        }
        var distanciasPorCoordenada =
            new Dictionary<(double Latitude, double Longitude, DateTime DataTransacao), Distancias>();

        foreach (var transacao in geolocalizadas)
        {
            var chave = (
                (double)transacao.Latitude!.Value,
                (double)transacao.Longitude!.Value,
                transacao.DataTransacao);
            if (!distanciasPorCoordenada.TryGetValue(chave, out var distancias))
            {
                distancias = CalcularDistancias(
                    chave.Item1,
                    chave.Item2,
                    chave.Item3,
                    pontos);
                distanciasPorCoordenada.Add(chave, distancias);
            }

            transacao.DistanciaMetroKm = distancias.MetroKm;
            transacao.DistanciaEscolaKm = distancias.EscolaKm;
            transacao.DistanciaParqueKm = distancias.ParqueKm;
        }

        _logger.LogInformation(
            "Features espaciais calculadas para {Geolocalizados} de {Total} transações, usando {Pontos} POIs do OpenStreetMap.",
            geolocalizadas.Count,
            lista.Count,
            pontos.Count);
        return lista;
    }

    private async Task<IReadOnlyList<PontoInteresse>> CarregarPontosInteresseAsync(
        Limites limites,
        string caminhoCache,
        CancellationToken cancellationToken)
    {
        if (File.Exists(caminhoCache))
        {
            try
            {
                var textoCache = await File.ReadAllTextAsync(caminhoCache, cancellationToken);
                if (!ArquivoFontePublica.EhJsonUtilizavel(textoCache))
                {
                    throw new JsonException("Cache OSM vazio, truncado ou em formato inesperado.");
                }

                var cache = JsonSerializer.Deserialize<CacheOpenStreetMap>(textoCache);
                if (cache is not null && cache.Limites.Contem(limites))
                {
                    _logger.LogInformation("Usando cache OSM em {Caminho}.", caminhoCache);
                    return cache.Pontos;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                _logger.LogWarning(
                    ex,
                    "Cache OSM em {Caminho} está incompleto ou corrompido e será ignorado.",
                    caminhoCache);
                ArquivoFontePublica.TentarExcluir(caminhoCache);
            }
        }

        var query = CriarConsultaOverpass(limites);
        using var conteudo = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["data"] = query
        });
        using var resposta = await _http.PostAsync(UrlOverpass, conteudo, cancellationToken);
        resposta.EnsureSuccessStatusCode();
        var json = await resposta.Content.ReadAsStringAsync(cancellationToken);
        if (!ArquivoFontePublica.EhJsonUtilizavel(json))
        {
            throw new InvalidDataException(
                "A API Overpass devolveu uma resposta incompleta ou que não é JSON.");
        }

        using var documento = JsonDocument.Parse(json);
        if (documento.RootElement.ValueKind != JsonValueKind.Object ||
            !documento.RootElement.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "A resposta da Overpass está incompleta e não contém a lista de elementos.");
        }

        var pontos = elements
            .EnumerateArray()
            .ToArray()
            .Select(elemento => ConverterElemento(elemento))
            .Where(ponto => ponto is not null)
            .Select(ponto => ponto!)
            .DistinctBy(ponto => (ponto.Tipo, ponto.Latitude, ponto.Longitude))
            .ToList();

        var pastaCache = Path.GetDirectoryName(caminhoCache);
        if (!string.IsNullOrEmpty(pastaCache))
        {
            Directory.CreateDirectory(pastaCache);
        }
        var cacheAtualizado = new CacheOpenStreetMap(limites, pontos);
        await ArquivoFontePublica.SubstituirAtomicamenteAsync(
            caminhoCache,
            JsonSerializer.Serialize(cacheAtualizado),
            cancellationToken);
        return pontos;
    }

    private Dictionary<string, Coordenada> CarregarCoordenadas(string caminho)
    {
        string[] linhas;
        try
        {
            linhas = File.ReadAllLines(caminho);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                ex,
                "Não foi possível ler o arquivo de coordenadas {Caminho}. Nenhum CEP será associado.",
                caminho);
            return new Dictionary<string, Coordenada>(StringComparer.Ordinal);
        }

        if (linhas.Length == 0 ||
            !linhas[0].Contains("Cep", StringComparison.OrdinalIgnoreCase) ||
            !linhas[0].Contains("Latitude", StringComparison.OrdinalIgnoreCase) ||
            !linhas[0].Contains("Longitude", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "O arquivo {Caminho} está incompleto ou sem as colunas Cep,Latitude,Longitude. " +
                "Nenhum CEP será associado.",
                caminho);
            return new Dictionary<string, Coordenada>(StringComparer.Ordinal);
        }

        var coordenadas = new Dictionary<string, Coordenada>(StringComparer.Ordinal);
        var linhasInvalidas = 0;
        for (var i = 1; i < linhas.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(linhas[i]))
            {
                continue;
            }

            var colunas = linhas[i].Split(',');
            if (colunas.Length < 3 ||
                !double.TryParse(colunas[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(colunas[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
                !CoordenadaValida(latitude, longitude))
            {
                linhasInvalidas++;
                continue;
            }

            var cep = NormalizarCep(colunas[0]);
            if (cep.Length != 8)
            {
                linhasInvalidas++;
                continue;
            }

            coordenadas[cep] = new Coordenada(latitude, longitude);
        }

        if (linhasInvalidas > 0)
        {
            _logger.LogWarning(
                "Ignoradas {Count} linhas incompletas ou inválidas no arquivo de coordenadas {Caminho}.",
                linhasInvalidas,
                caminho);
        }

        return coordenadas;
    }

    private static PontoInteresse? ConverterElemento(JsonElement elemento)
    {
        if (!elemento.TryGetProperty("tags", out var tags) ||
            tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var tipo = tags.TryGetProperty("railway", out var railway) &&
                   railway.GetString() == "station" &&
                   tags.TryGetProperty("station", out var station) &&
                   station.GetString() == "subway"
            ? TipoPonto.Metro
            : tags.TryGetProperty("amenity", out var amenity) && amenity.GetString() == "school"
                ? TipoPonto.Escola
                : tags.TryGetProperty("leisure", out var leisure) && leisure.GetString() == "park"
                    ? TipoPonto.Parque
                    : (TipoPonto?)null;

        var dataInicio = ObterDataInicio(tags);
        if (tipo is null || dataInicio is null)
        {
            return null;
        }

        var localizacao = elemento.TryGetProperty("center", out var center) ? center : elemento;
        if (!localizacao.TryGetProperty("lat", out var latitude) ||
            !localizacao.TryGetProperty("lon", out var longitude))
        {
            return null;
        }

        return new PontoInteresse(
            tipo.Value,
            latitude.GetDouble(),
            longitude.GetDouble(),
            dataInicio.Value);
    }

    private static Distancias CalcularDistancias(
        double latitude,
        double longitude,
        DateTime dataTransacao,
        IReadOnlyList<PontoInteresse> pontos)
    {
        var distancias = new Dictionary<TipoPonto, double>();
        foreach (var ponto in pontos)
        {
            if (ponto.DataInicio > dataTransacao)
            {
                continue;
            }

            var distancia = DistanciaHaversineKm(latitude, longitude, ponto.Latitude, ponto.Longitude);
            if (!distancias.TryGetValue(ponto.Tipo, out var atual) || distancia < atual)
            {
                distancias[ponto.Tipo] = distancia;
            }
        }

        return new Distancias(
            ParaFeature(distancias, TipoPonto.Metro),
            ParaFeature(distancias, TipoPonto.Escola),
            ParaFeature(distancias, TipoPonto.Parque));
    }

    private static DateTime? ObterDataInicio(JsonElement tags)
    {
        var valor = tags.TryGetProperty("start_date", out var startDate)
            ? startDate.GetString()
            : tags.TryGetProperty("opening_date", out var openingDate)
                ? openingDate.GetString()
                : null;
        var formatos = new[]
        {
            "yyyy",
            "yyyy-MM",
            "yyyy-MM-dd",
            "yyyy-MM-dd'T'HH:mm:ss",
            "dd/MM/yyyy"
        };
        return DateTime.TryParseExact(
            valor,
            formatos,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var data)
            ? data
            : null;
    }

    private static float ParaFeature(IReadOnlyDictionary<TipoPonto, double> distancias, TipoPonto tipo)
    {
        return distancias.TryGetValue(tipo, out var distancia) && double.IsFinite(distancia)
            ? (float)distancia
            : 1000f;
    }

    private static double DistanciaHaversineKm(
        double latitudeA,
        double longitudeA,
        double latitudeB,
        double longitudeB)
    {
        var latitudeDelta = GrausParaRadianos(latitudeB - latitudeA);
        var longitudeDelta = GrausParaRadianos(longitudeB - longitudeA);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                Math.Cos(GrausParaRadianos(latitudeA)) *
                Math.Cos(GrausParaRadianos(latitudeB)) *
                Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return 2 * RaioTerraKm * Math.Asin(Math.Sqrt(a));
    }

    private static string CriarConsultaOverpass(Limites limites)
    {
        var caixa = string.Join(",",
            limites.Sul.ToString("F6", CultureInfo.InvariantCulture),
            limites.Oeste.ToString("F6", CultureInfo.InvariantCulture),
            limites.Norte.ToString("F6", CultureInfo.InvariantCulture),
            limites.Leste.ToString("F6", CultureInfo.InvariantCulture));
        var filtros = new[]
        {
            """["railway"="station"]["station"="subway"]""",
            """["amenity"="school"]""",
            """["leisure"="park"]"""
        };
        var elementos = filtros.SelectMany(filtro => new[] { "node", "way", "relation" }
            .Select(tipo => $"{tipo}{filtro}({caixa});"));
        return $"[out:json][timeout:90];({string.Join(string.Empty, elementos)});out center;";
    }

    private static Limites CriarLimites(IReadOnlyList<ImovelTransacao> transacoes)
    {
        return new Limites(
            transacoes.Min(t => t.Latitude!.Value) - MargemBuscaGraus,
            transacoes.Min(t => t.Longitude!.Value) - MargemBuscaGraus,
            transacoes.Max(t => t.Latitude!.Value) + MargemBuscaGraus,
            transacoes.Max(t => t.Longitude!.Value) + MargemBuscaGraus);
    }

    private static bool CoordenadaValida(double? latitude, double? longitude)
    {
        return latitude is >= -90 and <= 90 &&
               longitude is >= -180 and <= 180 &&
               double.IsFinite(latitude.Value) &&
               double.IsFinite(longitude.Value);
    }

    private static double GrausParaRadianos(double graus) => graus * Math.PI / 180;

    private static string NormalizarCep(string cep)
    {
        var digitos = new string(cep.Where(char.IsDigit).ToArray());
        return digitos.Length is > 0 and <= 8 ? digitos.PadLeft(8, '0') : digitos;
    }

    private enum TipoPonto
    {
        Metro,
        Escola,
        Parque
    }

    private sealed record Coordenada(double Latitude, double Longitude);
    private sealed record Distancias(float MetroKm, float EscolaKm, float ParqueKm);
    private sealed class Limites
    {
        public double Sul { get; set; }
        public double Oeste { get; set; }
        public double Norte { get; set; }
        public double Leste { get; set; }

        public Limites()
        {
        }

        public Limites(double sul, double oeste, double norte, double leste)
        {
            Sul = sul;
            Oeste = oeste;
            Norte = norte;
            Leste = leste;
        }

        public bool Contem(Limites outros) =>
            Sul <= outros.Sul && Oeste <= outros.Oeste &&
            Norte >= outros.Norte && Leste >= outros.Leste;
    }

    private sealed class CacheOpenStreetMap
    {
        public Limites Limites { get; set; } = new();
        public List<PontoInteresse> Pontos { get; set; } = [];

        public CacheOpenStreetMap()
        {
        }

        public CacheOpenStreetMap(Limites limites, List<PontoInteresse> pontos)
        {
            Limites = limites;
            Pontos = pontos;
        }
    }

    private sealed class PontoInteresse
    {
        public TipoPonto Tipo { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public DateTime DataInicio { get; set; }

        public PontoInteresse()
        {
        }

        public PontoInteresse(
            TipoPonto tipo,
            double latitude,
            double longitude,
            DateTime dataInicio)
        {
            Tipo = tipo;
            Latitude = latitude;
            Longitude = longitude;
            DataInicio = dataInicio;
        }
    }
}
