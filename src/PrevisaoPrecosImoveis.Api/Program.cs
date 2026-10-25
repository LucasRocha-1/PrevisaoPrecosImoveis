using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using PrevisaoPrecosImoveis.Api.Endpoints;
using PrevisaoPrecosImoveis.Api.Middleware;
using PrevisaoPrecosImoveis.Api.OpenApi;
using PrevisaoPrecosImoveis.Api.Services;
using Swashbuckle.AspNetCore.SwaggerUI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Previsão de Preços de Imóveis",
        Version = "v1",
        Description =
            "API para estimar o preço de transação de imóveis residenciais. " +
            "No POST /api/v1/previsoes, abra **Try it out** e escolha um exemplo pronto " +
            "(Apartamento em Pinheiros, Casa em Moema, etc.) — todos já vêm com dados válidos. " +
            "Use GET /api/v1/previsoes/exemplos para listar os mesmos corpos e GET /api/v1/modelo " +
            "para conferir se o arquivo treinado está carregado."
    });
    options.TagActionsBy(api =>
    {
        var caminho = api.RelativePath ?? string.Empty;
        if (caminho.StartsWith("health", StringComparison.OrdinalIgnoreCase))
        {
            return ["Saúde"];
        }

        if (caminho.StartsWith("api/v1/modelo", StringComparison.OrdinalIgnoreCase))
        {
            return ["Modelo"];
        }

        return ["Previsões"];
    });
    options.DocInclusionPredicate((_, _) => true);
    options.SchemaFilter<ExemplosOpenApiSchemaFilter>();
    options.OperationFilter<ExemplosOpenApiOperationFilter>();

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});
builder.Services.AddSingleton<IPredictionService, PredictionService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "Previsão de Preços de Imóveis";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Previsão de Preços de Imóveis v1");
    options.EnableTryItOutByDefault();
    options.DisplayRequestDuration();
    options.DocExpansion(DocExpansion.List);
    options.DefaultModelsExpandDepth(1);
});
app.MapGet("/", () => TypedResults.Redirect("/swagger/index.html"))
    .ExcludeFromDescription();
app.MapHealthEndpoints();
app.MapPredictionEndpoints();

app.Run();

public partial class Program;
