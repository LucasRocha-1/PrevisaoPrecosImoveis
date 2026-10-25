namespace PrevisaoPrecosImoveis.Api.Contracts;

/// <summary>Preço estimado e intervalo aproximado retornados pelo modelo.</summary>
public sealed record PrevisaoResponse(
    float PrecoPrevisto,
    float IntervaloEstimadoInferior,
    float IntervaloEstimadoSuperior,
    DateTimeOffset DataPredicao);

/// <summary>Estado de disponibilidade do serviço de predição.</summary>
public sealed record EstadoApiResponse(bool Disponivel, bool ModeloCarregado);
