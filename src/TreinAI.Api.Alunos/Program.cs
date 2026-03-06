using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TreinAI.Shared.Extensions;
using TreinAI.Shared.Middleware;
using TreinAI.Shared.Models;
using TreinAI.Shared.Repositories;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(worker =>
    {
        worker.UseMiddleware<ExceptionHandlingMiddleware>();
        worker.UseMiddleware<TenantMiddleware>();
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        var cosmosEndpoint = Environment.GetEnvironmentVariable("CosmosDb__Endpoint")
            ?? "https://treinai-cosmos-dev.documents.azure.com:443/";
        var databaseName = Environment.GetEnvironmentVariable("CosmosDb__DatabaseName")
            ?? "treinai-db";

        services.AddTreinAIShared(cosmosEndpoint, databaseName);
        services.AddRepository<Aluno>("alunos");
    })
    .Build();

host.Run();
