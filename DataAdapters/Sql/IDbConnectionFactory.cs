using System.Data.Common;

namespace Dmart.DataAdapters.Sql;

// Opens connections to whichever SQL backend DATABASE_DRIVER selected.
//
// Deliberately NOT DbProviderFactories: that resolves providers by assembly
// name through reflection, which Native AOT cannot see through. Implementations
// are registered by an explicit switch in Program.cs, so every provider type is
// statically rooted.
//
// Returns DbConnection rather than a provider type on purpose — it is the seam.
// Callers that genuinely need PostgreSQL-specific behaviour (binary COPY, the
// import session's SQLSTATE-driven reconnect logic) keep taking Db directly
// instead of widening this interface to accommodate them.
public interface IDbConnectionFactory
{
    /// <summary>
    /// False when the backend has no usable configuration, letting the host
    /// boot for smoke checks and tests instead of failing DI resolution.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Opens a new connection. The caller owns and disposes it.</summary>
    Task<DbConnection> OpenAsync(CancellationToken ct = default);
}
