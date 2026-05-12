using Microsoft.Data.SqlClient;

namespace AdefHelpDeskGraphExplorer.Services.HelpDesk;

public class ConnectionTester
{
    public async Task<(bool ok, string message)> TestAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 TaskID FROM ADefHelpDesk_Tasks";
            var result = await cmd.ExecuteScalarAsync(ct);
            return (true, $"Connected. Sample TaskID: {result ?? "(none)"}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
