using Dmart.Auth;

namespace Dmart.DataAdapters.Sql;

// OTP store over the `otps` table — one row per issued code; rows persist as
// request history after consumption.
//
// Invariants:
//   * At most one redeemable code per (identifier, purpose): IssueAsync marks
//     any prior live row `superseded`, and verification reads only the
//     latest non-consumed row.
//   * Consumption is a guarded UPDATE (`... WHERE id = ? AND consumed_at IS
//     NULL`) — atomic, no transaction needed.
//   * Wrong guesses bump `attempts` in place; a row at the cap stays in place,
//     dead.
//
// `code_hash` is a keyed HMAC (OtpHasher), never the raw code.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: CommandText is assembled from compile-time SQL and $N placeholders only. Every caller-supplied value is bound through DbParams, never concatenated.")]
public sealed class OtpRepository(IDbConnectionFactory db, OtpHasher hasher)
{
    private const string StatusConsumed = "consumed";
    private const string StatusSuperseded = "superseded";

    // Issues a new code for (identifier, purpose): supersedes any live
    // predecessor, then inserts. Only the latest issued code stays redeemable.
    //
    // One transaction, because that invariant is the whole point. As two
    // independent statements, concurrent issues for the same pair can
    // interleave supersede-insert-supersede-insert and leave TWO rows with
    // consumed_at IS NULL. VerifyAndConsumeAsync only ever looks at the
    // newest, so the older one is not redeemable — but it sits live until the
    // sweeper reaches it, and the header above would be describing something
    // the code does not actually guarantee. The resend cooldown makes the race
    // rare, not impossible: the cooldown is checked in the handler, well
    // before this call, so two requests that pass it together arrive here
    // together.
    public async Task IssueAsync(string identifier, string purpose, string code,
        DateTime expiresAt, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var sup = conn.CreateCommand())
        {
            sup.Transaction = tx;
            var now = DbParams.Add(sup, TimeUtils.Now());
            var st = DbParams.Add(sup, StatusSuperseded);
            var i = DbParams.Add(sup, identifier);
            var p = DbParams.Add(sup, purpose);
            sup.CommandText = $"""
                UPDATE otps SET consumed_at = {now}, status = {st}
                WHERE identifier = {i} AND purpose = {p} AND consumed_at IS NULL
                """;
            await sup.ExecuteNonQueryAsync(ct);
        }

        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        var pi = DbParams.Add(ins, identifier);
        var pp = DbParams.Add(ins, purpose);
        var ph = DbParams.Add(ins, hasher.Hash(code));
        // Timestamps bound rather than NOW(): SQLite has no NOW(), and
        // CURRENT_TIMESTAMP is UTC with second resolution, which would not
        // match the local wall-clock format these columns store.
        var pc = DbParams.Add(ins, TimeUtils.Now());
        var pe = DbParams.Add(ins, expiresAt);
        ins.CommandText = $"""
            INSERT INTO otps (identifier, purpose, code_hash, created_at, expires_at, attempts)
            VALUES ({pi}, {pp}, {ph}, {pc}, {pe}, 0)
            """;
        await ins.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    // Seconds since the newest code was issued for (identifier, purpose),
    // regardless of its state. Null when no row exists.
    public Task<int?> GetCreatedSinceAsync(string identifier, string purpose,
        CancellationToken ct = default)
        => CreatedSinceCoreAsync(identifier, purpose, ct);

    // Seconds since the newest code was issued to `identifier` under any
    // purpose. Backs the resend cooldown, which applies per destination
    // across all purposes.
    public Task<int?> GetCreatedSinceAnyPurposeAsync(string identifier,
        CancellationToken ct = default)
        => CreatedSinceCoreAsync(identifier, purpose: null, ct);

    private async Task<int?> CreatedSinceCoreAsync(string identifier, string? purpose,
        CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var i = DbParams.Add(cmd, identifier);
        cmd.CommandText = purpose is null
            ? $"SELECT MAX(created_at) FROM otps WHERE identifier = {i}"
            : $"SELECT MAX(created_at) FROM otps WHERE identifier = {i} AND purpose = {DbParams.Add(cmd, purpose)}";
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (ReadTimestamp(raw) is not { } written) return null;
        // App-side subtraction on both providers: SQLite's julianday() round
        // trip loses resolution (a 60-second gap measures as 59, letting a
        // resend through a second early), and one code path beats two.
        var elapsed = (TimeUtils.Now() - written).TotalSeconds;
        return (int)Math.Max(0, elapsed);
    }

    // Codes issued to `identifier` across all purposes since `cutoff`.
    // Backs MaxOtpRequestsPerDay.
    // `purpose` null counts every purpose. Non-null counts that one alone, or
    // — with `invertPurpose` — everything except it. The two forms exist to
    // split the daily budget into independent buckets: see the note in
    // OtpHandler for why account recovery gets one of its own, and why the
    // split has to cut BOTH ways to be worth anything.
    public async Task<int> CountIssuedSinceAsync(string identifier, DateTime cutoff,
        CancellationToken ct = default, string? purpose = null, bool invertPurpose = false)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var i = DbParams.Add(cmd, identifier);
        var c = DbParams.Add(cmd, cutoff);
        var purposeClause = purpose is null
            ? ""
            : $" AND purpose {(invertPurpose ? "<>" : "=")} {DbParams.Add(cmd, purpose)}";
        // The `>` works on both providers: PostgreSQL compares TIMESTAMPs,
        // SQLite compares fixed-width SqliteValues text, which sorts
        // chronologically by construction.
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM otps WHERE identifier = {i} AND created_at > {c}{purposeClause}
            """;
        var raw = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Verifies `code` against the latest live row for (identifier, purpose)
    // and consumes it on success. Returns false for: no row, expired,
    // attempts exhausted, hash mismatch, lost consume race.
    //
    // maxAttempts > 0 caps wrong guesses against a single stored code;
    // maxAttempts == 0 disables the cap.
    public async Task<bool> VerifyAndConsumeAsync(string identifier, string purpose,
        string code, int maxAttempts, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        // Read the candidate row completely BEFORE issuing any follow-up
        // command: Npgsql runs one command per connection, so the increment /
        // consume below can only start once this reader is disposed.
        long id;
        string storedHash;
        DateTime? expiresAt;
        int attempts;
        {
            await using var cmd = conn.CreateCommand();
            var i = DbParams.Add(cmd, identifier);
            var p = DbParams.Add(cmd, purpose);
            cmd.CommandText = $"""
                SELECT id, code_hash, expires_at, attempts FROM otps
                WHERE identifier = {i} AND purpose = {p} AND consumed_at IS NULL
                ORDER BY created_at DESC LIMIT 1
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;

            id = Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
            storedHash = reader.GetString(1);
            expiresAt = ReadTimestamp(reader.GetValue(2));
            attempts = Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (expiresAt is null || expiresAt < TimeUtils.Now()) return false;
        if (maxAttempts > 0 && attempts >= maxAttempts) return false;

        // `storedHash` is the keyed HMAC of the real code, never the
        // plaintext; hash the supplied guess the same way and compare in
        // fixed time. Both sides are fixed-width hex, so the length check
        // is constant.
        var storedBytes = System.Text.Encoding.UTF8.GetBytes(storedHash);
        var inputBytes = System.Text.Encoding.UTF8.GetBytes(hasher.Hash(code));
        var matches = storedBytes.Length == inputBytes.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedBytes, inputBytes);
        if (!matches)
        {
            // In-place increment — a read-modify-write of the whole row
            // would race with a concurrent attempt and lose an increment.
            await using var upd = conn.CreateCommand();
            var uid = DbParams.Add(upd, id);
            upd.CommandText = $"UPDATE otps SET attempts = attempts + 1 WHERE id = {uid}";
            await upd.ExecuteNonQueryAsync(ct);
            return false;
        }

        // Guarded consume: the `consumed_at IS NULL` predicate makes two
        // racing correct guesses resolve to exactly one winner — the loser's
        // UPDATE affects zero rows and reports failure.
        await using var con = conn.CreateCommand();
        var now = DbParams.Add(con, TimeUtils.Now());
        var st = DbParams.Add(con, StatusConsumed);
        var cid = DbParams.Add(con, id);
        con.CommandText = $"""
            UPDATE otps SET consumed_at = {now}, status = {st}
            WHERE id = {cid} AND consumed_at IS NULL
            """;
        return await con.ExecuteNonQueryAsync(ct) == 1;
    }

    // Purge rows older than `cutoff` — called by OtpHistorySweeper on the
    // OtpHistoryRetentionDays schedule. Retention must exceed 24h or the
    // per-day issue cap loses the rows it counts.
    public async Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var c = DbParams.Add(cmd, cutoff);
        cmd.CommandText = $"DELETE FROM otps WHERE created_at < {c}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // Timestamp columns come back as DateTime from Npgsql and as
    // SqliteValues-formatted TEXT from SQLite; converge before use.
    private static DateTime? ReadTimestamp(object? raw) => raw switch
    {
        null or DBNull => null,
        DateTime dt => dt,
        string s => SqliteValues.TryToDateTime(s, out var parsed) ? parsed : null,
        _ => null,
    };
}
