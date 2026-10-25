using System.ComponentModel.DataAnnotations;

namespace PrevisaoPrecosImoveis.Api.Contracts;

/// <summary>Dados de um imóvel para estimar o preço de transação.</summary>
public sealed record CreatePrevisaoRequest
{
    /// <summary>Cidade do imóvel.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string Cidade { get; init; }

    /// <summary>Bairro do imóvel.</summary>
    [Required, StringLength(150, MinimumLength = 1)]
    public required string Bairro { get; init; }

    /// <summary>Área construída em metros quadrados.</summary>
    [Range(0.1, 100000)]
    public float AreaConstruidaM2 { get; init; }

    /// <summary>Área do terreno em metros quadrados.</summary>
    [Range(0, 1000000)]
    public float AreaTerrenoM2 { get; init; }

    /// <summary>Número de dormitórios.</summary>
    [Range(0, 100)]
    public int Dormitorios { get; init; }

    /// <summary>Número de banheiros.</summary>
    [Range(0, 100)]
    public int Banheiros { get; init; }

    /// <summary>Número de vagas de garagem.</summary>
    [Range(0, 100)]
    public int VagasGaragem { get; init; }

    /// <summary>Ano de construção, quando conhecido.</summary>
    [Range(1600, 2200)]
    public int AnoConstrucao { get; init; }

    /// <summary>Categoria do imóvel, como Apartamento ou Casa.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string TipoImovel { get; init; }

    /// <summary>Padrão construtivo informado para o imóvel.</summary>
    [Required, StringLength(150, MinimumLength = 1)]
    public required string PadraoConstrutivo { get; init; }

    /// <summary>Valor venal do imóvel em reais, se disponível.</summary>
    [Range(0, float.MaxValue)]
    public float ValorVenal { get; init; }

    /// <summary>IPCA acumulado em 12 meses, em percentual.</summary>
    [Range(-100, 1000)]
    public float IpcaAcumulado12m { get; init; }

    /// <summary>Meta Selic, em percentual.</summary>
    [Range(0, 1000)]
    public float SelicMeta { get; init; }

    /// <summary>Índice FipeZap local normalizado, se já associado ao período.</summary>
    [Range(0, 1000000)]
    public float IndiceFipeZapCidade { get; init; }

    /// <summary>Distância em linha reta ao metrô mais próximo, em quilômetros.</summary>
    [Range(0, 1000)]
    public float DistanciaMetroKm { get; init; } = 1000f;

    /// <summary>Distância em linha reta à escola mais próxima, em quilômetros.</summary>
    [Range(0, 1000)]
    public float DistanciaEscolaKm { get; init; } = 1000f;

    /// <summary>Distância em linha reta ao parque mais próximo, em quilômetros.</summary>
    [Range(0, 1000)]
    public float DistanciaParqueKm { get; init; } = 1000f;

    /// <summary>Indica se as coordenadas do imóvel estão disponíveis.</summary>
    public bool PossuiCoordenadas { get; init; }

    /// <summary>Data da transação ou referência da previsão, com offset ISO 8601.</summary>
    public DateTimeOffset? DataTransacao { get; init; }
}
