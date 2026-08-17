using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dmart.QueryGrammar;

// Port of csdmart/DataAdapters/Sql/QueryHelper.cs — the RediSearch-flavoured
// `@field:value` parser the dmart HTTP server uses to build SQL WHERE clauses.
// The csdmart version emits positional `$N` placeholders against an
// args list. The demo SDK uses named parameters throughout DmartSqlAdapter
// (so it can coexist cleanly with PermissionFilter), so this port adapts
// the parameter style to named `@s_<n>` placeholders. Behaviour is
// otherwise identical, including:
//
//   - boolean grouping: whitespace = AND, the `or` keyword = OR,
//     parentheses for nesting; AND binds tighter than OR. (The literal
//     `and` is an optional no-op synonym for whitespace.) NOTE: as of
//     2026-06-20 whitespace between paren groups means AND, not OR — OR
//     is expressed only via `or` or value-level alternation `|`.
//   - negation (`-@field:value`),
//   - alternation (`@k:a|b`),
//   - ranges (`@k:[v1 v2]` / `@k:[v1,v2]`),
//   - comparison operators (`>` `<` `>=` `<=` `!`),
//   - wildcard tail value (`@k:abc*`),
//   - payload jsonb paths (`@payload.body.x.y:v`),
//   - payload array iteration (`@payload.body.items[].price:>100`),
//   - jsonb-array / text-array / boolean / timestamp columns,
//   - free-text plain words against shortname / payload / displayname /
//     description / tags.
//
// Extension over csdmart: `@msisdn:<v>` and `@email:<v>` resolve through the
// entries' owner_shortname → users(shortname) join, because dmart stores
// msisdn / email only on the users table and treating them as
// `<col>::text = $v` against entries would error. Anything else falls
// through to csdmart's exact behaviour, including the `<col>::text` path
// (which still errors against unknown columns — that's csdmart parity).
/// <summary>
/// Controls the placeholder style emitted by <see cref="SearchExpressionParser.Parse(string, int, PlaceholderStyle)"/>.
/// </summary>
/// <remarks>
/// Two callers, two conventions. The SDK (<c>DmartSqlAdapter.QueryAsync</c>) builds
/// commands with named placeholders so it can coexist with <c>@space</c>/<c>@subpath</c>/etc.;
/// the server's <c>QueryHelper</c> uses positional <c>$N</c> placeholders against a
/// flat parameter list. Mixing styles in one command works on some
/// Npgsql versions and breaks on others, so the parser emits whichever style the
/// caller is already using — no mixing.
/// </remarks>
public enum PlaceholderStyle
{
    /// <summary>Emit <c>@s_&lt;n&gt;</c> named placeholders; parameters carry that name.</summary>
    Named,

    /// <summary>Emit <c>$N</c> positional placeholders; parameters are nameless and bound by position.</summary>
    Positional,
}

public static class SearchExpressionParser
{
    public sealed record Parsed(IReadOnlyList<string> Clauses, IReadOnlyList<SqlParam> Parameters);

    // ── Grammar-aware safety check (kept beside the parser so the two
    // can't drift) ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="value"/> contains a character that
    /// the parser would interpret as a metachar — meaning the value cannot
    /// be safely interpolated into a synthesized search clause (e.g.
    /// <c>@field:v1|v2</c>) without changing the parse.
    /// </summary>
    /// <remarks>
    /// Owned by the parser so any future grammar change is in one file
    /// instead of two. Today's metachars: <c>|</c> (alternation),
    /// <c>:</c> (field separator), <c>*</c> (wildcard/existence),
    /// <c>(</c>/<c>)</c>/<c>[</c>/<c>]</c> (grouping), <c>"</c>/<c>'</c>
    /// (string delimiters not currently honored but plausibly added),
    /// <c>\</c> (escape), <c>@</c> (field marker), <c>&lt;</c>/<c>&gt;</c>/
    /// <c>=</c>/<c>!</c> (comparison operators), <c>{</c>/<c>}</c> (range
    /// syntax not currently honored), plus whitespace which terminates
    /// a value token.
    /// </remarks>
    public static bool IsSafeForAlternationValue(string s)
    {
        foreach (var c in s)
        {
            if (c == '|' || c == ':' || c == '*' || c == '('
                || c == ')' || c == '[' || c == ']' || c == '{'
                || c == '}' || c == '"' || c == '\'' || c == '\\'
                || c == '@' || c == '<' || c == '>' || c == '='
                || c == '!' || char.IsWhiteSpace(c))
                return false;
        }
        return true;
    }

    // ── Entry point ───────────────────────────────────────────────────────

    /// <summary>
    /// Hard ceiling on the expression length the parser will look at. Longer
    /// input is rejected before a single regex touches it.
    /// </summary>
    /// <remarks>
    /// <c>Query.Search</c> carries no bound of its own and <c>POST /public/query</c>
    /// is unauthenticated, so without this a megabyte body gets tokenized — and
    /// re-scanned per token — on every request. The ceiling is deliberately well
    /// above the ~4KB a hand-written expression ever needs: callers do not only
    /// pass user text through here. <c>QueryService.ApplyClientJoinsAsync</c>
    /// synthesizes <c>@rightField:v1|v2|…</c> narrowing terms from a whole base
    /// page (bounded by <c>MaxQueryLimit</c>, default 10000) and
    /// <c>ApplyFilterFieldsValues</c> appends the caller's permission clauses to
    /// the same string — a 4KB cap here would silently zero out legitimate joins
    /// on pages of more than ~100 records. A tighter bound on the user-supplied
    /// <c>Query.Search</c> belongs at the HTTP boundary, where wire input is
    /// still distinguishable from these synthesized expressions.
    /// </remarks>
    public const int MaxExpressionLength = 64 * 1024;

    // Emitted in place of the caller's clauses when the expression cannot be
    // parsed safely (over-length, or a pattern that blew its match timeout).
    // Fails CLOSED on purpose: ApplyFilterFieldsValues folds the caller's
    // permission filter INTO this same expression, so dropping the search and
    // returning no clauses would widen the result set past what the caller is
    // allowed to see. Carries no placeholder, so downstream $N / @s_n numbering
    // is unaffected.
    private const string NoMatchClause = "FALSE";

    /// <summary>
    /// Parses a RediSearch-style expression and returns SQL clause fragments
    /// plus the bound parameters they reference.
    /// </summary>
    /// <param name="expression">The search expression — see class docs for grammar.</param>
    /// <param name="startingParamIndex">
    /// Numeric suffix used in emitted placeholders. With
    /// <see cref="PlaceholderStyle.Named"/> the parser emits <c>@s_&lt;n&gt;</c>
    /// where <c>n</c> starts here; with <see cref="PlaceholderStyle.Positional"/>
    /// it emits <c>$N</c> where <c>N = startingParamIndex + 1</c> (Npgsql is
    /// 1-based). Caller is responsible for ensuring no name/position collision
    /// with their own parameters.
    /// </param>
    /// <param name="style">
    /// Placeholder style — see <see cref="PlaceholderStyle"/>. Defaults to
    /// <see cref="PlaceholderStyle.Named"/> for the SDK's historical behaviour.
    /// </param>
    /// <param name="targetTable">
    /// Name of the SQL table the resulting WHERE clause will be appended to,
    /// when known. Used today to gate the user-meta join: <c>@email</c> and
    /// <c>@msisdn</c> normally resolve via
    /// <c>owner_shortname IN (SELECT shortname FROM users WHERE col = $v)</c>
    /// — correct against <c>entries</c>/<c>attachments</c> but broken against
    /// <c>users</c> itself (no <c>owner_shortname</c> column there). When
    /// <paramref name="targetTable"/> is <c>"users"</c>, those fields fall
    /// through to the scalar-column path instead. Defaults to <c>null</c>
    /// for back-compat with SDK callers that don't carry table context.
    /// </param>
    public static Parsed Parse(
        string expression,
        int startingParamIndex,
        PlaceholderStyle style = PlaceholderStyle.Named,
        string? targetTable = null,
        ISqlDialect? dialect = null)
    {
        var clauses = new List<string>();
        var pars = new List<SqlParam>();
        if (string.IsNullOrWhiteSpace(expression)) return new Parsed(clauses, pars);

        // Length gate before any regex sees the text — see MaxExpressionLength.
        if (expression.Length > MaxExpressionLength)
        {
            clauses.Add(NoMatchClause);
            return new Parsed(clauses, pars);
        }

        var ctx = new ParamCtx(startingParamIndex, style, targetTable, dialect);

        try
        {
            // Phase 1: tokenize + recursive-descent parse into a boolean AST.
            // At the top level (stopAtParen: false) a stray `)` is skipped as noise
            // rather than ending the parse, so ParseOrExpr consumes the entire
            // stream — including any `or` that follows an unbalanced `)`. No
            // post-hoc recovery pass is needed (one would wrongly drop such an
            // `or`, silently turning OR into AND).
            var ts = new TokenStream(Tokenize(expression));
            var root = ParseOrExpr(ts, stopAtParen: false);

            // Phase 2: emit SQL. Parameters are bound during the walk in
            // left-to-right token order, so $N / @s_n numbering matches reading
            // order — identical to the previous inline builder for non-`or` input.
            var sql = EmitNode(root, ctx);

            pars.AddRange(ctx.Parameters);
            if (sql is not null) clauses.Add(sql);
            return new Parsed(clauses, pars);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological expression blew a pattern's RegexTimeoutMs budget.
            // Discard the partial parse — including whatever ctx bound so far,
            // which no emitted clause now references — and fail closed rather
            // than letting the exception surface as a 500.
            return new Parsed(new[] { NoMatchClause }, Array.Empty<SqlParam>());
        }
    }

    // ── Parameter bookkeeping ─────────────────────────────────────────────

    private sealed class ParamCtx
    {
        private int _next;
        private readonly PlaceholderStyle _style;
        public List<SqlParam> Parameters { get; } = new();
        public string? TargetTable { get; }

        // The backend the emitted SQL is destined for. Carried here rather than
        // passed to each Build* method: every one of them needs it, and an
        // extra parameter on ten signatures would obscure the actual arguments.
        public ISqlDialect Dialect { get; }

        public ParamCtx(int start, PlaceholderStyle style, string? targetTable = null,
                        ISqlDialect? dialect = null)
        {
            _next = start;
            _style = style;
            TargetTable = targetTable;
            Dialect = dialect ?? PostgresSqlDialect.Instance;
        }

        // Convenience: bind through the dialect's binder shape.
        public SqlBinder Binder => (value, kind) => Add(value, kind);

        // Bind a value, return the placeholder text to splice into SQL.
        // For Named: emits @s_<n> and tags the parameter with that name.
        // For Positional: emits $<n+1> (Npgsql is 1-based) and leaves the
        // parameter nameless so the provider binds by position.
        //
        // Produces a provider-neutral SqlParam; the caller's dialect
        // materializes it into a concrete DbParameter. The distinction between
        // an explicitly-typed and an inferred parameter is carried by
        // SqlValueKind.Inferred and MUST be preserved — Npgsql types an
        // untagged parameter differently from one tagged Text, which changes
        // the server-side cast without changing the SQL text.
        public string Add(object? value, SqlValueKind kind = SqlValueKind.Inferred)
        {
            string placeholder;
            string? paramName;
            if (_style == PlaceholderStyle.Named)
            {
                placeholder = "@s_" + _next.ToString(CultureInfo.InvariantCulture);
                paramName = placeholder;
            }
            else
            {
                // Positional: Npgsql $N is 1-based; _next is 0-based, so add 1.
                placeholder = "$" + (_next + 1).ToString(CultureInfo.InvariantCulture);
                paramName = null;
            }
            _next++;
            Parameters.Add(new SqlParam(paramName, value ?? DBNull.Value, kind));
            return placeholder;
        }
    }

    // ── Regex & column whitelists ─────────────────────────────────────────

    // Every pattern below runs against caller-controlled text on an
    // unauthenticated endpoint (POST /public/query), so each carries a match
    // timeout. RangeRegex is the one that actually needs it: two lazy `.+?`
    // pivoting on `[\s,]` with a `$`-anchored `]` that never arrives backtracks
    // quadratically (2k chars ≈ 0.02s, 8k ≈ 0.3s, 16k ≈ 1.2s). Parse() catches
    // the timeout and fails the expression closed — an escaping
    // RegexMatchTimeoutException would be a 500. Same 100ms budget the other
    // user-facing patterns use (Config/RegexPatternsConfig.cs,
    // Middleware/ChannelAuthMiddleware.cs). Kept as a plain int const rather
    // than a static readonly TimeSpan so static-field initialization order
    // can't hand a regex TimeSpan.Zero.
    private const int RegexTimeoutMs = 100;

    // Matches (in order): @field:[range] | @field:"quoted" | @field:value | plain_word
    private static readonly Regex SearchTokenRegex = new(
        @"-?@[^:\s]+:\[[^\]]*\]|-?@[^:\s]+:""[^""]*""|-?@[^:\s]+:[^\s]+|\S+",
        RegexOptions.Compiled, matchTimeout: TimeSpan.FromMilliseconds(RegexTimeoutMs));

    private static readonly Regex ComparisonRegex = new(@"^(>=|<=|>|<|!)(.+)$",
        RegexOptions.Compiled, matchTimeout: TimeSpan.FromMilliseconds(RegexTimeoutMs));
    private static readonly Regex NumericRegex = new(@"^-?\d+(?:\.\d+)?$",
        RegexOptions.Compiled, matchTimeout: TimeSpan.FromMilliseconds(RegexTimeoutMs));
    private static readonly Regex RangeRegex = new(@"^\[(.+?)[\s,](.+?)\]$",
        RegexOptions.Compiled, matchTimeout: TimeSpan.FromMilliseconds(RegexTimeoutMs));

    private static readonly HashSet<string> JsonbArrayColumns = new(StringComparer.Ordinal)
        { "tags", "roles", "groups" };

    private static readonly HashSet<string> TextArrayColumns = new(StringComparer.Ordinal)
        { "query_policies" };

    private static readonly HashSet<string> BooleanColumns = new(StringComparer.Ordinal)
        { "is_active", "is_open" };

    private static readonly HashSet<string> TimestampColumns = new(StringComparer.Ordinal)
        { "created_at", "updated_at", "timestamp" };

    // Fields that live on users(shortname=owner_shortname). Resolved with an
    // owner_shortname IN (SELECT shortname FROM users WHERE <col> = $v) join.
    private static readonly HashSet<string> UserMetaColumns = new(StringComparer.Ordinal)
        { "msisdn", "email" };

    private static readonly Regex SafeColumnIdent = new(
        @"^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.Compiled, matchTimeout: TimeSpan.FromMilliseconds(RegexTimeoutMs));

    private static string EscapeSqlLiteral(string s) => s.Replace("'", "''");

    // ── Parsed data structures ────────────────────────────────────────────

    private sealed class SearchField
    {
        public List<string> Values { get; set; } = new();
        public string Operation { get; set; } = "AND";   // AND | OR | RANGE
        public bool Negative { get; set; }
        public string ValueType { get; set; } = "string"; // string | numeric | boolean
        public string? ComparisonOperator { get; set; }
        public bool IsRange { get; set; }
    }

    private sealed class SearchGroup
    {
        public Dictionary<string, SearchField> Fields { get; } = new(StringComparer.Ordinal);
        public List<string> TextTerms { get; } = new();
    }

    // ── Phase 1: Parse ────────────────────────────────────────────────────

    // Single left-to-right pass that decides which '(' / ')' are group delimiters
    // and pads them with spaces so the tokenizer sees them as standalone tokens.
    //
    // Parens are exempted ONLY inside an `@field:"…"` quoted value — the one form
    // the grammar lets carry an arbitrary literal that may legitimately contain
    // parens (e.g. @displayname.en:"*Pad 8/256GB(Blue)*"). Everywhere else parens
    // stay structural, which is consistent with how the grammar already groups
    // bare text: `hello (world)` is two groups. Recognising the quoted span as it
    // actually opens (right after an `@field:` prefix, mirroring SearchTokenRegex's
    // `-?@[^:\s]+:"…"` alternative) means a stray '"' in free text — e.g. `5"
    // (@a:b)` — can't swallow the following group delimiters.
    //
    // Returns whether any structural paren was found and the space-padded string.
    // Like the tokenizer, '"' is unescaped: the value ends at the next '"'.
    private static (bool HasGroupingParens, string Normalized) ScanGroupingParens(string search)
    {
        var sb = new StringBuilder(search.Length + 16);
        bool inQuotedValue = false;
        bool hasGrouping = false;

        for (int i = 0; i < search.Length; i++)
        {
            var c = search[i];

            if (inQuotedValue)
            {
                sb.Append(c);
                if (c == '"') inQuotedValue = false;   // closing quote of the value
                continue;
            }

            if (c == '"' && OpensQuotedFieldValue(search, i))
            {
                inQuotedValue = true;
                sb.Append(c);
                continue;
            }

            if (c == '(' || c == ')')
            {
                hasGrouping = true;
                sb.Append(' ').Append(c).Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return (hasGrouping, sb.ToString());
    }

    // True when the '"' at quoteIndex opens an `@field:"…"` value, i.e. the text
    // immediately before it is a `-?@<field>:` prefix (field = one or more
    // non-colon, non-space chars). Matches SearchTokenRegex's quoted alternative.
    private static bool OpensQuotedFieldValue(string s, int quoteIndex)
    {
        var colon = quoteIndex - 1;
        if (colon < 0 || s[colon] != ':') return false;          // must be …:"

        // Walk back over the field name to the token boundary (space / start).
        var fieldEnd = colon - 1;                                 // last field char
        var j = fieldEnd;
        while (j >= 0 && s[j] != ':' && !char.IsWhiteSpace(s[j])) j--;
        var fieldStart = j + 1;

        if (s[fieldStart] == '@') return fieldEnd > fieldStart;                  // @x:"
        if (s[fieldStart] == '-' && fieldStart + 1 <= fieldEnd && s[fieldStart + 1] == '@')
            return fieldEnd > fieldStart + 1;                                    // -@x:"
        return false;
    }

    // ── Tokenize + boolean AST ─────────────────────────────────────────────
    //
    // Grammar (each level a method below):
    //
    //   orExpr  := andExpr ( OR  andExpr )*      OR  = the word "or"
    //   andExpr := factor  ( factor )*           juxtaposition / "and" = AND
    //   factor  := '(' orExpr ')' | leafRun
    //   leafRun := ( selector | textTerm | "and" )+   one contiguous run
    //
    // AND binds tighter than OR. A maximal contiguous run of leaf tokens
    // (selectors + free-text, with the no-op `and` skipped) is gathered into a
    // SINGLE SearchGroup via the existing ParseSearchString, preserving
    // same-field accumulation and last-sign-wins. `or` and `(`/`)` are the only
    // boundaries that start a new group.

    private enum TokenKind { Term, Or, And, LParen, RParen }

    // `Text` (not `Value`) deliberately: a `Token?` would expose `Nullable<>.Value`,
    // and a member also named `Value` makes `tok.Value.Kind` read ambiguously.
    private readonly record struct Token(TokenKind Kind, string Text);

    private sealed class TokenStream
    {
        private readonly IReadOnlyList<Token> _tokens;
        private int _pos;
        public TokenStream(IReadOnlyList<Token> tokens) => _tokens = tokens;
        public bool Eof => _pos >= _tokens.Count;
        public Token? Peek => _pos < _tokens.Count ? _tokens[_pos] : null;
        public void Advance() => _pos++;
    }

    private abstract class Node;

    private sealed class LeafNode : Node
    {
        public required SearchGroup Group { get; init; }
    }

    private sealed class AndNode : Node
    {
        public List<Node> Children { get; }
        public AndNode(List<Node> children) => Children = children;
    }

    private sealed class OrNode : Node
    {
        public List<Node> Children { get; }
        public OrNode(List<Node> children) => Children = children;
    }

    // ScanGroupingParens space-pads structural `(`/`)` (exempting quoted
    // `@field:"…"` values) so the regex sees them as standalone tokens. We then
    // classify. `or`/`and` are recognized ONLY as bare whitespace-delimited
    // tokens (case-insensitive) — never inside a value, because a value like
    // `@x:or` matches as a single `@…:…` token.
    private static List<Token> Tokenize(string search)
    {
        var (_, normalized) = ScanGroupingParens(search);
        var matches = SearchTokenRegex.Matches(normalized);
        var tokens = new List<Token>(matches.Count);
        foreach (Match m in matches)
        {
            var v = m.Value;
            var kind = v switch
            {
                "(" => TokenKind.LParen,
                ")" => TokenKind.RParen,
                _ when v.Equals("or", StringComparison.OrdinalIgnoreCase) => TokenKind.Or,
                _ when v.Equals("and", StringComparison.OrdinalIgnoreCase) => TokenKind.And,
                _ => TokenKind.Term,
            };
            tokens.Add(new Token(kind, v));
        }
        return tokens;
    }

    // stopAtParen: true inside a `( … )` group, where a `)` closes the group
    // and is consumed by the enclosing ParseFactor; false at the top level,
    // where an unmatched `)` is stray noise to be skipped (see ParseAndExpr).
    private static Node? ParseOrExpr(TokenStream ts, bool stopAtParen)
    {
        var parts = new List<Node>();
        var first = ParseAndExpr(ts, stopAtParen);
        if (first is not null) parts.Add(first);

        while (ts.Peek is { Kind: TokenKind.Or })
        {
            ts.Advance();                       // consume `or`
            var next = ParseAndExpr(ts, stopAtParen);
            if (next is not null) parts.Add(next); // drop empty operands (`a or or b`, `a or`)
        }

        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];
        return new OrNode(parts);
    }

    private static Node? ParseAndExpr(TokenStream ts, bool stopAtParen)
    {
        var parts = new List<Node>();
        while (true)
        {
            while (ts.Peek is { Kind: TokenKind.And }) ts.Advance(); // no-op separator
            var tok = ts.Peek;
            if (tok is null || tok.Value.Kind == TokenKind.Or) break;
            if (tok.Value.Kind == TokenKind.RParen)
            {
                if (stopAtParen) break;         // let the enclosing group consume `)`
                ts.Advance();                   // top-level stray `)` → skip as noise
                continue;
            }

            var factor = ParseFactor(ts);
            if (factor is not null) parts.Add(factor);
            // factor may be null (empty `()`) but ParseFactor always consumes at
            // least one token in that case, so the loop makes progress.
        }

        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];
        return new AndNode(parts);
    }

    private static Node? ParseFactor(TokenStream ts)
    {
        var tok = ts.Peek;
        if (tok is null) return null;

        if (tok.Value.Kind == TokenKind.LParen)
        {
            ts.Advance();                       // consume `(`
            var inner = ParseOrExpr(ts, stopAtParen: true);
            if (ts.Peek is { Kind: TokenKind.RParen }) ts.Advance(); // `)`; auto-close at EOF
            return inner;                       // null for an empty group
        }

        // A maximal run of leaf tokens → one SearchGroup. `and` inside the run
        // is the no-op separator; the run stops at `or`, `(`, `)`, or EOF.
        var fieldTokens = new List<string>();
        var textTerms = new List<string>();
        while (ts.Peek is { } cur && cur.Kind is TokenKind.Term or TokenKind.And)
        {
            ts.Advance();
            if (cur.Kind == TokenKind.And) continue;
            if (cur.Text.StartsWith('@') || cur.Text.StartsWith("-@"))
                fieldTokens.Add(cur.Text);
            else
                textTerms.Add(cur.Text);
        }

        var group = new SearchGroup();
        ParseSearchString(fieldTokens, group.Fields);
        group.TextTerms.AddRange(textTerms);
        return new LeafNode { Group = group };
    }

    // ── AST → SQL ──────────────────────────────────────────────────────────

    private static string? EmitNode(Node? node, ParamCtx ctx) => node switch
    {
        null => null,
        LeafNode leaf => EmitLeaf(leaf.Group, ctx),
        AndNode a => EmitJoin(a.Children, " AND ", ctx),
        OrNode o => EmitJoin(o.Children, " OR ", ctx),
        _ => null,
    };

    private static string? EmitJoin(List<Node> children, string op, ParamCtx ctx)
    {
        var parts = new List<string>();
        foreach (var child in children)
        {
            var sql = EmitNode(child, ctx);
            if (sql is not null) parts.Add(sql);
        }
        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];
        return "(" + string.Join(op, parts) + ")";
    }

    // One leaf = one contiguous run: fields AND'd together, then free-text
    // terms AND'd on. Byte-identical to the previous per-group emit.
    private static string? EmitLeaf(SearchGroup group, ParamCtx ctx)
    {
        var conditions = new List<string>();

        foreach (var (field, data) in group.Fields)
        {
            var clause = BuildSearchFieldSql(field, data, ctx);
            if (clause is not null) conditions.Add(clause);
        }

        foreach (var term in group.TextTerms)
        {
            var p = ctx.Add($"%{term}%");
            conditions.Add(
                BuildFreeTextTerm(p, ctx));
        }

        if (conditions.Count == 0) return null;
        return "(" + string.Join(" AND ", conditions) + ")";
    }

    private static void ParseSearchString(List<string> tokens, Dictionary<string, SearchField> result)
    {
        foreach (var token in tokens)
        {
            var raw = token;
            var negative = raw.StartsWith("-@");
            if (negative) raw = raw[1..];

            if (!raw.StartsWith('@')) continue;
            var colonIdx = raw.IndexOf(':', 1);
            if (colonIdx < 0) continue;

            var field = raw[1..colonIdx];
            var value = raw[(colonIdx + 1)..].Trim('"');

            string? compOp = null;
            var compMatch = ComparisonRegex.Match(value);
            if (compMatch.Success)
            {
                var potOp = compMatch.Groups[1].Value;
                var potVal = compMatch.Groups[2].Value;
                if (potOp == "!" || NumericRegex.IsMatch(potVal))
                {
                    compOp = potOp;
                    value = potVal;
                }
            }

            var rangeMatch = RangeRegex.Match(value);
            if (rangeMatch.Success)
            {
                var v1 = rangeMatch.Groups[1].Value.Trim();
                var v2 = rangeMatch.Groups[2].Value.Trim();
                bool allNum = NumericRegex.IsMatch(v1) && NumericRegex.IsMatch(v2);
                result[field] = new SearchField
                {
                    Values = new() { v1, v2 },
                    Operation = "RANGE",
                    Negative = negative,
                    ValueType = allNum ? "numeric" : "string",
                    IsRange = true,
                };
                continue;
            }

            var values = value.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim()).ToList();
            var operation = values.Count > 1 ? "OR" : "AND";

            var valueType = "string";
            bool allBool = values.All(v =>
                v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("false", StringComparison.OrdinalIgnoreCase));
            bool allNumeric = values.All(v => NumericRegex.IsMatch(v));
            if (allBool) valueType = "boolean";
            else if (allNumeric) valueType = "numeric";

            if (result.TryGetValue(field, out var existing))
            {
                if (existing.Negative != negative)
                {
                    result[field] = new SearchField
                    {
                        Values = values, Operation = operation, Negative = negative,
                        ValueType = valueType, ComparisonOperator = compOp,
                    };
                }
                else
                {
                    existing.Values.AddRange(values);
                    if (operation == "OR") existing.Operation = "OR";
                }
            }
            else
            {
                result[field] = new SearchField
                {
                    Values = values, Operation = operation, Negative = negative,
                    ValueType = valueType, ComparisonOperator = compOp,
                };
            }
        }
    }

    // ── Phase 2: SQL generation ───────────────────────────────────────────

    private static string? BuildSearchFieldSql(string field, SearchField data, ParamCtx ctx)
    {
        if (data.Values.Count == 0) return null;

        // Existence check: @k:* → IS NOT NULL,  -@k:* → IS NULL
        if (data.Values.Count == 1 && data.Values[0] == "*" && !data.IsRange)
        {
            var nullCheck = data.Negative ? "IS NULL" : "IS NOT NULL";
            if (field.StartsWith("payload.", StringComparison.Ordinal))
            {
                var parts = field["payload.".Length..].Split('.');
                return $"{ctx.Dialect.JsonValue("payload", parts)} {nullCheck}";
            }
            if (!SafeColumnIdent.IsMatch(field)) return null;
            if (TextArrayColumns.Contains(field))
            {
                var lengthExpr = ctx.Dialect.ArrayLength(field);
                return data.Negative ? $"{lengthExpr} = 0" : $"{lengthExpr} > 0";
            }
            return $"{field} {nullCheck}";
        }

        // Extension: user-meta fields live on `users`. Resolve via owner —
        // but only when the query is NOT itself against the users table.
        // When the caller IS querying users directly, email/msisdn are
        // real columns and the join would reference a non-existent
        // owner_shortname column.
        if (UserMetaColumns.Contains(field) && ctx.TargetTable != "users")
            return BuildUserMetaSql(field, data, ctx);

        // Payload JSONB paths
        if (field.StartsWith("payload.", StringComparison.Ordinal))
            return BuildPayloadSql(field["payload.".Length..], data, ctx);

        if (JsonbArrayColumns.Contains(field))
            return BuildJsonbArraySql(field, data, ctx);

        if (TextArrayColumns.Contains(field))
            return BuildTextArraySql(field, data, ctx);

        if (field.Contains('.'))
        {
            var dot = field.IndexOf('.');
            var col = field[..dot];
            var sub = field[(dot + 1)..];
            if (!SafeColumnIdent.IsMatch(col)) return null;
            if (sub == "*") return BuildWildcardTextSql(col, data, ctx);
            var expr = BuildJsonbPath(col, sub);
            return BuildScalarSql(expr, data, ctx);
        }

        if (BooleanColumns.Contains(field))
            return BuildBooleanColumnSql(field, data, ctx);

        if (TimestampColumns.Contains(field))
            return BuildTimestampColumnSql(field, data, ctx);

        if (!SafeColumnIdent.IsMatch(field)) return null;
        return BuildScalarSql(ctx.Dialect.AsText(field), data, ctx);
    }

    // — User-meta join (extension over csdmart) ———————————————————————————

    private static string? BuildUserMetaSql(string column, SearchField data, ParamCtx ctx)
    {
        // Don't bother with ranges/comparisons here — msisdn/email are
        // simple identifier strings. If you need richer semantics, query
        // the users table directly via a future UserRepository.
        if (data.IsRange || data.ComparisonOperator is { } op && op != "!") return null;

        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            var p = ctx.Add(value);
            var negate = data.Negative || data.ComparisonOperator == "!";
            var inner = $"SELECT shortname FROM users WHERE {column} = {p}";
            conditions.Add(negate
                ? $"owner_shortname NOT IN ({inner})"
                : $"owner_shortname IN ({inner})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Payload (JSONB) ————————————————————————————————————————————————

    private static string? BuildPayloadSql(string path, SearchField data, ParamCtx ctx)
    {
        var parts = path.Split('.');

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].EndsWith("[]", StringComparison.Ordinal))
                return BuildPayloadArraySql(parts, i, data, ctx);
        }

        if (parts.Contains("*"))
        {
            var wildcardIdx = Array.IndexOf(parts, "*");
            var baseExpr = ctx.Dialect.JsonValue("payload", parts[..wildcardIdx]);
            return BuildWildcardTextSql($"({baseExpr})", data, ctx);
        }

        // jsonExpr addresses the value AS JSON (for type tests and containment);
        // textExtract addresses it as SQL text (for comparisons). PostgreSQL
        // spells these as an arrow chain ending in -> or ->>; SQLite as one
        // path string. Both come from the dialect so neither is spelled here.
        var jsonExpr = ctx.Dialect.JsonValue("payload", parts);
        var textExtract = ctx.Dialect.JsonText("payload", parts);

        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric")
            {
                if (double.TryParse(v1, out var d1) && double.TryParse(v2, out var d2) && d1 > d2) (v1, v2) = (v2, v1);
                var p1 = ctx.Add(v1);
                var p2 = ctx.Add(v2);
                return $"({ctx.Dialect.JsonTypeIs(jsonExpr, JsonKind.Number)} AND {ctx.Dialect.AsNumber(jsonExpr)} {(data.Negative ? "NOT " : "")}BETWEEN {ctx.Dialect.NumberParam(p1)} AND {ctx.Dialect.NumberParam(p2)})";
            }
            if (string.Compare(v1, v2, StringComparison.Ordinal) > 0) (v1, v2) = (v2, v1);
            var sp1 = ctx.Add(v1);
            var sp2 = ctx.Add(v2);
            return $"({textExtract} {(data.Negative ? "NOT " : "")}BETWEEN {sp1} AND {sp2})";
        }

        return BuildPayloadValueSql(jsonExpr, textExtract, parts, data, ctx);
    }

    private static string BuildPayloadContainmentJson(string[] parts, string jsonValueLiteral)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
            sb.Append('{').Append('"').Append(EscapeJsonStringLiteral(parts[i])).Append('"').Append(':');
        sb.Append(jsonValueLiteral);
        for (int i = 0; i < parts.Length; i++) sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeJsonStringLiteral(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? BuildPayloadArraySql(string[] parts, int arrayIdx, SearchField data, ParamCtx ctx)
    {
        var prefixParts = new List<string>(arrayIdx + 1);
        for (int i = 0; i < arrayIdx; i++) prefixParts.Add(parts[i]);
        prefixParts.Add(parts[arrayIdx][..^2]);
        var arrayExpr = ctx.Dialect.JsonValue("payload", prefixParts);

        var remaining = parts.Skip(arrayIdx + 1).ToArray();
        bool hasSubPath = remaining.Length > 0;

        // The iterator and both element accessors come as a set: they have to
        // agree on the alias and on how an element is dereferenced, and the two
        // engines differ on both.
        var (iterator, elementJsonb, elementText) =
            ctx.Dialect.JsonArrayIterate(arrayExpr, remaining);

        var typeofGuard = ctx.Dialect.JsonTypeIs(arrayExpr, JsonKind.Array);

        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric")
            {
                if (double.TryParse(v1, out var d1) && double.TryParse(v2, out var d2) && d1 > d2) (v1, v2) = (v2, v1);
                var p1 = ctx.Add(v1);
                var p2 = ctx.Add(v2);
                string between = hasSubPath
                    ? $"{ctx.Dialect.JsonTypeIs(elementJsonb, JsonKind.Number)} AND {ctx.Dialect.AsNumber(elementJsonb)} BETWEEN {ctx.Dialect.NumberParam(p1)} AND {ctx.Dialect.NumberParam(p2)}"
                    : $"{ctx.Dialect.ColumnAsNumber(elementText)} BETWEEN {ctx.Dialect.NumberParam(p1)} AND {ctx.Dialect.NumberParam(p2)}";
                var exists = $"EXISTS (SELECT 1 FROM {iterator} WHERE {between})";
                // Negation: absent/null fields are intentionally included so
                // `-@items[].price:[100 200]` also matches rows that don't
                // carry the field at all. Under 3VL a bare `NOT EXISTS` over
                // a NULL array would still be NULL → row dropped; the
                // IS NULL OR jsonb null disjunct restores the expected
                // "field doesn't exist counts as out-of-range" semantics.
                return data.Negative
                    ? $"({arrayExpr} IS NULL OR {ctx.Dialect.JsonTypeIs(arrayExpr, JsonKind.Null)} OR ({typeofGuard} AND NOT {exists}))"
                    : $"({typeofGuard} AND {exists})";
            }
            if (string.Compare(v1, v2, StringComparison.Ordinal) > 0) (v1, v2) = (v2, v1);
            var sp1 = ctx.Add(v1);
            var sp2 = ctx.Add(v2);
            var between2 = $"{elementText} BETWEEN {sp1} AND {sp2}";
            var exists2 = $"EXISTS (SELECT 1 FROM {iterator} WHERE {between2})";
            return data.Negative
                ? $"({arrayExpr} IS NULL OR {ctx.Dialect.JsonTypeIs(arrayExpr, JsonKind.Null)} OR ({typeofGuard} AND NOT {exists2}))"
                : $"({typeofGuard} AND {exists2})";
        }

        // `-@arr[]:v` is negated by the NOT EXISTS wrapper below, so the
        // per-element predicate must stay POSITIVE. Emitting `!=` here as well
        // double-negates into "EVERY element equals v" — the opposite of the
        // documented "the array does not contain v" (docs/query-search.md).
        // `@arr[]:!v` (bang, no `-@`) has no wrapper negation, so it keeps the
        // `!=` predicate: "some element differs from v".
        var compOp = data.Negative && data.ComparisonOperator == "!"
            ? null
            : data.ComparisonOperator;
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            bool isNum = NumericRegex.IsMatch(value);
            string predicate;

            if (isNum && compOp is not null)
            {
                var sqlOp = compOp switch { "!" => "!=", ">" => ">", ">=" => ">=", "<" => "<", "<=" => "<=", _ => "=" };
                var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                predicate = hasSubPath
                    ? $"({ctx.Dialect.JsonTypeIs(elementJsonb, JsonKind.Number)} AND {ctx.Dialect.AsNumber(elementJsonb)} {sqlOp} {ctx.Dialect.NumberParam(pNum)})"
                    : $"{ctx.Dialect.ColumnAsNumber(elementText)} {sqlOp} {ctx.Dialect.NumberParam(pNum)}";
            }
            else if (compOp == "!")
            {
                var p = ctx.Add(value);
                predicate = $"{elementText} != {p}";
            }
            else if (isNum)
            {
                if (hasSubPath)
                {
                    var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                    var pStr = ctx.Add(value);
                    predicate = $"(({ctx.Dialect.JsonTypeIs(elementJsonb, JsonKind.Number)} AND {ctx.Dialect.AsNumber(elementJsonb)} = {ctx.Dialect.NumberParam(pNum)}) OR {elementText} = {pStr})";
                }
                else
                {
                    var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                    predicate = $"{ctx.Dialect.ColumnAsNumber(elementText)} = {ctx.Dialect.NumberParam(pNum)}";
                }
            }
            else
            {
                var p = ctx.Add(value);
                predicate = $"{elementText} = {p}";
            }

            var exists = $"EXISTS (SELECT 1 FROM {iterator} WHERE {predicate})";
            conditions.Add(data.Negative
                ? $"({arrayExpr} IS NULL OR {ctx.Dialect.JsonTypeIs(arrayExpr, JsonKind.Null)} OR ({typeofGuard} AND NOT {exists}))"
                : $"({typeofGuard} AND {exists})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    private static string? BuildPayloadValueSql(string jsonExpr, string textExtract, string[] parts, SearchField data, ParamCtx ctx)
    {
        var conditions = new List<string>();
        var compOp = data.ComparisonOperator;

        // Null check: `@path:null` matches when the field is missing OR its
        // JSON value is null. Negated form (`-@path:null`) requires the
        // field to exist with a non-null value. Only fires for a lone
        // `null` token (case-insensitive), not when null is one of several
        // alternation values or part of a literal like `nullified`.
        if (!data.IsRange && compOp is null
            && data.Values.Count == 1
            && data.Values[0].Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            var pathExpr = jsonExpr;
            return ctx.Dialect.JsonIsNullOrAbsent(pathExpr, negated: data.Negative);
        }

        if (data.ValueType == "boolean")
        {
            foreach (var v in data.Values)
            {
                var bv = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                var p = ctx.Add(bv, SqlValueKind.Boolean);
                var eq = (data.Negative || compOp == "!") ? "!=" : "=";
                conditions.Add(
                    $"(({ctx.Dialect.JsonTypeIs(jsonExpr, JsonKind.Boolean)} AND {ctx.Dialect.AsBoolean(textExtract)} {eq} {p}) OR " +
                    $"({ctx.Dialect.JsonTypeIs(jsonExpr, JsonKind.String)} AND {ctx.Dialect.AsBoolean(textExtract)} {eq} {p}))");
            }
            return JoinConditions(conditions, data.Operation, data.Negative);
        }

        foreach (var value in data.Values)
        {
            bool isNum = NumericRegex.IsMatch(value);

            // Wildcard: `*` in value → ILIKE pattern on the textually-extracted
            // value, guarded by `jsonb_typeof = 'string'`. Supports prefix
            // (`foo*`), suffix (`*foo`), and contains (`*foo*`). A lone `*`
            // value with `Values.Count == 1` is captured by the existence
            // check at line 333; a lone `*` inside an alternation (e.g.
            // `@k:vip|*`) falls through to JSONB containment as the literal
            // string "*", matching the existing alternation contract.
            if (compOp is null && value.Length > 1 && value.Contains('*'))
            {
                // Escape PG's ILIKE metachars (\ % _) BEFORE swapping `*` → `%`,
                // so user-supplied literal `%`/`_`/`\` don't act as wildcards
                // or escape sequences. ILIKE shares LIKE's metachar set; the
                // default escape is `\`, so `\\` / `\%` / `\_` round-trip
                // cleanly without needing an explicit ESCAPE clause.
                //
                // Performance: ILIKE on an extracted JSONB text value can't
                // use the existing `payload jsonb_path_ops` GIN — that
                // opclass only accelerates `@>` containment. To rescue
                // wildcards on large tables we emit a `payload::text ILIKE
                // @pattern` prefilter AND'd onto the precise per-path
                // check. SchemaInitializer maintains a `pg_trgm` GIN over
                // `(payload::text)` (see SqlSchema.ConcurrentIndexes); the
                // prefilter clause uses that index for sub-second lookups
                // on 75M-row tables instead of seq-scanning ~750 GB.
                //
                // The trigram index is "approximate" — a hit on `*foo*`
                // can match the literal substring "foo" anywhere in the
                // serialized JSON, including key names and unrelated
                // values. The precise per-path ILIKE that AND's onto the
                // prefilter prunes those false positives so the final row
                // set is exact. If the trigram index isn't present yet
                // (fresh install before index build, or operator on an
                // older PG without pg_trgm), the prefilter is just an
                // additional ILIKE — the result set is still correct,
                // just slow. So zero-risk to emit unconditionally.
                //
                // Patterns shorter than 3 chars (`*ab*`, `*x*`) can't be
                // serviced by a trigram index — PG's planner falls back
                // to seq scan automatically in that case.
                // Per-path pattern: preserves the user's wildcard position
                // (prefix `foo*` → `foo%`, suffix `*foo` → `%foo`,
                // contains `*foo*` → `%foo%`).
                var perPathPattern = value
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_")
                    .Replace('*', '%');
                var pPath = ctx.Add(perPathPattern);

                var pathExpr = jsonExpr;
                if (data.Negative)
                {
                    // A direct `NOT (typeof='string' AND ILIKE pattern)` is
                    // wrong: when the field is missing, `jsonb_typeof(NULL)`
                    // and `textExtract` both yield SQL NULL, the inner AND
                    // becomes NULL, and `NOT NULL` is NULL — which WHERE
                    // drops. Spell out the three disjoint passing cases so
                    // missing / non-string / non-matching all evaluate to
                    // TRUE under three-valued logic.
                    //
                    // The trigram index doesn't help the negated form —
                    // "exclude rows containing X" requires visiting every
                    // row anyway, since the index tells us which rows DO
                    // contain X (the opposite of what we want). Keep the
                    // precise per-path negated check; planner will pick
                    // the most selective path.
                    conditions.Add(
                        BuildNegatedWildcard(pathExpr, textExtract, pPath, ctx));
                }
                else
                {
                    // Trigram prefilter: contains-form regardless of the
                    // per-path wildcard position. Without this, a prefix
                    // pattern (`foo*`) would prefilter for "payload text
                    // starts with foo", which never matches because the
                    // serialized payload starts with `{`. Strip user `*`
                    // markers, escape metachars, convert remaining mid-
                    // pattern `*` to `%`, wrap in `%...%`.
                    var core = value.Trim('*');
                    var corePattern = "%" + core
                        .Replace("\\", "\\\\")
                        .Replace("%", "\\%")
                        .Replace("_", "\\_")
                        .Replace('*', '%') + "%";
                    var pPre = ctx.Add(corePattern);
                    conditions.Add(
                        BuildPositiveWildcard(pathExpr, textExtract, pPre, pPath, core, ctx));
                }
                continue;
            }

            if (isNum && compOp is not null)
            {
                var sqlOp = compOp switch { "!" => "!=", ">" => ">", ">=" => ">=", "<" => "<", "<=" => "<=", _ => "=" };
                var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                conditions.Add(
                    $"({ctx.Dialect.JsonTypeIs(jsonExpr, JsonKind.Number)} AND {ctx.Dialect.AsNumber(textExtract)} {sqlOp} {ctx.Dialect.NumberParam(pNum)})");
            }
            else if (data.Negative || compOp == "!")
            {
                // Negation: field absent, NOT in array, or != as string/number.
                // Absent/null fields are intentionally included — under 3VL,
                // a bare `NOT (typeof='string' AND ...)` would drop missing
                // rows (the inner AND is NULL → NOT NULL is NULL → WHERE
                // skips). The absent-cond disjunct restores the expected
                // "field doesn't exist counts as not equal" semantics.
                var pVal = ctx.Add(value);
                var pJsonArr = ctx.Add(ToJsonArray(value), SqlValueKind.Json);
                var absentCond = $"({jsonExpr} IS NULL OR {ctx.Dialect.JsonTypeIs(jsonExpr, JsonKind.Null)})";
                // CAST(... AS jsonb) is redundant alongside the typed param
                // but kept verbatim from the server's pre-extraction emit
                // so logged SQL stays byte-identical (see BuildJsonbArraySql).
                var arrayCond = $"({ctx.Dialect.JsonTypeIs(jsonExpr, JsonKind.Array)} AND NOT ({ctx.Dialect.JsonContains(jsonExpr, ctx.Dialect.JsonParam(pJsonArr))}))";
                var stringCond = $"({ctx.Dialect.JsonTypeIs(jsonExpr, JsonKind.String)} AND {textExtract} != {pVal})";
                if (isNum)
                {
                    var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                    var numCond = $"({ctx.Dialect.JsonTypeIs(jsonExpr, JsonKind.Number)} AND {ctx.Dialect.AsNumber(textExtract)} != {ctx.Dialect.NumberParam(pNum)})";
                    conditions.Add($"({absentCond} OR {arrayCond} OR {stringCond} OR {numCond})");
                }
                else
                {
                    conditions.Add($"({absentCond} OR {arrayCond} OR {stringCond})");
                }
            }
            else
            {
                var pContainStr = ctx.Add(BuildPayloadContainmentJson(parts, ToJsonString(value)), SqlValueKind.Json);
                var containStringCond = $"({ctx.Dialect.JsonContains(ctx.Dialect.JsonValue("payload", Array.Empty<string>()), pContainStr)})";
                var pContainArr = ctx.Add(BuildPayloadContainmentJson(parts, ToJsonArray(value)), SqlValueKind.Json);
                var containArrayCond = $"({ctx.Dialect.JsonContains(ctx.Dialect.JsonValue("payload", Array.Empty<string>()), pContainArr)})";

                if (isNum)
                {
                    var pNum = ctx.Add(double.Parse(value, CultureInfo.InvariantCulture));
                    var numCond = $"({ctx.Dialect.JsonTypeIs(jsonExpr, JsonKind.Number)} AND {ctx.Dialect.AsNumber(textExtract)} = {ctx.Dialect.NumberParam(pNum)})";
                    conditions.Add($"({containStringCond} OR {containArrayCond} OR {numCond})");
                }
                else
                {
                    conditions.Add($"({containStringCond} OR {containArrayCond})");
                }
            }
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // A bare word matches across the columns a human would expect to search:
    // the shortname plus the textual rendering of the JSON-ish columns.
    private static string BuildFreeTextTerm(string p, ParamCtx ctx)
    {
        var d = ctx.Dialect;
        var tests = new[]
        {
            d.ILike("shortname", p, negated: false),
            d.ILike(d.AsText("payload"), p, negated: false),
            d.ILike(d.AsText("displayname"), p, negated: false),
            d.ILike(d.AsText("description"), p, negated: false),
            d.ILike(d.AsText("tags"), p, negated: false),
        };
        return "(" + string.Join(" OR ", tests) + ")";
    }

    // Negated wildcard. A direct NOT(...) is wrong under three-valued logic:
    // when the field is missing both the type test and the extract are NULL, so
    // the AND is NULL and NOT NULL is NULL, which WHERE drops. Spelling out the
    // three passing cases keeps missing / non-string / non-matching all TRUE.
    private static string BuildNegatedWildcard(
        string pathExpr, string textExtract, string pPath, ParamCtx ctx)
    {
        var notString = ctx.Dialect.JsonTypeIsNot(pathExpr, JsonKind.String);
        var notLike = ctx.Dialect.ILike(textExtract, pPath, negated: true);
        return $"({pathExpr} IS NULL OR {notString} OR {notLike})";
    }

    // Positive wildcard: a cheap whole-document prefilter AND the precise
    // per-path check. On PostgreSQL the prefilter is what the pg_trgm GIN index
    // serves; SQLite has no such index, so it is simply a second comparison and
    // the result set is identical, just slower.
    private static string BuildPositiveWildcard(
        string pathExpr, string textExtract, string pPre, string pPath,
        string coreLiteral, ParamCtx ctx)
    {
        var prefilter = ctx.Dialect.WildcardPrefilter("payload", pPre, ctx.TargetTable, coreLiteral);
        var isString = ctx.Dialect.JsonTypeIs(pathExpr, JsonKind.String);
        var precise = ctx.Dialect.ILike(textExtract, pPath, negated: false);
        // A dialect that cannot serve the prefilter for this pattern omits it;
        // the precise check alone is exact, just unindexed.
        return prefilter is null
            ? $"({isString} AND {precise})"
            : $"({prefilter} AND {isString} AND {precise})";
    }

    // Substring match against a JSON object rendered as text — the fallback for
    // a column holding an object where an array was expected.
    private static string BuildObjectContains(
        string column, string pVal, bool negated, ParamCtx ctx)
    {
        var isObject = ctx.Dialect.JsonTypeIs(column, JsonKind.Object);
        var like = ctx.Dialect.ILike(ctx.Dialect.AsText(column), $"'%' || {pVal} || '%'", negated: false);
        return negated ? $"({isObject} AND NOT ({like})" : $"({isObject} AND {like}";
    }

    // — JSONB array columns ————————————————————————————————————————————————

    private static string? BuildJsonbArraySql(string column, SearchField data, ParamCtx ctx)
    {
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            var pVal = ctx.Add(value);
            var pJson = ctx.Add(ToJsonArray(value), SqlValueKind.Json);
            // CAST(... AS jsonb) is functionally redundant — the param is
            // already typed Jsonb — but kept verbatim from the server's
            // pre-extraction emit so logged SQL stays byte-identical and
            // tests that inspect emitted text (e.g. Spec_Roles_Array_Search)
            // continue to assert against a stable shape.
            if (data.Negative)
            {
                conditions.Add(
                    $"(({ctx.Dialect.JsonTypeIs(column, JsonKind.Array)} AND NOT ({ctx.Dialect.JsonContains(column, ctx.Dialect.JsonParam(pJson))})) OR " +
                    BuildObjectContains(column, pVal, negated: true, ctx) + "))");
            }
            else
            {
                conditions.Add(
                    $"(({ctx.Dialect.JsonTypeIs(column, JsonKind.Array)} AND {ctx.Dialect.JsonContains(column, ctx.Dialect.JsonParam(pJson))}) OR " +
                    BuildObjectContains(column, pVal, negated: false, ctx) + "))");
            }
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Text-array columns ————————————————————————————————————————————————

    private static string? BuildTextArraySql(string column, SearchField data, ParamCtx ctx)
    {
        var negative = data.Negative || data.ComparisonOperator == "!";
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            string predicate;
            if (value.Contains('*'))
            {
                var p = ctx.Add(value.Replace('*', '%'));
                predicate = ctx.Dialect.ILike("elem", p, negated: false);
            }
            else
            {
                var p = ctx.Add(value);
                predicate = $"elem = {p}";
            }
            var exists = $"EXISTS (SELECT 1 FROM {ctx.Dialect.ArrayElements(column, "elem")} WHERE {predicate})";
            conditions.Add(negative ? $"NOT {exists}" : exists);
        }
        return JoinConditions(conditions, data.Operation, negative);
    }

    // — Wildcard text search on a JSONB subtree ————————————————————————————

    private static string? BuildWildcardTextSql(string baseExpr, SearchField data, ParamCtx ctx)
    {
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            var p = ctx.Add($"%{value}%");
            conditions.Add(data.Negative
                ? "(" + ctx.Dialect.ILike(ctx.Dialect.AsText(baseExpr), p, negated: true) + ")"
                : "(" + ctx.Dialect.ILike(ctx.Dialect.AsText(baseExpr), p, negated: false) + ")");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Boolean column ————————————————————————————————————————————————

    private static string? BuildBooleanColumnSql(string column, SearchField data, ParamCtx ctx)
    {
        var conditions = new List<string>();
        foreach (var value in data.Values)
        {
            var bv = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            var p = ctx.Add(bv, SqlValueKind.Boolean);
            var eq = (data.Negative || data.ComparisonOperator == "!") ? "!=" : "=";
            conditions.Add($"({ctx.Dialect.ColumnAsBoolean(column)} {eq} {p})");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Timestamp column ——————————————————————————————————————————————

    private static string? BuildTimestampColumnSql(string column, SearchField data, ParamCtx ctx)
    {
        string ParamExpr(string v)
        {
            var p = ctx.Add(v);
            return ctx.Dialect.TimestampFrom(p, epochMillis: NumericRegex.IsMatch(v));
        }

        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric"
                && double.TryParse(v1, NumberStyles.Float, CultureInfo.InvariantCulture, out var d1)
                && double.TryParse(v2, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)
                && d1 > d2)
            {
                (v1, v2) = (v2, v1);
            }
            else if (data.ValueType != "numeric" && string.Compare(v1, v2, StringComparison.Ordinal) > 0)
            {
                (v1, v2) = (v2, v1);
            }
            var p1 = ParamExpr(v1);
            var p2 = ParamExpr(v2);
            return $"({column} {(data.Negative ? "NOT " : "")}BETWEEN {p1} AND {p2})";
        }

        var conditions = new List<string>();
        var compOp = data.ComparisonOperator;
        foreach (var value in data.Values)
        {
            var pExpr = ParamExpr(value);
            if (compOp is not null && compOp != "!")
                conditions.Add(data.Negative ? $"NOT ({column} {compOp} {pExpr})" : $"{column} {compOp} {pExpr}");
            else if (data.Negative || compOp == "!")
                conditions.Add($"{column} != {pExpr}");
            else
                conditions.Add($"{column} = {pExpr}");
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — Scalar text/numeric column ————————————————————————————————————————

    private static string? BuildScalarSql(string fieldExpr, SearchField data, ParamCtx ctx)
    {
        var compOp = data.ComparisonOperator;
        var conditions = new List<string>();

        if (data.IsRange && data.Values.Count == 2)
        {
            var v1 = data.Values[0];
            var v2 = data.Values[1];
            if (data.ValueType == "numeric")
            {
                if (double.TryParse(v1, out var d1) && double.TryParse(v2, out var d2) && d1 > d2) (v1, v2) = (v2, v1);
                var p1 = ctx.Add(v1);
                var p2 = ctx.Add(v2);
                return $"({ctx.Dialect.ColumnAsNumber(fieldExpr)} {(data.Negative ? "NOT " : "")}BETWEEN {ctx.Dialect.NumberParam(p1)} AND {ctx.Dialect.NumberParam(p2)})";
            }
            if (string.Compare(v1, v2, StringComparison.Ordinal) > 0) (v1, v2) = (v2, v1);
            var sp1 = ctx.Add(v1);
            var sp2 = ctx.Add(v2);
            return $"({fieldExpr} {(data.Negative ? "NOT " : "")}BETWEEN {sp1} AND {sp2})";
        }

        foreach (var value in data.Values)
        {
            if (compOp is not null && compOp != "!")
            {
                var p = ctx.Add(value);
                var cast = "::numeric";
                conditions.Add(data.Negative
                    ? $"NOT ({fieldExpr}{cast} {compOp} {p}{cast})"
                    : $"{fieldExpr}{cast} {compOp} {p}{cast}");
            }
            else if (data.Negative || compOp == "!")
            {
                var p = ctx.Add(value);
                conditions.Add($"{fieldExpr} != {p}");
            }
            else
            {
                if (value.Contains('*'))
                {
                    var p = ctx.Add(value.Replace('*', '%'));
                    conditions.Add(ctx.Dialect.ILike(fieldExpr, p, negated: false));
                }
                else
                {
                    var p = ctx.Add(value);
                    conditions.Add($"{fieldExpr} = {p}");
                }
            }
        }
        return JoinConditions(conditions, data.Operation, data.Negative);
    }

    // — JSON literal helpers ——————————————————————————————————————————————

    private static string ToJsonArray(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"[\"{escaped}\"]";
    }

    private static string ToJsonString(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    // — Join helpers ——————————————————————————————————————————————————

    private static string? JoinConditions(List<string> conditions, string operation, bool negative)
    {
        if (conditions.Count == 0) return null;
        if (conditions.Count == 1) return conditions[0];

        string joinOp;
        if (negative) joinOp = operation == "AND" ? " OR " : " AND ";
        else joinOp = operation == "AND" ? " AND " : " OR ";

        return "(" + string.Join(joinOp, conditions) + ")";
    }

    private static string BuildJsonbPath(string column, string dotPath)
    {
        var segments = dotPath.Split('.');
        if (segments.Length == 0) return $"{column}::text";
        if (segments.Length == 1) return $"{column}::jsonb->>'{EscapeSqlLiteral(segments[0])}'";

        var sb = new StringBuilder($"{column}::jsonb");
        for (var i = 0; i < segments.Length - 1; i++)
            sb.Append($"->'{EscapeSqlLiteral(segments[i])}'");
        sb.Append($"->>'{EscapeSqlLiteral(segments[^1])}'");
        return sb.ToString();
    }
}
