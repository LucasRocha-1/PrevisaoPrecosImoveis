using System.Globalization;
using PrevisaoPrecosImoveis.Core.ML;
using PrevisaoPrecosImoveis.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

internal sealed record RelatorioPdfDados(
    string CaminhoPdf,
    string PastaVisualizacoes,
    IReadOnlyList<ImovelTransacao> DadosDoModelo,
    int TransacoesItbiColetadas,
    int PontosIpcaColetados,
    int PontosSelicColetados,
    int PontosFipeZapColetados,
    int RegistrosProcessados,
    bool ModeloComDadosSinteticos,
    IReadOnlyList<string> ArquivosItbi,
    IReadOnlyList<string> ArquivosFipeZap,
    IReadOnlyList<RegistroIgnorado> RegistrosIgnorados,
    ModeloRegressaoPreco Modelo);

internal static class RelatorioPdfModelo
{
    private const string UrlItbi =
        "https://prefeitura.sp.gov.br/web/fazenda/w/acesso_a_informacao/31501";
    private const string UrlBcb =
        "https://api.bcb.gov.br/dados/serie/bcdata.sgs.{codigo}/dados";
    private const string UrlFipeZap = "https://www.fipe.org.br/pt-br/indices/fipezap/";
    private const string UrlOverpass = "https://overpass-api.de/";

    public static void Gerar(RelatorioPdfDados dados)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        Directory.CreateDirectory(Path.GetDirectoryName(dados.CaminhoPdf)!);
        ExportarRegistrosIgnorados(dados);

        Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(style => style.FontSize(9).FontColor("#263238"));
                page.Header().Element(AdicionarCabecalho);
                page.Content().Column(coluna =>
                {
                    ComporResumo(coluna, dados);
                    ComporFontesEDados(coluna, dados);
                    ComporRegistrosIgnorados(coluna, dados);
                    ComporRelacionamentos(coluna);
                    ComporImportancias(coluna, dados);
                    ComporResiduos(coluna, dados);
                    ComporValidacaoTemporal(coluna, dados);
                    ComporInterpretacao(coluna, dados);
                });
                page.Footer().AlignCenter().DefaultTextStyle(style => style.FontSize(8).FontColor(Colors.Grey.Darken1))
                    .Text(texto =>
                {
                    texto.Span("Relatório gerado em ");
                    texto.Span(DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")));
                    texto.Span("  |  Página ");
                    texto.CurrentPageNumber();
                });
            });
        }).GeneratePdf(dados.CaminhoPdf);
    }

    private static void AdicionarCabecalho(IContainer container)
    {
        container.PaddingBottom(8)
            .BorderBottom(1)
            .BorderColor(Colors.Blue.Darken2)
            .Text("Previsão de preços de imóveis | Relatório de dados e modelo")
            .FontSize(10)
            .SemiBold()
            .FontColor(Colors.Blue.Darken2);
    }

    private static void ComporResumo(ColumnDescriptor coluna, RelatorioPdfDados dados)
    {
        var datas = dados.DadosDoModelo
            .Where(t => t.DataTransacao != default)
            .Select(t => t.DataTransacao)
            .ToList();
        var periodo = datas.Count == 0
            ? "não disponível"
            : $"{datas.Min():dd/MM/yyyy} a {datas.Max():dd/MM/yyyy}";
        var metricas = dados.Modelo.MetricasHoldout;

        coluna.Item().PaddingTop(10).Text("Relatório do pipeline de previsão")
            .FontSize(22).Bold().FontColor(Colors.Blue.Darken3);
        coluna.Item().PaddingTop(4).Text(
            "Este documento registra a origem dos dados, as etapas de coleta e preparação, " +
            "a relação entre variáveis e fontes e os resultados avaliados em períodos futuros.")
            .FontSize(10);

        if (dados.ModeloComDadosSinteticos)
        {
            coluna.Item().PaddingTop(8).Border(1).BorderColor(Colors.Orange.Medium)
                .Background(Colors.Orange.Lighten5).Padding(8)
                .Text("ATENÇÃO: o modelo foi treinado com dados sintéticos de demonstração. " +
                      "Os números e gráficos não representam uma avaliação de mercado real.")
                .Bold().FontColor(Colors.Orange.Darken3);
        }

        AdicionarTabela(coluna, new[]
        {
            ("Transações ITBI-SP lidas", dados.TransacoesItbiColetadas.ToString("N0", PtBr)),
            ("Registros processados", dados.RegistrosProcessados.ToString("N0", PtBr)),
            ("Registros/arquivos ignorados com problemas", dados.RegistrosIgnorados.Count.ToString("N0", PtBr)),
            ("Amostras válidas do modelo", (dados.Modelo.QuantidadeTreino + dados.Modelo.QuantidadeTeste).ToString("N0", PtBr)),
            ("Amostras de treino / holdout", $"{dados.Modelo.QuantidadeTreino:N0} / {dados.Modelo.QuantidadeTeste:N0}"),
            ("Período dos dados do modelo", periodo),
            ("Período do holdout temporal", FormatarPeriodo(dados.Modelo.DataInicioTeste, dados.Modelo.DataFimTeste)),
            ("Algoritmo", dados.Modelo.AlgoritmoSelecionado ?? "não informado"),
            ("R² / RMSE / MAE no holdout", metricas is null
                ? "não disponível"
                : $"{metricas.RSquared:F4} / {metricas.Rmse:N2} / {metricas.Mae:N2}")
        });
    }

    private static void ComporFontesEDados(ColumnDescriptor coluna, RelatorioPdfDados dados)
    {
        QuebraDePagina(coluna);
        TituloSecao(coluna, "1. Fontes e coleta dos dados");
        coluna.Item().Text(
            "As contagens abaixo são desta execução. Uma fonte consultada ou carregada não é necessariamente " +
            "uma feature efetivamente associada ao imóvel; essa distinção aparece na tabela de relações.")
            .FontSize(9);

        Tabela(coluna, new[] { "Fonte", "Coleta / processamento", "Registros", "Referência" }, tabela =>
        {
            LinhaTabela(tabela, "ITBI – Prefeitura de São Paulo",
                "Download de XLSX publicados; leitura das colunas de data, valor, áreas, bairro e cadastro.",
                $"{dados.TransacoesItbiColetadas:N0} válidas lidas; {dados.RegistrosIgnorados.Count:N0} itens ignorados",
                UrlItbi);
            LinhaTabela(tabela, "BCB SGS – IPCA 12 meses (13522)",
                "API JSON; associação por mês com defasagem conservadora de dois meses.",
                dados.PontosIpcaColetados.ToString("N0", PtBr), UrlBcb.Replace("{codigo}", "13522"));
            LinhaTabela(tabela, "BCB SGS – Meta Selic (432)",
                "API JSON; associação por mês com defasagem conservadora de dois meses.",
                dados.PontosSelicColetados.ToString("N0", PtBr), UrlBcb.Replace("{codigo}", "432"));
            LinhaTabela(tabela, "FipeZap",
                "Planilha XLSX local em data/raw/fipezap; coletor carregou os pontos, mas não os associou às transações do ITBI.",
                dados.PontosFipeZapColetados.ToString("N0", PtBr), UrlFipeZap);
            LinhaTabela(tabela, "Coordenadas de CEP",
                "Arquivo local data/raw/geolocalizacao/cep_coordenadas.csv; aproximação por centroide. A origem específica do arquivo não é registrada pelo pipeline.",
                $"{dados.DadosDoModelo.Count(t => t.PossuiCoordenadas > 0):N0} imóveis com coordenadas",
                "Informar a fonte ao preparar o arquivo");
            LinhaTabela(tabela, "OpenStreetMap / Overpass",
                "Consulta de estações, escolas e parques; distância geodésica ao POI mais próximo com data de abertura conhecida.",
                $"{ContarPontosOsm()} POIs no cache OSM",
                UrlOverpass);
        });

        coluna.Item().PaddingTop(8).Text(
            "Privacidade e limites: endereços de transações não são enviados a geocodificadores. " +
            "A consulta espacial usa coordenadas associadas localmente por CEP. Os POIs OSM sem start_date " +
            "ou opening_date são ignorados para reduzir vazamento temporal. Distâncias são em linha reta, não rotas. " +
            "A origem do arquivo de coordenadas deve ser registrada pelo responsável pelos dados.")
            .FontSize(8).FontColor(Colors.Grey.Darken2);

        coluna.Item().PaddingTop(6).Text(
            $"Arquivos ITBI XLSX encontrados: {FormatarArquivos(dados.ArquivosItbi)}")
            .FontSize(8);
        coluna.Item().Text(
            $"Arquivos FipeZap XLSX encontrados: {FormatarArquivos(dados.ArquivosFipeZap)}")
            .FontSize(8);

        var localidades = dados.DadosDoModelo
            .Where(t => !string.IsNullOrWhiteSpace(t.Cidade))
            .GroupBy(t => (t.Cidade, t.Bairro))
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => $"{g.Key.Cidade} / {(string.IsNullOrWhiteSpace(g.Key.Bairro) ? "bairro não informado" : g.Key.Bairro)} ({g.Count():N0})")
            .ToList();
        coluna.Item().PaddingTop(6).Text(localidades.Count == 0
            ? "Localidades dos dados do modelo: não informadas."
            : $"Localidades mais frequentes no conjunto usado pelo modelo: {string.Join("; ", localidades)}.")
            .FontSize(8);
    }

    private static void ComporRegistrosIgnorados(ColumnDescriptor coluna, RelatorioPdfDados dados)
    {
        QuebraDePagina(coluna);
        TituloSecao(coluna, "2. Registros ignorados e motivos");
        coluna.Item().Text(
            "Os registros abaixo foram descartados durante a leitura ou limpeza porque faltavam dados " +
            "obrigatórios, havia valores inválidos ou o arquivo/planilha não pôde ser lido. " +
            "A listagem completa também foi salva em visualizacoes/registros_ignorados.csv.")
            .FontSize(9);

        if (dados.RegistrosIgnorados.Count == 0)
        {
            coluna.Item().PaddingTop(8).Text(
                "Nenhum registro ou arquivo foi ignorado nesta execução.")
                .FontSize(9).FontColor(Colors.Green.Darken2);
            return;
        }

        coluna.Item().PaddingTop(6).Text(
            $"{dados.RegistrosIgnorados.Count:N0} registros/arquivos foram ignorados. " +
            "Resumo por motivo:")
            .FontSize(9).SemiBold();

        Tabela(coluna, new[] { "Motivo", "Quantidade" }, tabela =>
        {
            foreach (var grupo in dados.RegistrosIgnorados
                         .GroupBy(registro => registro.Motivo)
                         .OrderByDescending(grupo => grupo.Count()))
            {
                LinhaTabela(tabela, grupo.Key, grupo.Count().ToString("N0", PtBr));
            }
        });

        const int limiteDeLinhasNoPdf = 100;
        coluna.Item().PaddingTop(8).Text("Amostra dos dados descartados")
            .FontSize(10).SemiBold();
        Tabela(coluna, new[] { "Arquivo / linha", "Dados recebidos", "Por que foi ignorado" }, tabela =>
        {
            foreach (var registro in dados.RegistrosIgnorados.Take(limiteDeLinhasNoPdf))
            {
                LinhaTabela(
                    tabela,
                    registro.Referencia,
                    registro.DadosRecebidos,
                    registro.Motivo);
            }
        });

        if (dados.RegistrosIgnorados.Count > limiteDeLinhasNoPdf)
        {
            coluna.Item().PaddingTop(6).Text(
                $"O PDF mostra os primeiros {limiteDeLinhasNoPdf} itens; " +
                $"os outros {dados.RegistrosIgnorados.Count - limiteDeLinhasNoPdf:N0} estão no CSV completo.")
                .FontSize(8).FontColor(Colors.Grey.Darken2);
        }
    }

    private static void ComporRelacionamentos(ColumnDescriptor coluna)
    {
        QuebraDePagina(coluna);
        TituloSecao(coluna, "3. Como fontes, dados e variáveis se conectam");
        coluna.Item().Text(
            "Este mapa de linhagem identifica o dado observado, sua origem e o papel no modelo. " +
            "As features são explicativas; o valor da transação é o alvo previsto.")
            .FontSize(9);

        Tabela(coluna, new[] { "Dado de entrada", "Origem", "Transformação e uso no modelo" }, tabela =>
        {
            LinhaTabela(tabela, "Valor da transação",
                "ITBI-SP, coluna de valor declarado",
                "Alvo (label) da regressão; não deve ser usado como feature.");
            LinhaTabela(tabela, "Área, terreno, bairro, tipo, padrão e ano",
                "Campos do XLSX de ITBI-SP",
                "Features físicas e categóricas do imóvel.");
            LinhaTabela(tabela, "IPCA acumulado 12 meses / Selic meta",
                "BCB SGS, séries 13522 e 432",
                "Join mensal com defasagem de dois meses; contexto macroeconômico.");
            LinhaTabela(tabela, "Distâncias ao metrô, escola e parque",
                "CEP + coordenadas locais + POIs OSM/Overpass",
                "Haversine em km até o POI elegível mais próximo; marcador 1000 km se ausente.");
            LinhaTabela(tabela, "Cidade e bairro",
                "Campos de localização ITBI-SP",
                "Features categóricas; também indicam o recorte geográfico do conjunto.");
            LinhaTabela(tabela, "Índice FipeZap",
                "XLSX em data/raw/fipezap",
                "Coletado separadamente, mas sem join temporal/geográfico com ITBI; não interpretar como efeito real.");
            LinhaTabela(tabela, "Importância das features",
                "Predições no holdout temporal",
                "Aumento do RMSE após permutar uma feature; associação preditiva, não causalidade.");
            LinhaTabela(tabela, "Resíduos",
                "Valor ITBI real − preço previsto no holdout",
                "Diagnóstico do erro para as amostras futuras reservadas.");
        });
    }

    private static void ComporImportancias(ColumnDescriptor coluna, RelatorioPdfDados dados)
    {
        QuebraDePagina(coluna);
        TituloSecao(coluna, "4. Gráfico de importância das features");
        coluna.Item().Text(
            "O gráfico usa permutação no holdout cronológico. Barras maiores indicam aumento do RMSE " +
            "quando a informação é embaralhada; não provam que a variável causa alteração no preço.")
            .FontSize(9);

        AdicionarSvg(coluna, Path.Combine(dados.PastaVisualizacoes, "importancia_features.svg"), 300);
        Tabela(coluna, new[] { "Feature avaliada", "Ligação com o dado de origem" }, tabela =>
        {
            foreach (var item in dados.Modelo.ImportanciasFeatures.Take(10))
            {
                LinhaTabela(tabela, item.Nome, DescreverOrigemFeature(item.Nome));
            }
        });
    }

    private static void ComporResiduos(ColumnDescriptor coluna, RelatorioPdfDados dados)
    {
        QuebraDePagina(coluna);
        TituloSecao(coluna, "5. Gráfico de resíduos");
        coluna.Item().Text(
            "Cada ponto representa uma transação do holdout. O eixo horizontal mostra o preço previsto; " +
            "o vertical mostra o resíduo (valor real ITBI menos valor previsto). Pontos acima de zero indicam subestimação; abaixo de zero, superestimação.")
            .FontSize(9);
        AdicionarSvg(coluna, Path.Combine(dados.PastaVisualizacoes, "residuos.svg"), 300);
        coluna.Item().PaddingTop(6).Text(
            "Os valores que alimentam o gráfico são exportados em visualizacoes/residuos.csv. " +
            "A amostra pertence ao período de teste mais recente e não foi usada no ajuste final do modelo.")
            .FontSize(8).FontColor(Colors.Grey.Darken2);
    }

    private static void ComporValidacaoTemporal(ColumnDescriptor coluna, RelatorioPdfDados dados)
    {
        QuebraDePagina(coluna);
        TituloSecao(coluna, "6. Validação temporal e prevenção de vazamento");
        coluna.Item().Text(
            "Os registros são ordenados por data. O holdout contém o período mais recente; os folds expansivos " +
            "são avaliados em janelas futuras dentro do histórico anterior ao holdout. Dados do mesmo dia não são separados entre treino e teste.")
            .FontSize(9);

        var caminhoCsv = Path.Combine(dados.PastaVisualizacoes, "validacao_cruzada_temporal.csv");
        if (!File.Exists(caminhoCsv))
        {
            coluna.Item().PaddingTop(8).Text(
                "As métricas dos folds temporais não foram geradas nesta execução; consulte " +
                "visualizacoes/validacao_cruzada_temporal.csv após um treino com quantidade suficiente de datas distintas.")
                .FontSize(9);
            return;
        }

        var linhas = File.ReadAllLines(caminhoCsv)
            .Where(linha => !string.IsNullOrWhiteSpace(linha))
            .ToList();
        if (linhas.Count < 2)
        {
            coluna.Item().PaddingTop(8).Text("O arquivo de validação temporal não contém folds.")
                .FontSize(9);
            return;
        }

        Tabela(coluna, new[]
        {
            "Fold", "Treino", "Validação", "Início", "Fim", "RMSE", "MAE", "R²"
        }, tabela =>
        {
            foreach (var linha in linhas.Skip(1))
            {
                var valores = linha.Split(',');
                if (valores.Length != 8)
                {
                    continue;
                }

                LinhaTabela(tabela, valores);
            }
        });
    }

    private static void ComporInterpretacao(ColumnDescriptor coluna, RelatorioPdfDados dados)
    {
        QuebraDePagina(coluna);
        TituloSecao(coluna, "7. Leitura dos resultados e limites");

        var top = dados.Modelo.ImportanciasFeatures
            .Where(feature => feature.AumentoRmse > 0)
            .Take(3)
            .Select(feature => $"{feature.Nome} ({DescreverOrigemFeature(feature.Nome)})")
            .ToList();
        coluna.Item().Text(top.Count == 0
            ? "Nenhuma feature mostrou aumento positivo de RMSE na análise por permutação."
            : $"As três maiores contribuições preditivas observadas foram: {string.Join("; ", top)}. " +
              "Elas se referem ao conjunto e ao período avaliados e não são relações causais.")
            .FontSize(10);

        var metricas = dados.Modelo.MetricasHoldout;
        if (metricas is not null)
        {
            coluna.Item().PaddingTop(8).Text(
                $"No holdout futuro de {dados.Modelo.QuantidadeTeste:N0} registros, " +
                $"R²={metricas.RSquared:F4}, RMSE={metricas.Rmse:N2} e MAE={metricas.Mae:N2}. " +
                "RMSE penaliza erros grandes; MAE é o erro absoluto médio. Analise os resíduos juntamente com essas métricas.")
                .FontSize(9);
        }

        coluna.Item().PaddingTop(8).Text(
            "Limitações: os valores do ITBI são declarados; coordenadas por CEP são aproximadas; " +
            "a cobertura e a qualidade das tags OSM variam; o FipeZap atualmente não é associado às transações. " +
            "A importância por permutação depende da distribuição do holdout e não deve ser interpretada causalmente.")
            .FontSize(9).FontColor(Colors.Grey.Darken2);
        coluna.Item().PaddingTop(8).Text(
            "Atribuição OSM: © OpenStreetMap contributors, dados sob ODbL. Consulte as fontes e orientações de reutilização nos links deste relatório.")
            .FontSize(8).FontColor(Colors.Grey.Darken2);
    }

    private static int ContarPontosOsm()
    {
        const string caminhoCache = "data/raw/openstreetmap/pontos_interesse.json";
        if (!File.Exists(caminhoCache))
        {
            return 0;
        }

        using var documento = System.Text.Json.JsonDocument.Parse(File.ReadAllText(caminhoCache));
        return documento.RootElement.TryGetProperty("Pontos", out var pontos) &&
               pontos.ValueKind == System.Text.Json.JsonValueKind.Array
            ? pontos.GetArrayLength()
            : 0;
    }

    private static string FormatarArquivos(IReadOnlyList<string> arquivos)
    {
        return arquivos.Count == 0
            ? "nenhum"
            : string.Join(", ", arquivos.Select(Path.GetFileName));
    }

    private static void ExportarRegistrosIgnorados(RelatorioPdfDados dados)
    {
        var caminho = Path.Combine(dados.PastaVisualizacoes, "registros_ignorados.csv");
        var linhas = new List<string>
        {
            "Referencia;DadosRecebidos;Motivo"
        };
        linhas.AddRange(dados.RegistrosIgnorados.Select(registro => string.Join(
            ";",
            EscaparCsv(registro.Referencia),
            EscaparCsv(registro.DadosRecebidos),
            EscaparCsv(registro.Motivo))));
        File.WriteAllLines(caminho, linhas, System.Text.Encoding.UTF8);
    }

    private static string EscaparCsv(string valor) => $"\"{valor.Replace("\"", "\"\"")}\"";

    private static string DescreverOrigemFeature(string nome)
    {
        if (nome.Contains("metrô", StringComparison.OrdinalIgnoreCase) ||
            nome.Contains("escolas", StringComparison.OrdinalIgnoreCase) ||
            nome.Contains("parques", StringComparison.OrdinalIgnoreCase) ||
            nome.Contains("coordenadas", StringComparison.OrdinalIgnoreCase))
        {
            return "CEP e coordenadas locais; pontos OSM/Overpass";
        }

        if (nome.Contains("IPCA", StringComparison.OrdinalIgnoreCase))
        {
            return "BCB SGS 13522; associação mensal defasada";
        }

        if (nome.Contains("Selic", StringComparison.OrdinalIgnoreCase))
        {
            return "BCB SGS 432; associação mensal defasada";
        }

        if (nome.Contains("FipeZap", StringComparison.OrdinalIgnoreCase))
        {
            return "Campo existe, mas não foi associado ao ITBI nesta execução";
        }

        if (nome.Contains("venal", StringComparison.OrdinalIgnoreCase))
        {
            return "Campo de valor venal lido na planilha ITBI";
        }

        if (new[]
            {
                "área", "dormitórios", "banheiros", "garagem", "construção",
                "imóvel", "cidade", "bairro", "tipo", "padrão"
            }.Any(termo => nome.Contains(termo, StringComparison.OrdinalIgnoreCase)))
        {
            return "Campos físicos, categóricos ou de localização do ITBI-SP";
        }

        return "Campo tabular do conjunto de treino; consulte a tabela de linhagem";
    }

    private static void AdicionarSvg(ColumnDescriptor coluna, string caminho, float altura)
    {
        if (File.Exists(caminho))
        {
            var svg = File.ReadAllText(caminho);
            coluna.Item().PaddingTop(8).Height(altura).Svg(svg);
        }
        else
        {
            coluna.Item().PaddingTop(8).Text($"Gráfico não encontrado: {caminho}")
                .FontColor(Colors.Grey.Darken1);
        }
    }

    private static void TituloSecao(ColumnDescriptor coluna, string titulo)
    {
        coluna.Item().PaddingTop(8).PaddingBottom(6)
            .Text(titulo).FontSize(15).Bold().FontColor(Colors.Blue.Darken3);
    }

    private static void QuebraDePagina(ColumnDescriptor coluna)
    {
        coluna.Item().PageBreak();
    }

    private static void AdicionarTabela(
        ColumnDescriptor coluna,
        IReadOnlyList<(string Rotulo, string Valor)> linhas)
    {
        Tabela(coluna, new[] { "Indicador", "Valor" }, tabela =>
        {
            foreach (var (rotulo, valor) in linhas)
            {
                LinhaTabela(tabela, rotulo, valor);
            }
        });
    }

    private static void Tabela(
        ColumnDescriptor coluna,
        IReadOnlyList<string> cabecalhos,
        Action<TableDescriptor> preencher)
    {
        coluna.Item().PaddingTop(8).Table(tabela =>
        {
            tabela.ColumnsDefinition(colunas =>
            {
                foreach (var _ in cabecalhos)
                {
                    colunas.RelativeColumn();
                }
            });
            tabela.Header(cabecalho =>
            {
                foreach (var texto in cabecalhos)
                {
                    cabecalho.Cell().Element(CelulaCabecalho).Text(texto).Bold().FontSize(8);
                }
            });
            preencher(tabela);
        });
    }

    private static void LinhaTabela(TableDescriptor tabela, params string[] valores)
    {
        foreach (var valor in valores)
        {
            tabela.Cell().Element(CelulaCorpo).Text(valor).FontSize(7);
        }
    }

    private static void LinhaTabela(
        TableDescriptor tabela,
        string fonte,
        string coleta,
        string contagem,
        string referencia)
    {
        LinhaTabela(tabela, new[] { fonte, coleta, contagem, referencia });
    }

    private static IContainer CelulaCabecalho(IContainer container)
    {
        return container.Background(Colors.Blue.Darken2)
            .PaddingVertical(5).PaddingHorizontal(4).DefaultTextStyle(style => style.FontColor(Colors.White));
    }

    private static IContainer CelulaCorpo(IContainer container)
    {
        return container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
            .PaddingVertical(5).PaddingHorizontal(4);
    }

    private static string FormatarPeriodo(DateTime? inicio, DateTime? fim)
    {
        return inicio is null || fim is null
            ? "não disponível"
            : $"{inicio.Value:dd/MM/yyyy} a {fim.Value:dd/MM/yyyy}";
    }

    private static CultureInfo PtBr { get; } = CultureInfo.GetCultureInfo("pt-BR");
}
