using PrevisaoPrecosImoveis.Core.Models;

namespace PrevisaoPrecosImoveis.Core.DataCollection;

/// <summary>
/// Contrato para coletores de dados públicos governamentais.
/// Cada implementação baixa/processa uma fonte oficial.
/// </summary>
public interface IColetorDados
{
    string NomeFonte { get; }
    Task<IReadOnlyList<ImovelTransacao>> ColetarAsync(CancellationToken ct = default);
}

/// <summary>
/// Coletor de indicadores macroeconômicos (BCB SGS).
/// </summary>
public interface IColetorIndicadores
{
    Task<IReadOnlyList<IndicadorMacro>> ColetarSerieAsync(int codigoSerie, DateTime inicio, DateTime fim, CancellationToken ct = default);
}
