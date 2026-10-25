namespace PrevisaoPrecosImoveis.Core.Models;

/// <summary>
/// Série temporal de indicadores macroeconômicos (BCB SGS / IBGE).
/// Usados como features externas no modelo de regressão.
/// </summary>
public class IndicadorMacro
{
    public DateTime Data { get; set; }
    public string CodigoSerie { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;
    public float Valor { get; set; }
}

/// <summary>
/// Códigos oficiais das séries do Banco Central (SGS) e referências.
/// </summary>
public static class SeriesBcb
{
    // Inflação
    public const int IpcaMensal = 433;
    public const int IpcaAcumulado12Meses = 13522;
    public const int IgpmMensal = 189;

    // Juros
    public const int SelicMeta = 432;
    public const int SelicEfetiva = 1178;
    public const int Tr = 226;

    // Crédito imobiliário (Mercado Imobiliário BCB)
    // Endpoint OData: https://olinda.bcb.gov.br/olinda/servico/MercadoImobiliario/versao/v1/odata/
}
