using AdefHelpDeskGraphExplorer.Models;
using AdefHelpDeskGraphExplorer.Models.Graph;
using Microsoft.Extensions.Options;

namespace AdefHelpDeskGraphExplorer.Services.Graph;

public interface IHelpDeskGraphBuilder
{
    Task<GraphDocument> BuildAsync(IProgress<int>? progress, CancellationToken ct);
    Task SaveAsync(GraphDocument doc, string outputDir, CancellationToken ct);
}
