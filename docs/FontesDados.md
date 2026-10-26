# Fontes de Dados Públicos – Mercado Imobiliário Brasil

## 1. ITBI – Transações Reais (melhor label para regressão)

### São Paulo (capital)
- **Portal**: Secretaria Municipal da Fazenda  
  https://prefeitura.sp.gov.br/web/fazenda/w/acesso_a_informacao/31501
- **Conteúdo**: Declarações de Transações Imobiliárias (DTI) com recolhimento de ITBI.
- **Formato**: Excel (.xlsx) e ODS por ano (2018–atual). O coletor baixa automaticamente os links XLSX da página oficial e ignora arquivos que já existem em `data/raw/itbi_sp/`; ODS ainda não é processado.
- **Campos úteis**: data do pagamento, logradouro, bairro, área, valor da transação, natureza.
- **Observação**: valores são os *declarados* pelo contribuinte (próximos do preço de mercado).

### Belo Horizonte
- Portal de Dados Abertos PBH: https://dados.pbh.gov.br/dataset/itbi-relatorios
- Relatórios mensais em CSV.

### Outras capitais
Verificar o portal de transparência / dados abertos de cada prefeitura (Curitiba, Rio, Recife, etc.).

### Curitiba (importação CSV)
- **Portal municipal de dados abertos**: https://dadosabertos.curitiba.pr.gov.br/conjuntodado
- O coletor `ColetorItbiCuritiba` baixa um CSV quando `CuritibaItbiUrl` está configurada e também lê os CSVs locais em `data/raw/itbi_curitiba/`. O endereço configurado precisa ser um link HTTPS direto para um arquivo CSV no esquema normalizado abaixo; páginas de catálogo HTML não são fontes de registros.
- O catálogo público consultado não permitiu confirmar um arquivo aberto de transações individuais de ITBI nem um esquema disponível para download automático. Antes de usar dados, confirme com a Prefeitura que a fonte contém transações/valores declarados de venda e que a reutilização é permitida. Guias de recolhimento, base de cálculo e valor venal não devem ser tratados como preço de venda.
- Configure o endereço do CSV pela variável de ambiente `CuritibaItbiUrl` ou pela configuração equivalente do .NET. O arquivo baixado é salvo como `data/raw/itbi_curitiba/curitiba_itbi_download.csv`, validado antes da substituição do arquivo anterior e limitado a 250 MB. Uma resposta inválida ou indisponibilidade da URL interrompe a coleta com erro explícito; não é tratada como treino bem-sucedido sem download.
- O CSV precisa conter cabeçalhos de data da transação, valor da transação e área construída. O coletor aceita os nomes canônicos do exemplo abaixo e variações comuns sem acentos ou separadores (por exemplo `Data da Transação`, `Valor Declarado`, `Área Privativa`). Campos não fornecidos pela fonte ficam sem preenchimento e são normalizados para zero ou “Não informado”.

Exemplo de esquema canônico (UTF-8; delimitador vírgula ou ponto e vírgula; datas `dd/MM/yyyy`; números em formato brasileiro ou invariável):

```csv
data_transacao;valor_transacao;area_construida_m2;area_terreno_m2;valor_venal;dormitorios;banheiros;vagas_garagem;ano_construcao;tipo_imovel;padrao_construtivo;bairro;logradouro;cep
15/01/2025;580000,00;78,5;0;0;2;2;1;2012;Apartamento;Medio;Centro;Rua Exemplo;80010000
```

Linhas sem data, valor positivo ou área construída positiva são registradas como ignoradas. A cidade é marcada como `Curitiba`; a área de terreno, dormitórios, banheiros, vagas, ano, tipo, padrão, bairro, logradouro e CEP são carregados quando presentes. Após a importação, o pipeline combina essas linhas com ITBI-SP, aplica a mesma preparação temporal e treina novamente o modelo. As métricas agregadas não substituem a avaliação separada por cidade; Curitiba precisa de volume e cobertura temporal suficientes antes de se interpretar suas previsões.

---

## 2. IPTU – Valor Venal e Cadastro

- **São Paulo – GeoSampa**: cadastro fiscal completo (valor venal, área terreno/construção, padrão, ano).
  - Download via camada “LOTE” → cadastro IPTU.
  - Também disponível no Base dos Dados: https://basedosdados.org/dataset/05f1b96d-883b-4202-a4bd-40379c5d326a
- Útil como **feature** (valor venal correlaciona com preço de mercado, mas não é o label).

---

## 3. Banco Central do Brasil

### SGS (Sistema Gerenciador de Séries Temporais)
Endpoint padrão:
```
https://api.bcb.gov.br/dados/serie/bcdata.sgs.{CODIGO}/dados?formato=json&dataInicial=DD/MM/AAAA&dataFinal=DD/MM/AAAA
```

Códigos mais relevantes:
| Código | Série |
|--------|-------|
| 433    | IPCA mensal |
| 13522  | IPCA acumulado 12 meses |
| 189    | IGP-M |
| 432    | Meta Selic |
| 1178   | Selic efetiva |
| 226    | TR |

### Mercado Imobiliário (Olinda)
```
https://olinda.bcb.gov.br/olinda/servico/MercadoImobiliario/versao/v1/odata/mercadoimobiliario?$format=text/csv
```
Séries de crédito imobiliário, estoque, concessões, etc.

---

## 4. FipeZap

- Site: https://www.fipe.org.br/pt-br/indices/fipezap/
- Planilhas Excel com séries históricas de preços pedidos (venda e locação) por cidade.
- Abrangência: 56 cidades (venda residencial).
- Uso recomendado: **feature de tendência** (índice normalizado por cidade/mês).

---

## 5. Outras fontes oficiais úteis

- **IBGE**: IPCA detalhado, Censo, PNAD (renda, demografia).
- **SINAPI (Caixa/IBGE)**: custos de construção.
- **Ministério das Cidades / FGTS**: programas habitacionais (MCMV).
- **dados.gov.br**: catálogo geral de datasets abertos.
- **OpenStreetMap (OSM)**: estações de metrô, escolas e parques via Overpass API; dados © OpenStreetMap contributors, sob ODbL.

### Coordenadas e features espaciais

O ITBI-SP não fornece latitude/longitude nos campos usados pelo coletor. Para associar imóveis a coordenadas sem enviar endereços transacionais para um geocodificador externo, o pipeline aceita `data/raw/geolocalizacao/cep_coordenadas.csv` com as colunas `Cep,Latitude,Longitude`. A fonte das coordenadas deve ser pública e documentada; coordenadas de CEP são centroides aproximados, não a localização exata do imóvel.

Com coordenadas disponíveis, o pipeline busca no Overpass os POIs dentro da área dos imóveis, persiste a resposta em `data/raw/openstreetmap/pontos_interesse.json` e calcula distâncias geodésicas ao metrô, à escola e ao parque mais próximos. Para não atribuir retroativamente a uma venda POIs que só abriram depois, só entram no cálculo registros com `start_date` ou `opening_date` conhecido e anterior à transação; os sem data conhecida são descartados. O cache evita consultas repetidas. A API pública pode impor limites; não faça consultas em alta frequência. Sem coordenadas ou POI temporalmente elegível, as features recebem o marcador de ausência `1000 km`; `PossuiCoordenadas` indica quando a localização do imóvel foi encontrada.

### Validação temporal

O treino ordena transações pela data, usa os registros mais recentes como holdout e realiza três folds walk-forward expansivos somente no período anterior. Cada fold ajusta seu próprio pipeline FastTree de referência; as métricas aparecem em `visualizacoes/validacao_cruzada_temporal.csv`. A seleção automática do regressor usa treino e validação separados cronologicamente dentro do período anterior ao holdout, e o algoritmo vencedor é reajustado em todo esse período antes de ser avaliado no holdout. Assim, nem o AutoML nem o modelo final acessam as observações do holdout durante o ajuste. Registros com a mesma data não são separados entre treino e validação/holdout. Se o AutoML esgotar o tempo de execução sem escolher um algoritmo, o pipeline registra o motivo e usa FastTree como fallback. Os indicadores mensais do BCB são associados com defasagem de dois meses para reduzir vazamento por publicação posterior ao período de referência.

## API de predições

O projeto `src/PrevisaoPrecosImoveis.Api` serve o modelo persistido em `data/models/modelo_preco_imovel.zip`. Execute o treinamento pelo console antes de iniciar a API. O caminho pode ser sobrescrito por `ModelPath` em `appsettings.json` ou por variável de ambiente. A API publica `GET /health`, `GET /health/ready` e `POST /api/v1/previsoes`; o Swagger está em `/swagger`. Se o modelo não existir, a aplicação informa readiness 503 e não produz estimativas. As distâncias ao metrô, escola e parque no request são informadas em quilômetros e devem usar as mesmas convenções das features do treino.

## Relatório PDF de execução

Ao concluir o treino, o pipeline gera `visualizacoes/relatorio_modelo.pdf` com contagens desta execução, período e localidades do conjunto usado, nomes dos XLSX lidos, links das fontes, descrição dos métodos de coleta, tabela de linhagem (dado → fonte → uso), gráficos de importância e resíduos e métricas do holdout/folds temporais. O relatório deixa explícito quando dados sintéticos foram usados e que o FipeZap atualmente é carregado, mas não associado às transações ITBI.

A origem do arquivo de coordenadas de CEP não é conhecida pelo código; o PDF marca essa limitação e pede que a fonte seja registrada ao preparar o arquivo. O documento também atribui os dados OSM a seus colaboradores e inclui os limites da interpretação dos gráficos.

---

## Ordem sugerida de implementação

1. BCB SGS (já funcional no esqueleto – API aberta).
2. ITBI-SP (maior volume de transações reais).
3. FipeZap (tendência de mercado).
4. IPTU/GeoSampa (features de cadastro).
5. Expandir para outras capitais.
