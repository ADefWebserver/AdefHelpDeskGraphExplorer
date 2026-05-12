using AdefHelpDeskGraphExplorer.Models;
using Microsoft.Extensions.Options;

namespace AdefHelpDeskGraphExplorer.Services.Graph;

/// <summary>Holds the on-disk location of the built graph artifacts.</summary>
public class GraphCache
{
    public GraphCache(IWebHostEnvironment env, IConfiguration cfg)
    {
        var dir = cfg.GetSection(GraphOptions.SectionName).Get<GraphOptions>()?.OutputDirectory
                  ?? "App_Data/graph";
        OutputDir = Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir);
        Directory.CreateDirectory(OutputDir);
    }

    public string OutputDir { get; }
    public string GraphJsonPath => Path.Combine(OutputDir, "graph.json");
    public bool Exists => File.Exists(GraphJsonPath);
}
