using PrevisaoPrecosImoveis.Api.Contracts;

namespace PrevisaoPrecosImoveis.Api.Services;

public interface IPredictionService
{
    bool ModeloCarregado { get; }

    string CaminhoModelo { get; }

    PrevisaoResponse Prever(CreatePrevisaoRequest request);
}
