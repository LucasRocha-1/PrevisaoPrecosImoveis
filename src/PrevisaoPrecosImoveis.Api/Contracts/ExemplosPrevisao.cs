namespace PrevisaoPrecosImoveis.Api.Contracts;

/// <summary>Corpos de requisição prontos para o Swagger e para GET /api/v1/previsoes/exemplos.</summary>
public static class ExemplosPrevisao
{
    public static CreatePrevisaoRequest ApartamentoPinheiros { get; } = new()
    {
        Cidade = "São Paulo",
        Bairro = "Pinheiros",
        AreaConstruidaM2 = 85,
        AreaTerrenoM2 = 0,
        Dormitorios = 2,
        Banheiros = 2,
        VagasGaragem = 1,
        AnoConstrucao = 2015,
        TipoImovel = "Apartamento",
        PadraoConstrutivo = "Medio",
        ValorVenal = 650_000,
        IpcaAcumulado12m = 4.5f,
        SelicMeta = 10.5f,
        IndiceFipeZapCidade = 0,
        DistanciaMetroKm = 0.5f,
        DistanciaEscolaKm = 0.3f,
        DistanciaParqueKm = 1.2f,
        PossuiCoordenadas = true,
        DataTransacao = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(-3))
    };

    public static CreatePrevisaoRequest CasaMoema { get; } = new()
    {
        Cidade = "São Paulo",
        Bairro = "Moema",
        AreaConstruidaM2 = 220,
        AreaTerrenoM2 = 250,
        Dormitorios = 4,
        Banheiros = 3,
        VagasGaragem = 2,
        AnoConstrucao = 2008,
        TipoImovel = "Casa",
        PadraoConstrutivo = "Alto",
        ValorVenal = 1_800_000,
        IpcaAcumulado12m = 4.5f,
        SelicMeta = 10.5f,
        IndiceFipeZapCidade = 0,
        DistanciaMetroKm = 0.8f,
        DistanciaEscolaKm = 0.4f,
        DistanciaParqueKm = 0.6f,
        PossuiCoordenadas = true,
        DataTransacao = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.FromHours(-3))
    };

    public static CreatePrevisaoRequest ApartamentoCompactoSantana { get; } = new()
    {
        Cidade = "São Paulo",
        Bairro = "Santana",
        AreaConstruidaM2 = 48,
        AreaTerrenoM2 = 0,
        Dormitorios = 1,
        Banheiros = 1,
        VagasGaragem = 0,
        AnoConstrucao = 1998,
        TipoImovel = "Apartamento",
        PadraoConstrutivo = "Baixo",
        ValorVenal = 280_000,
        IpcaAcumulado12m = 4.5f,
        SelicMeta = 10.5f,
        IndiceFipeZapCidade = 0,
        DistanciaMetroKm = 0.2f,
        DistanciaEscolaKm = 0.5f,
        DistanciaParqueKm = 1.8f,
        PossuiCoordenadas = true,
        DataTransacao = new DateTimeOffset(2025, 11, 3, 15, 30, 0, TimeSpan.FromHours(-3))
    };

    public static CreatePrevisaoRequest SemCoordenadas { get; } = new()
    {
        Cidade = "São Paulo",
        Bairro = "Tatuapé",
        AreaConstruidaM2 = 110,
        AreaTerrenoM2 = 0,
        Dormitorios = 3,
        Banheiros = 2,
        VagasGaragem = 1,
        AnoConstrucao = 2010,
        TipoImovel = "Apartamento",
        PadraoConstrutivo = "Medio",
        ValorVenal = 720_000,
        IpcaAcumulado12m = 4.5f,
        SelicMeta = 10.5f,
        IndiceFipeZapCidade = 0,
        DistanciaMetroKm = 1000,
        DistanciaEscolaKm = 1000,
        DistanciaParqueKm = 1000,
        PossuiCoordenadas = false,
        DataTransacao = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(-3))
    };

    public static CreatePrevisaoRequest ApartamentoCuritiba { get; } = new()
    {
        Cidade = "Curitiba",
        Bairro = "Centro",
        AreaConstruidaM2 = 78,
        AreaTerrenoM2 = 0,
        Dormitorios = 2,
        Banheiros = 2,
        VagasGaragem = 1,
        AnoConstrucao = 2012,
        TipoImovel = "Apartamento",
        PadraoConstrutivo = "Medio",
        ValorVenal = 0,
        IpcaAcumulado12m = 4.5f,
        SelicMeta = 10.5f,
        IndiceFipeZapCidade = 0,
        DistanciaMetroKm = 1000,
        DistanciaEscolaKm = 1000,
        DistanciaParqueKm = 1000,
        PossuiCoordenadas = false,
        DataTransacao = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.FromHours(-3))
    };

    public static IReadOnlyList<ExemploPrevisaoDocumentado> Todos { get; } =
    [
        new(
            "apartamentoPinheiros",
            "Apartamento em Pinheiros",
            "Exemplo válido de apartamento com coordenadas e distâncias em km.",
            ApartamentoPinheiros),
        new(
            "casaMoema",
            "Casa em Moema",
            "Casa com terreno, padrão alto e valor venal correspondente.",
            CasaMoema),
        new(
            "apartamentoCompactoSantana",
            "Apartamento compacto em Santana",
            "Imóvel menor, útil para comparar a estimativa com o exemplo de Pinheiros.",
            ApartamentoCompactoSantana),
        new(
            "semCoordenadas",
            "Sem coordenadas",
            "Quando não há geolocalização, use 1000 km e possuiCoordenadas = false.",
            SemCoordenadas),
        new(
            "apartamentoCuritiba",
            "Apartamento em Curitiba",
            "Exemplo de entrada para Curitiba. A qualidade da estimativa depende do treinamento com dados reais da cidade.",
            ApartamentoCuritiba)
    ];
}

public sealed record ExemploPrevisaoDocumentado(
    string Id,
    string Titulo,
    string Descricao,
    CreatePrevisaoRequest Corpo);

public sealed record ModeloInfoResponse(
    bool ModeloCarregado,
    string CaminhoModelo,
    string Descricao,
    IReadOnlyList<string> CamposEntrada);
