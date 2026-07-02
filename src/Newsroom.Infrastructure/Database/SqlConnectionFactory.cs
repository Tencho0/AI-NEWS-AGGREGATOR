using System.Data;
using Microsoft.Data.SqlClient;

namespace Newsroom.Infrastructure.Database;

public sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public string ConnectionString => connectionString;

    public async Task<IDbConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
