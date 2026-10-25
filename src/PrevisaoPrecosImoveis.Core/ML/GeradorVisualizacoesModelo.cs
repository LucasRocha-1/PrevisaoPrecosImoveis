using System.Globalization;
using System.Security;
using System.Text;

namespace PrevisaoPrecosImoveis.Core.ML;

internal static class GeradorVisualizacoesModelo
{
    private const int Largura = 1000;
    private const int Altura = 600;
    private const int MaximoPontosGrafico = 3000;

    public static void Salvar(
        string pasta,
        IReadOnlyList<ImportanciaFeature> importancias,
        IReadOnlyList<ResiduoModelo> residuos)
    {
        Directory.CreateDirectory(pasta);
        File.WriteAllText(
            Path.Combine(pasta, "importancia_features.svg"),
            CriarGraficoImportancia(importancias),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(pasta, "residuos.svg"),
            CriarGraficoResiduos(residuos),
            Encoding.UTF8);
        SalvarResiduosCsv(Path.Combine(pasta, "residuos.csv"), residuos);
    }

    private static string CriarGraficoImportancia(IReadOnlyList<ImportanciaFeature> importancias)
    {
        const int margemEsquerda = 250;
        const int margemDireita = 40;
        const int margemSuperior = 80;
        const int margemInferior = 50;
        var valores = importancias.Select(item => item.AumentoRmse).ToList();
        var maximo = Math.Max(valores.Select(Math.Abs).DefaultIfEmpty(0).Max(), 1e-9);
        var larguraGrafico = Largura - margemEsquerda - margemDireita;
        var alturaGrafico = Altura - margemSuperior - margemInferior;
        var passo = alturaGrafico / (double)Math.Max(importancias.Count, 1);
        var eixoX = margemEsquerda + larguraGrafico / 2.0;
        var sb = IniciarSvg("Importância das variáveis (permutação)", Largura, Altura);

        sb.AppendLine($"<text x=\"{Largura / 2}\" y=\"40\" class=\"title\" text-anchor=\"middle\">Importância das variáveis (permutação)</text>");
        sb.AppendLine($"<text x=\"{Largura / 2}\" y=\"62\" class=\"subtitle\" text-anchor=\"middle\">Aumento do RMSE após embaralhar cada variável</text>");
        sb.AppendLine($"<line x1=\"{F(eixoX)}\" y1=\"{margemSuperior}\" x2=\"{F(eixoX)}\" y2=\"{Altura - margemInferior}\" class=\"axis\"/>");

        for (var i = 0; i < importancias.Count; i++)
        {
            var item = importancias[i];
            var y = margemSuperior + i * passo + passo * 0.18;
            var alturaBarra = Math.Max(2, passo * 0.62);
            var larguraBarra = Math.Abs(item.AumentoRmse) / maximo * (larguraGrafico / 2.0);
            var x = item.AumentoRmse >= 0 ? eixoX : eixoX - larguraBarra;
            var cor = item.AumentoRmse >= 0 ? "#2563eb" : "#dc2626";

            sb.AppendLine($"<text x=\"{margemEsquerda - 12}\" y=\"{F(y + alturaBarra * 0.78)}\" class=\"label\" text-anchor=\"end\">{E(item.Nome)}</text>");
            sb.AppendLine($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(larguraBarra)}\" height=\"{F(alturaBarra)}\" fill=\"{cor}\" rx=\"3\"/>");
            var valorX = item.AumentoRmse >= 0 ? x + larguraBarra + 6 : x - 6;
            var ancoragem = item.AumentoRmse >= 0 ? "start" : "end";
            sb.AppendLine($"<text x=\"{F(valorX)}\" y=\"{F(y + alturaBarra * 0.78)}\" class=\"value\" text-anchor=\"{ancoragem}\">{F(item.AumentoRmse)}</text>");
        }

        sb.AppendLine($"<text x=\"{Largura / 2}\" y=\"{Altura - 12}\" class=\"axis-label\" text-anchor=\"middle\">Aumento do RMSE (valores positivos indicam maior importância)</text>");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string CriarGraficoResiduos(IReadOnlyList<ResiduoModelo> residuos)
    {
        const int margemEsquerda = 90;
        const int margemDireita = 35;
        const int margemSuperior = 85;
        const int margemInferior = 65;
        var larguraGrafico = Largura - margemEsquerda - margemDireita;
        var alturaGrafico = Altura - margemSuperior - margemInferior;
        var sb = IniciarSvg("Resíduos do modelo", Largura, Altura);

        sb.AppendLine($"<text x=\"{Largura / 2}\" y=\"40\" class=\"title\" text-anchor=\"middle\">Resíduos no conjunto de teste</text>");
        sb.AppendLine($"<text x=\"{Largura / 2}\" y=\"62\" class=\"subtitle\" text-anchor=\"middle\">Resíduo = valor real − valor previsto</text>");

        if (residuos.Count == 0)
        {
            sb.AppendLine($"<text x=\"{Largura / 2}\" y=\"{Altura / 2}\" class=\"label\" text-anchor=\"middle\">Sem dados para exibir</text>");
            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        var pontos = SelecionarPontos(residuos);
        var minX = pontos.Min(item => item.ValorPrevisto);
        var maxX = pontos.Max(item => item.ValorPrevisto);
        var limiteY = Math.Max(pontos.Max(item => Math.Abs(item.Residuo)), 1);
        var amplitudeX = Math.Max(maxX - minX, 1);
        var eixoY = margemSuperior + alturaGrafico / 2.0;
        var zeroY = ConverterY(0, -limiteY, limiteY, margemSuperior, alturaGrafico);

        sb.AppendLine($"<line x1=\"{margemEsquerda}\" y1=\"{margemSuperior + alturaGrafico}\" x2=\"{margemEsquerda + larguraGrafico}\" y2=\"{margemSuperior + alturaGrafico}\" class=\"axis\"/>");
        sb.AppendLine($"<line x1=\"{margemEsquerda}\" y1=\"{margemSuperior}\" x2=\"{margemEsquerda}\" y2=\"{margemSuperior + alturaGrafico}\" class=\"axis\"/>");
        sb.AppendLine($"<line x1=\"{margemEsquerda}\" y1=\"{F(zeroY)}\" x2=\"{margemEsquerda + larguraGrafico}\" y2=\"{F(zeroY)}\" class=\"zero\"/>");

        foreach (var ponto in pontos)
        {
            var x = margemEsquerda + (ponto.ValorPrevisto - minX) / amplitudeX * larguraGrafico;
            var y = ConverterY(ponto.Residuo, -limiteY, limiteY, margemSuperior, alturaGrafico);
            sb.AppendLine($"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"3\" fill=\"#2563eb\" fill-opacity=\"0.45\"/>");
        }

        sb.AppendLine($"<text x=\"{Largura / 2}\" y=\"{Altura - 18}\" class=\"axis-label\" text-anchor=\"middle\">Valor previsto (R$)</text>");
        sb.AppendLine($"<text x=\"22\" y=\"{F(eixoY)}\" class=\"axis-label\" text-anchor=\"middle\" transform=\"rotate(-90 22 {F(eixoY)})\">Resíduo (R$)</text>");
        sb.AppendLine($"<text x=\"{margemEsquerda - 10}\" y=\"{margemSuperior + 5}\" class=\"value\" text-anchor=\"end\">{F(limiteY)}</text>");
        sb.AppendLine($"<text x=\"{margemEsquerda - 10}\" y=\"{margemSuperior + alturaGrafico}\" class=\"value\" text-anchor=\"end\">-{F(limiteY)}</text>");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static IReadOnlyList<ResiduoModelo> SelecionarPontos(IReadOnlyList<ResiduoModelo> residuos)
    {
        if (residuos.Count <= MaximoPontosGrafico)
        {
            return residuos;
        }

        var intervalo = residuos.Count / (double)MaximoPontosGrafico;
        return Enumerable.Range(0, MaximoPontosGrafico)
            .Select(indice => residuos[(int)(indice * intervalo)])
            .ToList();
    }

    private static void SalvarResiduosCsv(string caminho, IReadOnlyList<ResiduoModelo> residuos)
    {
        var linhas = new List<string>(residuos.Count + 1)
        {
            "ValorReal,ValorPrevisto,Residuo"
        };
        linhas.AddRange(residuos.Select(item =>
            string.Join(",",
                item.ValorReal.ToString("R", CultureInfo.InvariantCulture),
                item.ValorPrevisto.ToString("R", CultureInfo.InvariantCulture),
                item.Residuo.ToString("R", CultureInfo.InvariantCulture))));
        File.WriteAllLines(caminho, linhas, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static StringBuilder IniciarSvg(string titulo, int largura, int altura)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{largura}\" height=\"{altura}\" viewBox=\"0 0 {largura} {altura}\">");
        sb.AppendLine("<style>text{font-family:Segoe UI,Arial,sans-serif;fill:#1f2937}.title{font-size:22px;font-weight:600}.subtitle{font-size:13px;fill:#6b7280}.label{font-size:13px}.value{font-size:11px;fill:#4b5563}.axis-label{font-size:12px}.axis{stroke:#6b7280;stroke-width:1}.zero{stroke:#dc2626;stroke-width:1.5;stroke-dasharray:6 4}</style>");
        sb.AppendLine($"<title>{E(titulo)}</title>");
        return sb;
    }

    private static double ConverterY(double valor, double minimo, double maximo, int topo, int altura)
    {
        return topo + (maximo - valor) / (maximo - minimo) * altura;
    }

    private static string E(string texto) => SecurityElement.Escape(texto) ?? string.Empty;

    private static string F(double valor) => valor.ToString("0.###", CultureInfo.InvariantCulture);
}
