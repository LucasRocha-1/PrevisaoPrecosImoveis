namespace PrevisaoPrecosImoveis.Core.Models;

/// <summary>
/// Representa uma transação imobiliária real (ITBI) ou anúncio (FipeZap/índices).
/// Features alinhadas a regressão de preços (certificação Classificação/Clusterização/Regressão).
/// </summary>
public class ImovelTransacao
{
    // Identificação
    public string Id { get; set; } = string.Empty;
    public DateTime DataTransacao { get; set; }
    public string Fonte { get; set; } = string.Empty; // "ITBI_SP", "FipeZap", "BCB", etc.

    // Localização
    public string Cidade { get; set; } = string.Empty;
    public string Bairro { get; set; } = string.Empty;
    public string Logradouro { get; set; } = string.Empty;
    public string Cep { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // Características do imóvel
    public float AreaConstruidaM2 { get; set; }
    public float AreaTerrenoM2 { get; set; }
    public int Dormitorios { get; set; }
    public int Banheiros { get; set; }
    public int VagasGaragem { get; set; }
    public int AnoConstrucao { get; set; }
    public string TipoImovel { get; set; } = "Apartamento"; // Apartamento, Casa, Comercial
    public string PadraoConstrutivo { get; set; } = "Medio"; // Baixo, Medio, Alto, Luxo

    // Valores
    public float ValorTransacao { get; set; }          // Label principal (preço real)
    public float ValorVenal { get; set; }              // Valor venal IPTU (feature útil)
    public float PrecoPorM2 => AreaConstruidaM2 > 0 
        ? ValorTransacao / AreaConstruidaM2 
        : 0f;

    // Indicadores macroeconômicos no momento da transação (features externas)
    public float IpcaAcumulado12m { get; set; }
    public float SelicMeta { get; set; }
    public float IgpmAcumulado12m { get; set; }
    public float IndiceFipeZapCidade { get; set; }     // Índice local normalizado

    // Distâncias em linha reta; 1000 km indica ausência de coordenadas ou de POI no OSM.
    public float DistanciaMetroKm { get; set; } = 1000f;
    public float DistanciaEscolaKm { get; set; } = 1000f;
    public float DistanciaParqueKm { get; set; } = 1000f;
    public float PossuiCoordenadas { get; set; }
}

/// <summary>
/// Resultado da predição de preço.
/// </summary>
public class PredicaoPreco
{
    public float PrecoPrevisto { get; set; }
    public float IntervaloConfiancaInferior { get; set; }
    public float IntervaloConfiancaSuperior { get; set; }
}
