using System.Data.Common;
using Dmart.Auth;
using Dmart.QueryGrammar;
using Microsoft.Data.Sqlite;

namespace Dmart.DataAdapters.Sql;

// dmart's `otp` table uses HSTORE for the `value` column on PostgreSQL (not
// JSONB). SQLite has no hstore, so the same key->string map is stored as a JSON
// object in TEXT; DbParams handles both directions, and every read here goes
// through DbParams.ReadMap so the two providers' different CLR shapes —
// IDictionary from Npgsql, string from SQLite — converge before use.
//
// HSTORE is a key→string map; we store the code, the destination, and an expires_at
// ISO timestamp so the application layer can enforce TTL.
//
// The `code` field is never the raw 6-digit OTP — it's a keyed HMAC (OtpHasher),
// so a DB read can't surface a live, replayable credential within its TTL. The
// hash is deterministic, so verification stays a single SELECT + fixed-time
// compare (no per-row KDF on the auth hot path).
public sealed class OtpRepository(IDbConnectionFactory db, OtpHasher hasher)
{
    public async Task StoreAsync(string key, string code, DateTime expiresAt, CancellationToken ct = default)
    {
        var hstore = new Dictionary<string, string?>
        {
            ["code"] = hasher.Hash(code),
            ["expires_at"] = expiresAt.ToString("O"),
        };
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var k = DbParams.Add(cmd, key);
        var v = DbParams.Add(cmd, hstore, SqlValueKind.KeyValueMap);
        // Timestamp bound rather than NOW(): SQLite has no NOW(), and
        // CURRENT_TIMESTAMP is UTC with second resolution, which would not match
        // the local wall-clock format this column stores.
        var t = DbParams.Add(cmd, TimeUtils.Now());
        cmd.CommandText = $"""
            INSERT INTO otp (key, value, timestamp)
            VALUES ({k}, {v}, {t})
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, timestamp = {t}
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Seconds elapsed since the OTP row at `key` was last written. Null when
    // no row exists. Mirrors Python's `otp_created_since` — used by
    // /user/otp-request to enforce the resend cooldown.
    public async Task<int?> GetCreatedSinceAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // SQLite reads the stored timestamp and subtracts here rather than using
        // julianday(): that returns a float day count, and the round trip loses
        // resolution — a 60-second gap measures as 59, which would let a resend
        // through a second early. PostgreSQL keeps its server-side EXTRACT.
        if (cmd is SqliteCommand)
        {
            var k = DbParams.Add(cmd, key);
            cmd.CommandText = $"SELECT timestamp FROM otp WHERE key = {k}";
            var stamp = await cmd.ExecuteScalarAsync(ct);
            if (stamp is null or DBNull) return null;
            if (!SqliteValues.TryToDateTime(stamp as string, out var written)) return null;
            var elapsed = (TimeUtils.Now() - written).TotalSeconds;
            return (int)Math.Max(0, elapsed);
        }
        var pk = DbParams.Add(cmd, key);
        cmd.CommandText = $"SELECT EXTRACT(EPOCH FROM (NOW() - timestamp))::int FROM otp WHERE key = {pk}";
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (raw is null || raw is DBNull) return null;
        return Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Peek-verify: true when a non-expired OTP at `key` hashes to the same value
    // as `candidate`, WITHOUT consuming it (Python parity: verify_user calls
    // db.get_otp, which doesn't delete). Used by /user/create and the
    // /user/profile email/msisdn change so a failed attempt leaves the OTP usable
    // for another try within its TTL. Because codes are stored hashed, callers
    // can no longer fetch the plaintext to compare — they hand us the candidate
    // and we compare hashes here.
    public async Task<bool> VerifyPeekAsync(string key, string candidate, CancellationToken ct = default)
    {
        var stored = await PeekStoredHashAsync(key, ct);
        if (stored is null) return false;
        var expected = hasher.Hash(candidate);
        // Fixed-time compare over the hex hashes (both fixed 64-char ASCII, so
        // the length precondition always holds). The keyed hash already strips
        // any per-digit timing signal; this keeps the compare uniform.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(stored),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }

    // Returns the stored (hashed) OTP value at `key`, or null when no row exists
    // or it has expired. This is the keyed HMAC, NOT a usable code — exposed for
    // existence/freshness assertions and never compared against a plaintext code.
    // Callers validating a code use VerifyPeekAsync; this only answers "is there
    // a live OTP here?" (and, since the hash is deterministic, "is it unchanged?").
    public async Task<string?> PeekStoredHashAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var k = DbParams.Add(cmd, key);
        cmd.CommandText = $"SELECT value FROM otp WHERE key = {k}";
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (DbParams.ReadMap(raw) is not { } dict) return null;
        if (!dict.TryGetValue("code", out var code)) return null;
        if (dict.TryGetValue("expires_at", out var expRaw)
            && DateTime.TryParse(expRaw, out var exp) && exp < TimeUtils.Now()) return null;
        return code;
    }

    // Unconditional delete of the OTP row at `key`. Used by /user/create to
    // consume the registration OTP once the account is persisted, so a stored
    // code can't be replayed (e.g. via /user/otp-confirm) after the user
    // exists. A no-op when no row is present.
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var k = DbParams.Add(cmd, key);
        cmd.CommandText = $"DELETE FROM otp WHERE key = {k}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<bool> VerifyAndConsumeAsync(string key, string code, CancellationToken ct = default)
        => VerifyAndConsumeAsync(key, code, maxAttempts: 0, ct);

    // maxAttempts > 0 caps wrong guesses against a single stored code: each
    // mismatch bumps an "attempts" counter in the HSTORE value, and once it
    // reaches the cap the row is deleted so the code can never be redeemed —
    // even by a later correct guess. This closes the brute-force window on
    // anonymous OTP verification that per-IP rate limiting alone can't (a
    // distributed attacker spreads guesses across IPs). maxAttempts == 0
    // preserves the original uncapped behavior.
    //
    // Deliberate server-side divergence from Python dmart; the wire response is
    // unchanged (an exhausted code looks identical to an expired one).
    public async Task<bool> VerifyAndConsumeAsync(
        string key, string code, int maxAttempts, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                var k = DbParams.Add(cmd, key);
                cmd.CommandText = $"SELECT value FROM otp WHERE key = {k}";
                var raw = await cmd.ExecuteScalarAsync(ct);
                if (DbParams.ReadMap(raw) is not { } dict) return false;
                if (!dict.TryGetValue("code", out var stored) || stored is null) return false;
                if (dict.TryGetValue("expires_at", out var expRaw)
                    && DateTime.TryParse(expRaw, out var exp) && exp < TimeUtils.Now()) return false;
                // `stored` is the keyed HMAC of the real code, never the plaintext;
                // hash the supplied guess the same way and compare in fixed time.
                // Both sides are fixed-width hex, so the length check is constant.
                var storedBytes = System.Text.Encoding.UTF8.GetBytes(stored);
                var inputBytes = System.Text.Encoding.UTF8.GetBytes(hasher.Hash(code));
                var matches = storedBytes.Length == inputBytes.Length
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedBytes, inputBytes);
                if (!matches)
                {
                    await RecordFailedAttemptAsync(conn, tx, key, dict, maxAttempts, ct);
                    await tx.CommitAsync(ct);
                    return false;
                }
            }
            await using var del = conn.CreateCommand();
            del.Transaction = tx;
            var dk = DbParams.Add(del, key);
            del.CommandText = $"DELETE FROM otp WHERE key = {dk}";
            await del.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // On a wrong guess, either bump the attempts counter or, once the cap is
    // reached, delete the row so the code is permanently spent. No-op when
    // capping is disabled (maxAttempts <= 0).
    private static async Task RecordFailedAttemptAsync(
        DbConnection conn, DbTransaction tx, string key,
        IDictionary<string, string?> dict, int maxAttempts, CancellationToken ct)
    {
        if (maxAttempts <= 0) return;

        var attempts = dict.TryGetValue("attempts", out var a)
            && int.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n + 1 : 1;

        if (attempts >= maxAttempts)
        {
            await using var del = conn.CreateCommand();
            del.Transaction = tx;
            var dk = DbParams.Add(del, key);
            del.CommandText = $"DELETE FROM otp WHERE key = {dk}";
            await del.ExecuteNonQueryAsync(ct);
            return;
        }

        // Merge/overwrite just the one key, leaving code and expires_at intact.
        // PostgreSQL concatenates a single-pair hstore; SQLite's json_set does
        // the same to a JSON object. Both are in-place partial updates — a
        // read-modify-write of the whole map would race with a concurrent
        // attempt and lose one of the increments.
        await using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        var k = DbParams.Add(upd, key);
        var v = DbParams.Add(upd, attempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        upd.CommandText = upd is SqliteCommand
            ? $"UPDATE otp SET value = json_set(value, '$.attempts', {v}) WHERE key = {k}"
            : $"UPDATE otp SET value = value || hstore('attempts', {v}) WHERE key = {k}";
        await upd.ExecuteNonQueryAsync(ct);
    }
}
