using AdefHelpDeskGraphExplorer.Components;
using AdefHelpDeskGraphExplorer.Data;
using AdefHelpDeskGraphExplorer.Models;
using AdefHelpDeskGraphExplorer.Services.AI;
using AdefHelpDeskGraphExplorer.Services.AI.GraphTools;
using AdefHelpDeskGraphExplorer.Services.Graph;
using AdefHelpDeskGraphExplorer.Services.HelpDesk;
using Microsoft.EntityFrameworkCore;
using Radzen;

namespace AdefHelpDeskGraphExplorer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Writable overlay for runtime-edited settings (Settings page persists here).
            builder.Configuration.AddJsonFile("appsettings.User.json", optional: true, reloadOnChange: true);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddRadzenComponents();

            // Options
            builder.Services.AddOptions<AIOptions>()
                .Bind(builder.Configuration.GetSection(AIOptions.SectionName));
            builder.Services.AddOptions<GraphOptions>()
                .Bind(builder.Configuration.GetSection(GraphOptions.SectionName));

            // AI services
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<AIConfigurationService>();
            builder.Services.AddSingleton<ChatClientFactory>();
            builder.Services.AddHttpClient<AIModelService>();
            builder.Services.AddScoped<ChatService>();

            // Help-desk data access
            builder.Services.AddDbContext<HelpDeskDbContext>((sp, o) =>
            {
                var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("ADefHelpDesk");
                if (!string.IsNullOrWhiteSpace(cs))
                    o.UseSqlServer(cs).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            });
            builder.Services.AddScoped<HelpDeskRepository>();
            builder.Services.AddSingleton<ConnectionTester>();

            // Graph
            builder.Services.AddSingleton<GraphCache>();
            builder.Services.AddSingleton<GraphQueryService>();
            builder.Services.AddScoped<IHelpDeskGraphBuilder, HelpDeskGraphBuilder>();
            builder.Services.AddScoped<IGraphChatTools, GraphChatTools>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseHttpsRedirection();

            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            // ---- API endpoints ----
            app.MapGet("/api/graph.json", (GraphCache cache) =>
                cache.Exists
                    ? Results.File(cache.GraphJsonPath, "application/json")
                    : Results.NotFound(new { error = "graph.json has not been built yet. POST /api/graph/build." }));

            app.MapPost("/api/graph/build", async (
                IHelpDeskGraphBuilder b,
                GraphCache cache,
                CancellationToken ct) =>
            {
                var doc = await b.BuildAsync(progress: null, ct);
                await b.SaveAsync(doc, cache.OutputDir, ct);
                return Results.Ok(new
                {
                    nodeCount = doc.Nodes.Count,
                    edgeCount = doc.Edges.Count,
                    generatedUtc = doc.GeneratedUtc
                });
            });

            app.Run();
        }
    }
}
