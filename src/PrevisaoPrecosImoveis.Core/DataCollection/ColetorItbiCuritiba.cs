using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PrevisaoPrecosImoveis.Core.Models;

namespace PrevisaoPrecosImoveis.Core.DataCollection;

/// <summary>
/// Lê arquivos CSV de transações imobiliárias de Curitiba já obtidos e normalizados
/// a partir de uma fonte pública. Não pressupõe um endpoint ou formato municipal específico.
/// </summary>
public sealed class ColetorItbiCuritiba : IColetorDados
{
    private readonly ILogger<ColetorItbiCuritiba> _logger;
    private readonly string _pastaDados;
    private readonly List<RegistroIgnorado> _registrosIgnorados = [];

    public string NomeFonte => "ITBI_Curitiba";

    public IReadOnlyList<RegistroIgnorado> RegistrosIgnorados => _registrosIgnorados;

    public ColetorItbiCuritiba(
        ILogger<ColetorItbiCuritiba> logger,
        string pastaDados = "data/raw/itbi_curitiba")
    {
        _logger = logger;
        _pastaDados = pastaDados;
        Directory.CreateDirectory(_pastaDados);
    }

    public Task<IReadOnlyList<ImovelTransacao>> ColetarAsync(CancellationToken ct = default)
    {
        _registrosIgnorados.Clear();
        var resultados = new List<ImovelTransacao>();
        var arquivos = Directory.GetFiles(_pastaDados, "*.csv")
            .OrderBy(caminho => caminho, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (arquivos.Length == 0)
        {
            _logger.LogWarning(
                "Nenhum CSV de ITBI de Curitiba foi encontrado em {Pasta}. " +
                "Consulte docs/FontesDados.md para o esquema esperado.",
                _pastaDados);
        }

        foreach (var arquivo in arquivos)
        {
            ct.ThrowIfCancellationRequested();
            var ignoradosAntes = _registrosIgnorados.Count;
            try
            {
                resultados.AddRange(LerArquivo(arquivo, ignoradosAntes, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _registrosIgnorados.Add(new RegistroIgnorado(
                    Path.GetFileName(arquivo),
                    "Arquivo CSV de ITBI de Curitiba",
                    ex.Message));
                _logger.LogWarning(ex, "O CSV de ITBI de Curitiba {Arquivo} foi ignorado.", arquivo);
            }
        }

        _logger.LogInformation(
            "Coleta local do ITBI de Curitiba finalizada: {Validos} registros válidos; " +
            "{Ignorados} linhas/arquivos ignorados.",
            resultados.Count,
            _registrosIgnorados.Count);
        return Task.FromResult<IReadOnlyList<ImovelTransacao>>(resultados);
    }

    private List<ImovelTransacao> LerArquivo(
        string caminho,
        int ignoradosAntes,
        CancellationToken ct)
    {
        var linhas = File.ReadAllLines(caminho, Encoding.UTF8);
        if (linhas.Length == 0 || string.IsNullOrWhiteSpace(linhas[0]))
        {
            throw new InvalidDataException("O arquivo CSV está vazio ou não possui cabeçalho.");
        }

        var delimitador = DetectarDelimitador(linhas[0]);
        var cabecalho = ParsearLinha(linhas[0], delimitador)
            .Select(NormalizarCabecalho)
            .ToArray();
        var colunas = CriarMapaColunas(cabecalho);

        var registros = new List<ImovelTransacao>();
        for (var indiceLinha = 1; indiceLinha < linhas.Length; indiceLinha++)
        {
            ct.ThrowIfCancellationRequested();
            var linha = linhas[indiceLinha];
            if (string.IsNullOrWhiteSpace(linha))
            {
                continue;
            }

            List<string> valores;
            try
            {
                valores = ParsearLinha(linha, delimitador);
            }
            catch (InvalidDataException ex)
            {
                _registrosIgnorados.Add(new RegistroIgnorado(
                    $"{Path.GetFileName(caminho)} / linha {indiceLinha + 1}",
                    linha,
                    $"Linha CSV ilegível: {ex.Message}"));
                continue;
            }

            string Valor(string campo) =>
                colunas.TryGetValue(campo, out var indice) && indice < valores.Count
                    ? valores[indice].Trim()
                    : string.Empty;

            var referencia = $"{Path.GetFileName(caminho)} / linha {indiceLinha + 1}";
            var dataValida = TryLerData(Valor("data"), out var data);
            var valorValido = TryLerNumero(Valor("valor"), out var valor);
            var areaValida = TryLerNumero(Valor("area"), out var area);
            var motivos = new List<string>();

            if (!dataValida)
            {
                motivos.Add("Data da transação ausente ou inválida.");
            }
            if (!valorValido || valor <= 0f)
            {
                motivos.Add("Valor da transação ausente, inválido ou não positivo.");
            }
            if (!areaValida || area <= 0f)
            {
                motivos.Add("Área construída ausente, inválida ou não positiva.");
            }

            if (motivos.Count > 0)
            {
                _registrosIgnorados.Add(new RegistroIgnorado(
                    referencia,
                    $"Data={Valor("data")}; ValorTransacao={Valor("valor")}; AreaConstruidaM2={Valor("area")}",
                    string.Join(" ", motivos)));
                continue;
            }

            var areaTerreno = LerNumeroOuZero(Valor("terreno"));
            var valorVenal = LerNumeroOuZero(Valor("valor_venal"));
            TryLerNumero(Valor("ano"), out var anoConstrucao);
            TryLerNumero(Valor("dormitorios"), out var dormitorios);
            TryLerNumero(Valor("banheiros"), out var banheiros);
            TryLerNumero(Valor("vagas"), out var vagas);

            var tipo = Valor("tipo");
            var padrao = Valor("padrao");
            registros.Add(new ImovelTransacao
            {
                Id = Guid.NewGuid().ToString("N"),
                Fonte = NomeFonte,
                Cidade = "Curitiba",
                DataTransacao = data,
                Bairro = Valor("bairro"),
                Logradouro = Valor("logradouro"),
                Cep = Valor("cep"),
                AreaConstruidaM2 = area,
                AreaTerrenoM2 = Math.Max(0f, areaTerreno),
                Dormitorios = ParaInteiroValido(dormitorios),
                Banheiros = ParaInteiroValido(banheiros),
                VagasGaragem = ParaInteiroValido(vagas),
                AnoConstrucao = anoConstrucao is >= 1600 and <= 2200
                    ? (int)anoConstrucao
                    : 0,
                TipoImovel = string.IsNullOrWhiteSpace(tipo) ? "Não informado" : tipo,
                PadraoConstrutivo = string.IsNullOrWhiteSpace(padrao) ? "Não informado" : padrao,
                ValorTransacao = valor,
                ValorVenal = Math.Max(0f, valorVenal)
            });
        }

        _logger.LogInformation(
            "Arquivo de ITBI Curitiba {Arquivo}: {Validos} válidos; {Ignorados} linhas ignoradas.",
            Path.GetFileName(caminho),
            registros.Count,
            _registrosIgnorados.Count - ignoradosAntes);
        return registros;
    }

    private static Dictionary<string, int> CriarMapaColunas(IReadOnlyList<string> cabecalho)
    {
        var aliases = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["data"] =
                ["data", "datatransacao", "datadatransacao", "datapagamento", "datadopagamento", "datadepagamento", "dataoperacao"],
            ["valor"] = ["valor", "valortransacao", "valordatransacao", "valordetransacao", "valordeclarado"],
            ["area"] =
                ["areaconstruida", "areaconstruidam2", "areaprivativa", "areaprivativam2", "areautil", "areautilm2"],
            ["terreno"] = ["areaterreno", "areaterrenom2", "areadolote"],
            ["valor_venal"] = ["valorvenal", "valorvenalimovel"],
            ["ano"] = ["anoconstrucao", "anoconstruido"],
            ["dormitorios"] = ["dormitorios", "quartos"],
            ["banheiros"] = ["banheiros"],
            ["vagas"] = ["vagas", "vagasgaragem"],
            ["bairro"] = ["bairro"],
            ["logradouro"] = ["logradouro", "endereco"],
            ["cep"] = ["cep"],
            ["tipo"] = ["tipoimovel", "tipodeimovel", "tipo"],
            ["padrao"] = ["padrao", "padraoconstrutivo", "padraoimovel"]
        };

        var colunas = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (campo, nomes) in aliases)
        {
            var indice = -1;
            for (var posicao = 0; posicao < cabecalho.Count; posicao++)
            {
                if (nomes.Contains(cabecalho[posicao], StringComparer.Ordinal))
                {
                    indice = posicao;
                    break;
                }
            }

            if (indice >= 0)
            {
                colunas[campo] = indice;
            }
        }

        var obrigatorias = new[] { "data", "valor", "area" };
        var ausentes = obrigatorias.Where(campo => !colunas.ContainsKey(campo)).ToArray();
        if (ausentes.Length > 0)
        {
            throw new InvalidDataException(
                "Cabeçalho incompatível. São necessárias colunas de data da transação, valor da transação " +
                "e área construída. Consulte docs/FontesDados.md para os nomes aceitos.");
        }

        return colunas;
    }

    private static string NormalizarCabecalho(string cabecalho)
    {
        var decomposed = cabecalho.Trim().Normalize(NormalizationForm.FormD);
        return string.Concat(decomposed
            .Where(caractere => CharUnicodeInfo.GetUnicodeCategory(caractere) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit))
            .ToLowerInvariant();
    }

    private static char DetectarDelimitador(string cabecalho)
    {
        var virgulas = ContarDelimitadores(cabecalho, ',');
        var pontoEVirgulas = ContarDelimitadores(cabecalho, ';');
        return pontoEVirgulas > virgulas ? ';' : ',';
    }

    private static int ContarDelimitadores(string linha, char delimitador)
    {
        var quantidade = 0;
        var entreAspas = false;
        for (var indice = 0; indice < linha.Length; indice++)
        {
            if (linha[indice] == '"')
            {
                if (entreAspas && indice + 1 < linha.Length && linha[indice + 1] == '"')
                {
                    indice++;
                }
                else
                {
                    entreAspas = !entreAspas;
                }
            }
            else if (!entreAspas && linha[indice] == delimitador)
            {
                quantidade++;
            }
        }

        return quantidade;
    }

    private static List<string> ParsearLinha(string linha, char delimitador)
    {
        var campos = new List<string>();
        var campo = new StringBuilder();
        var entreAspas = false;

        for (var indice = 0; indice < linha.Length; indice++)
        {
            var caractere = linha[indice];
            if (caractere == '"')
            {
                if (entreAspas && indice + 1 < linha.Length && linha[indice + 1] == '"')
                {
                    campo.Append('"');
                    indice++;
                }
                else
                {
                    entreAspas = !entreAspas;
                }
            }
            else if (caractere == delimitador && !entreAspas)
            {
                campos.Add(campo.ToString());
                campo.Clear();
            }
            else
            {
                campo.Append(caractere);
            }
        }

        if (entreAspas)
        {
            throw new InvalidDataException("A linha CSV contém aspas sem fechamento.");
        }

        campos.Add(campo.ToString());
        return campos;
    }

    private static bool TryLerData(string texto, out DateTime data)
    {
        var formatos = new[]
        {
            "dd/MM/yyyy",
            "d/M/yyyy",
            "yyyy-MM-dd",
            "yyyy-MM-dd HH:mm:ss",
            "dd/MM/yyyy HH:mm:ss",
            "d/M/yyyy H:mm:ss"
        };

        return DateTime.TryParseExact(
                   texto,
                   formatos,
                   CultureInfo.GetCultureInfo("pt-BR"),
                   DateTimeStyles.AllowWhiteSpaces,
                   out data) ||
               DateTime.TryParse(
                   texto,
                   CultureInfo.GetCultureInfo("pt-BR"),
                   DateTimeStyles.AllowWhiteSpaces,
                   out data);
    }

    private static bool TryLerNumero(string texto, out float valor)
    {
        texto = texto.Trim().Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var possuiVirgula = texto.Contains(',');
        var possuiPonto = texto.Contains('.');
        if (possuiVirgula && possuiPonto)
        {
            var cultura = texto.LastIndexOf(',') > texto.LastIndexOf('.')
                ? CultureInfo.GetCultureInfo("pt-BR")
                : CultureInfo.InvariantCulture;
            return float.TryParse(texto, NumberStyles.Number, cultura, out valor) &&
                   float.IsFinite(valor);
        }

        if (possuiVirgula)
        {
            return float.TryParse(
                       texto,
                       NumberStyles.Number,
                       CultureInfo.GetCultureInfo("pt-BR"),
                       out valor) &&
                   float.IsFinite(valor);
        }

        if (texto.Count(caractere => caractere == '.') > 1)
        {
            texto = texto.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        return float.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out valor) &&
               float.IsFinite(valor);
    }

    private static float LerNumeroOuZero(string texto) =>
        TryLerNumero(texto, out var valor) ? valor : 0f;

    private static int ParaInteiroValido(float valor) =>
        float.IsFinite(valor) && valor is >= 0f and <= int.MaxValue
            ? (int)valor
            : 0;
}
