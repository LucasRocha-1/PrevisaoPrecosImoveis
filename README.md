# Previsão de Preços de Imóveis no Brasil (C# / ML.NET)

Esqueleto de projeto em **.NET 8** para prever preços de imóveis residenciais usando **dados públicos oficiais** do governo brasileiro e técnicas de **Machine Learning (regressão)**.

---

## Arquitetura

```
PrevisaoPrecosImoveis/
├── PrevisaoPrecosImoveis.sln
├── src/
│   ├── PrevisaoPrecosImoveis.Console/     # Aplicação de entrada (pipeline completo)
│   ├── PrevisaoPrecosImoveis.Api/         # API ASP.NET Core para predições
│   └── PrevisaoPrecosImoveis.Core/
│       ├── Models/                        # ImovelTransacao, IndicadorMacro, PredicaoPreco
│       ├── DataCollection/                # Coletores de fontes públicas
│       │   ├── ColetorItbiSaoPaulo.cs     # ITBI-SP (transações reais)
│       │   ├── ColetorBcb.cs              # Séries SGS + Mercado Imobiliário BCB
│       │   └── ColetorFipeZap.cs          # Índice FipeZap
│       ├── Preprocessing/                 # Limpeza, feature engineering, DataFrame
│       └── ML/                            # Pipeline ML.NET (AutoML e FastTree fallback)
├── data/
│   ├── raw/          # Arquivos baixados (ITBI Excel, FipeZap, CSVs)
│   ├── processed/    # CSVs limpos prontos para treino
│   └── models/       # Modelos .zip salvos
├── visualizacoes/    # Gráficos, resíduos e métricas da validação temporal
└── docs/
```

---

## Fontes de Dados Públicos Utilizadas

| Fonte | Tipo | O que fornece | Link oficial |
|-------|------|---------------|--------------|
| **Prefeitura de São Paulo – ITBI** | Transações reais | Valor declarado, área, endereço, data | [Acesso à Informação – ITBI](https://prefeitura.sp.gov.br/web/fazenda/w/acesso_a_informacao/31501) |
| **Banco Central (SGS)** | Séries temporais | IPCA, Selic, IGP-M, TR | [API SGS](https://api.bcb.gov.br/dados/serie/bcdata.sgs.{codigo}/dados) |
| **BCB – Mercado Imobiliário** | Crédito e estoque | Concessões, estoque de crédito imobiliário | [Olinda OData](https://olinda.bcb.gov.br/olinda/servico/MercadoImobiliario/) |
| **FipeZap** | Índices de preços pedidos | Variação mensal por cidade (venda/aluguel) | [Fipe – Índice FipeZap](https://www.fipe.org.br/pt-br/indices/fipezap/) |
| **GeoSampa / Base dos Dados** | Cadastro IPTU | Valor venal, área, padrão construtivo (SP) | [GeoSampa](https://geosampa.prefeitura.sp.gov.br/) / [Base dos Dados](https://basedosdados.org/) |
| **OpenStreetMap** | POIs geográficos | Estações de metrô, escolas e parques | [Overpass API](https://overpass-api.de/) |

---

## Técnicas Aplicadas

1. **Coleta de dados públicos**
   - HTTP download de Excel/CSV oficiais
   - Consumo de API REST do BCB (SGS e Olinda)
   - Parsing tabular com ClosedXML e System.Text.Json

2. **Pré-processamento (Data Preprocessing)**
   - Limpeza de nulos e valores inválidos
   - Detecção de outliers (IQR)
   - Feature engineering: idade do imóvel na data da transação e densidade construtiva; variáveis derivadas do preço-alvo não entram no treino
   - Enriquecimento temporal com IPCA e Selic com defasagem conservadora de dois meses
   - Distâncias em linha reta ao metrô, escolas e parques mapeados no OpenStreetMap
   - Encoding categórico (One-Hot) e normalização Min-Max

3. **Machine Learning – Regressão**
   - Seleção automática de regressor com **ML.NET AutoML**, avaliada em partições cronológicas
   - Fallback explícito para FastTree se o AutoML esgotar o tempo sem selecionar um modelo
   - Validação walk-forward com três folds temporais expansivos e holdout cronológico final (sem embaralhamento)
   - Avaliação: **R², RMSE, MAE**
   - Persistência e carga de modelo (`.zip`)
   - Pronto para evolução com AutoML / LightGBM

4. **Análise e Visualização**
   - Importância por permutação e gráfico de resíduos no holdout temporal
   - CSV de métricas de cada fold temporal
   - `Microsoft.Data.Analysis` (DataFrame) e exportação CSV dos dados processados

---

## Como executar

### Pré-requisitos
- .NET 8 SDK
- (Opcional) Visual Studio 2022 / VS Code + C# Dev Kit

### Passos

```bash
cd PrevisaoPrecosImoveis

# Restaurar pacotes
dotnet restore

# Compilar
dotnet build

# Executar o pipeline
dotnet run --project src/PrevisaoPrecosImoveis.Console
```

### Dados reais
1. Na execução, os arquivos XLSX publicados na página oficial do ITBI-SP são localizados e baixados automaticamente para `data/raw/itbi_sp/`. Arquivos já existentes não são baixados novamente.
2. Baixe a planilha histórica do FipeZap e coloque em `data/raw/fipezap/`.
3. As séries do BCB são baixadas automaticamente via API.
4. Para ativar as features espaciais, crie `data/raw/geolocalizacao/cep_coordenadas.csv` com o cabeçalho e formato abaixo. Use coordenadas públicas/centroides de CEP; o pipeline associa o CEP do ITBI e não envia endereços de transações para geocodificadores.

```csv
Cep,Latitude,Longitude
01310000,-23.561414,-46.655881
```

Com coordenadas associadas, o pipeline consulta metrô, escolas e parques no Overpass/OSM uma vez e salva o cache em `data/raw/openstreetmap/pontos_interesse.json`. Para evitar usar infraestrutura criada depois da transação, só são considerados POIs com `start_date` ou `opening_date` conhecido e anterior à data da venda; POIs sem essa informação são excluídos. Distâncias são geodésicas (linha reta), não rotas. Ausência de coordenadas ou de POI elegível recebe o marcador `1000 km`, acompanhado da feature `PossuiCoordenadas`; revise essa flag antes de interpretar resultados.

O holdout contém os registros mais recentes, e os três folds walk-forward avaliam períodos sucessivos dentro do histórico anterior. As métricas são registradas em `visualizacoes/validacao_cruzada_temporal.csv`; o treinamento final não usa o holdout. Os indicadores mensais do BCB são defasados em dois meses para reduzir risco de usar informações ainda não publicadas na data da transação.

As figuras `visualizacoes/importancia_features.svg` e `visualizacoes/residuos.svg`, os CSVs de resíduos e validação e o relatório `visualizacoes/relatorio_modelo.pdf` são gerados ao treinar. O PDF é composto com os dados da execução: métricas, período e localidades, fontes e arquivos lidos, contagens processadas e ignoradas, linhagem das features e gráficos. A seção de registros ignorados apresenta os dados recebidos e uma explicação breve do motivo (por exemplo, data inválida, preço não positivo ou área construída inválida). Para consultar todos os itens quando a lista for extensa, use `visualizacoes/registros_ignorados.csv`; o PDF mostra até 100 linhas de exemplo. O relatório sinaliza quando foram usados dados sintéticos e quando uma fonte (como FipeZap) foi carregada sem ser associada ao treino. Registre também a fonte específica do arquivo de coordenadas usado. Os dados e mapas do OpenStreetMap são © OpenStreetMap contributors, disponibilizados sob ODbL; respeite os limites do Overpass e mantenha a atribuição ao reutilizar os dados.

O coletor de ITBI usa somente links HTTPS da Prefeitura e arquivos XLSX; arquivos ODS ainda não são processados. Se a página oficial estiver indisponível, os arquivos ITBI já existentes serão usados. Se não houver dados suficientes, o programa gera **dados sintéticos** apenas para demonstrar o fluxo de treino e predição.

### API de predições

Primeiro execute o pipeline do console para treinar e gravar `data/models/modelo_preco_imovel.zip`. Em seguida, inicie a API na raiz da solução:

```bash
dotnet run --project src/PrevisaoPrecosImoveis.Api --urls http://localhost:5080
```

A URL raiz `http://localhost:5080/` redireciona para a documentação interativa Swagger, também disponível em `http://localhost:5080/swagger`; o documento OpenAPI fica em `http://localhost:5080/swagger/v1/swagger.json`. Endpoints:

| Método e caminho | Resultado |
|------------------|-----------|
| `GET /health` | Confirma que a API está ativa e informa se o modelo foi carregado |
| `GET /health/ready` | Retorna 200 quando o modelo está disponível ou 503 enquanto não foi treinado/encontrado |
| `GET /api/v1/modelo` | Mostra o estado do modelo e os campos esperados |
| `GET /api/v1/previsoes/exemplos` | Lista os corpos prontos para testar uma previsão |
| `POST /api/v1/previsoes` | Recebe as características do imóvel e retorna a estimativa; entrada inválida retorna 400 |

No Swagger, abra **Previsões → POST /api/v1/previsoes → Try it out** e escolha no seletor de exemplos: apartamento em Pinheiros, casa em Moema, apartamento compacto em Santana ou imóvel sem coordenadas. Os corpos já vêm preenchidos com dados válidos; após executar o treinamento e carregar o modelo, cada opção pode ser enviada diretamente. As seções **Saúde** e **Modelo** também permitem verificar a API e a disponibilidade do modelo.

Exemplo mínimo de corpo para `POST /api/v1/previsoes` (distâncias espaciais em quilômetros):

```json
{
  "cidade": "São Paulo",
  "bairro": "Pinheiros",
  "areaConstruidaM2": 85,
  "areaTerrenoM2": 0,
  "dormitorios": 2,
  "banheiros": 2,
  "vagasGaragem": 1,
  "anoConstrucao": 2015,
  "tipoImovel": "Apartamento",
  "padraoConstrutivo": "Medio",
  "valorVenal": 650000,
  "ipcaAcumulado12m": 4.5,
  "selicMeta": 10.5,
  "indiceFipeZapCidade": 0,
  "distanciaMetroKm": 0.5,
  "distanciaEscolaKm": 0.3,
  "distanciaParqueKm": 1.2,
  "possuiCoordenadas": true,
  "dataTransacao": "2025-01-15T12:00:00-03:00"
}
```

Os campos de entrada correspondem às features do modelo; para variáveis espaciais ausentes, use `1000` km e `possuiCoordenadas: false`. O caminho do modelo pode ser sobrescrito pela configuração `ModelPath` ou variável de ambiente `ModelPath`. Se o arquivo ainda não existir, a API inicia, mas readiness e predições informam indisponibilidade em vez de retornar uma estimativa fictícia. O arquivo [app.http](src/PrevisaoPrecosImoveis.Api/app.http) contém exemplos de chamadas.

---

## Estrutura de pacotes NuGet principais

| Pacote | Uso |
|--------|-----|
| `Microsoft.ML` + `Microsoft.ML.AutoML` + `Microsoft.ML.FastTree` | Seleção de regressão com AutoML e fallback |
| `Swashbuckle.AspNetCore` | Swagger/OpenAPI da API |
| `Microsoft.Data.Analysis` | DataFrames e análise tabular |
| `ClosedXML` | Leitura de Excel (ITBI, FipeZap) |
| `QuestPDF` | Relatório PDF de fontes, coleta, linhagem e resultados |
| `Microsoft.Extensions.Hosting` | DI e logging |

---

## Licença e aviso legal

Os dados oficiais são de domínio público. Respeite os termos de uso de cada portal e a LGPD ao tratar eventuais dados pessoais (os arquivos de ITBI geralmente não contêm CPF/CNPJ de pessoas físicas). O QuestPDF usa a Community License; verifique os critérios e termos oficiais da biblioteca antes de usar em organizações que não se enquadrem nela.