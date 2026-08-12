namespace Dmart.QueryGrammar;

/// <summary>
/// A reducer dmart knows about, that the configured backend cannot compute.
/// </summary>
/// <remarks>
/// An exception rather than a null return because the two failure modes must
/// not converge. An UNRECOGNIZED reducer name has always been skipped — the
/// SELECT item is simply not emitted — and that behaviour predates the seam
/// and is part of the PostgreSQL contract. A reducer the caller spelled
/// correctly, that this backend genuinely cannot express, must not take that
/// path: the response would come back missing a column the client asked for,
/// with a 200 and nothing to explain it. That is the exact shape of silent
/// degradation the SQLite tier is not allowed to have.
/// </remarks>
public sealed class UnsupportedReducerException(string reducerName, string dialectName)
    : Exception($"aggregation reducer '{reducerName}' is not supported on the "
                + $"{dialectName} backend")
{
    public string ReducerName { get; } = reducerName;
    public string DialectName { get; } = dialectName;
}
