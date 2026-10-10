namespace Dmart.QueryGrammar;

/// <summary>
/// A search expression that names something the parser cannot turn into a
/// condition: a field that cannot be a column, a comparison a field does not
/// support, a timestamp that is not a date.
/// </summary>
/// <remarks>
/// An exception rather than a dropped clause. Dropping it fails OPEN: the
/// condition silently disappears and the query returns more than was asked
/// for, which also defeats a permission filter folded into the same
/// expression. The server answers it with a 400 that carries the message.
/// </remarks>
public sealed class InvalidSearchException(string message) : Exception(message);
