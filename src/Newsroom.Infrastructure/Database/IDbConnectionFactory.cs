using System.Data;

namespace Newsroom.Infrastructure.Database;

public interface IDbConnectionFactory
{
    /// <summary>Opens a connection to the Newsroom database.</summary>
    Task<IDbConnection> OpenAsync(CancellationToken ct = default);
}
