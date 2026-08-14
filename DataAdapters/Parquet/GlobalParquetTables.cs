using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.DataAdapters.Parquet;

// The four tables that are NOT per-space: spaces, users, roles, permissions.
//
// They are what makes a restore usable rather than merely present. Entries
// without these give you content in a system nobody can log into, with no ACL
// and no space definitions — which is why they are worth the volume of
// mechanical mapping below.
//
// Unlike entries, `space_name` IS a column here: these files are not Hive
// partitioned (§4.1 puts them at `users/part-00000.parquet`, no `space_name=`
// directory), so there is no partition key to collide with. Users, roles and
// permissions all live in the management space, and spaces span every space by
// definition — partitioning either would produce one directory, or one per row.

internal static class SpaceParquetTable
{
    public static IReadOnlyList<ParquetFileWriter.ColumnSpec> Schema { get; } =
    [
        Pq.Required("shortname"),
        Pq.Required("space_name"),
        Pq.Required("subpath"),
        Pq.Required("uuid"),
        Pq.Flag("is_active"),
        Pq.Optional("slug"),
        Pq.Optional("displayname"),
        Pq.Optional("description"),
        Pq.Required("tags"),
        Pq.Timestamp("created_at"),
        Pq.Timestamp("updated_at"),
        Pq.Required("owner_shortname"),
        Pq.Optional("owner_group_shortname"),
        Pq.Optional("acl"),
        Pq.Optional("payload"),
        Pq.Optional("relationships"),
        Pq.Optional("last_checksum_history"),
        Pq.Required("root_registration_signature"),
        Pq.Required("primary_website"),
        Pq.Flag("indexing_enabled"),
        Pq.Flag("capture_misses"),
        Pq.Flag("check_health"),
        Pq.Required("languages"),
        Pq.Required("icon"),
        Pq.Optional("mirrors"),
        Pq.Optional("hide_folders"),
        Pq.OptionalFlag("hide_space"),
        Pq.OptionalInt("ordinal"),
        Pq.Required("query_policies"),
    ];

    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(IReadOnlyList<Space> rows) =>
    [
        Pq.Str(rows, s => s.Shortname),
        Pq.Str(rows, s => s.SpaceName),
        Pq.Str(rows, s => s.Subpath),
        Pq.Str(rows, s => s.Uuid),
        Pq.Bool(rows, s => s.IsActive),
        Pq.NullableStr(rows, s => s.Slug),
        Pq.NullableStr(rows, s => Pq.Json(s.Displayname, DmartJsonContext.Default.Translation)),
        Pq.NullableStr(rows, s => Pq.Json(s.Description, DmartJsonContext.Default.Translation)),
        Pq.Str(rows, s => Pq.JsonAlways(s.Tags, DmartJsonContext.Default.ListString)),
        Pq.Ts(rows, s => s.CreatedAt),
        Pq.Ts(rows, s => s.UpdatedAt),
        Pq.Str(rows, s => s.OwnerShortname),
        Pq.NullableStr(rows, s => s.OwnerGroupShortname),
        Pq.NullableStr(rows, s => Pq.Json(s.Acl, DmartJsonContext.Default.ListAclEntry)),
        Pq.NullableStr(rows, s => Pq.Json(s.Payload, DmartJsonContext.Default.Payload)),
        Pq.NullableStr(rows, s => Pq.Json(s.Relationships, DmartJsonContext.Default.ListDictionaryStringObject)),
        Pq.NullableStr(rows, s => s.LastChecksumHistory),
        Pq.Str(rows, s => s.RootRegistrationSignature),
        Pq.Str(rows, s => s.PrimaryWebsite),
        Pq.Bool(rows, s => s.IndexingEnabled),
        Pq.Bool(rows, s => s.CaptureMisses),
        Pq.Bool(rows, s => s.CheckHealth),
        Pq.Str(rows, s => Pq.JsonAlways(s.Languages, DmartJsonContext.Default.ListLanguage)),
        Pq.Str(rows, s => s.Icon),
        Pq.NullableStr(rows, s => Pq.Json(s.Mirrors, DmartJsonContext.Default.ListString)),
        Pq.NullableStr(rows, s => Pq.Json(s.HideFolders, DmartJsonContext.Default.ListString)),
        Pq.NullableBool(rows, s => s.HideSpace),
        Pq.NullableInt(rows, s => s.Ordinal),
        Pq.Str(rows, s => Pq.JsonAlways(s.QueryPolicies, DmartJsonContext.Default.ListString)),
    ];

    public static List<Space> FromTable(ParquetFileReader.ParquetTable t)
    {
        var count = (int)t.RowCount;
        var shortname = Pq.Strings(t, "shortname");
        var spaceName = Pq.Strings(t, "space_name");
        var subpath = Pq.Strings(t, "subpath");
        var uuid = Pq.Strings(t, "uuid");
        var isActive = Pq.Bools(t, "is_active");
        var slug = Pq.Strings(t, "slug");
        var displayname = Pq.Strings(t, "displayname");
        var description = Pq.Strings(t, "description");
        var tags = Pq.Strings(t, "tags");
        var createdAt = t.Column("created_at").AsTimestamps();
        var updatedAt = t.Column("updated_at").AsTimestamps();
        var owner = Pq.Strings(t, "owner_shortname");
        var ownerGroup = Pq.Strings(t, "owner_group_shortname");
        var acl = Pq.Strings(t, "acl");
        var payload = Pq.Strings(t, "payload");
        var relationships = Pq.Strings(t, "relationships");
        var checksum = Pq.Strings(t, "last_checksum_history");
        var rootSig = Pq.Strings(t, "root_registration_signature");
        var website = Pq.Strings(t, "primary_website");
        var indexing = Pq.Bools(t, "indexing_enabled");
        var captureMisses = Pq.Bools(t, "capture_misses");
        var checkHealth = Pq.Bools(t, "check_health");
        var languages = Pq.Strings(t, "languages");
        var icon = Pq.Strings(t, "icon");
        var mirrors = Pq.Strings(t, "mirrors");
        var hideFolders = Pq.Strings(t, "hide_folders");
        var hideSpace = Pq.Bools(t, "hide_space");
        var ordinal = Pq.Longs(t, "ordinal");
        var queryPolicies = Pq.Strings(t, "query_policies");

        var result = new List<Space>(count);
        for (var i = 0; i < count; i++)
            result.Add(new Space
            {
                Shortname = shortname[i] ?? "",
                SpaceName = spaceName[i] ?? "",
                Subpath = subpath[i] ?? "/",
                Uuid = uuid[i] ?? "",
                IsActive = isActive[i] ?? false,
                Slug = slug[i],
                Displayname = Pq.FromJson(displayname[i], DmartJsonContext.Default.Translation),
                Description = Pq.FromJson(description[i], DmartJsonContext.Default.Translation),
                Tags = Pq.FromJson(tags[i], DmartJsonContext.Default.ListString) ?? [],
                CreatedAt = Pq.ToLocalNaive(createdAt[i]),
                UpdatedAt = Pq.ToLocalNaive(updatedAt[i]),
                OwnerShortname = owner[i] ?? "",
                OwnerGroupShortname = ownerGroup[i],
                Acl = Pq.FromJson(acl[i], DmartJsonContext.Default.ListAclEntry),
                Payload = Pq.FromJson(payload[i], DmartJsonContext.Default.Payload),
                Relationships = Pq.FromJson(relationships[i], DmartJsonContext.Default.ListDictionaryStringObject),
                LastChecksumHistory = checksum[i],
                RootRegistrationSignature = rootSig[i] ?? "",
                PrimaryWebsite = website[i] ?? "",
                IndexingEnabled = indexing[i] ?? false,
                CaptureMisses = captureMisses[i] ?? false,
                CheckHealth = checkHealth[i] ?? false,
                Languages = Pq.FromJson(languages[i], DmartJsonContext.Default.ListLanguage) ?? [],
                Icon = icon[i] ?? "",
                Mirrors = Pq.FromJson(mirrors[i], DmartJsonContext.Default.ListString),
                HideFolders = Pq.FromJson(hideFolders[i], DmartJsonContext.Default.ListString),
                HideSpace = hideSpace[i],
                Ordinal = ordinal[i] is { } o ? (int)o : null,
                QueryPolicies = Pq.FromJson(queryPolicies[i], DmartJsonContext.Default.ListString) ?? [],
            });
        return result;
    }
}

internal static class UserParquetTable
{
    // `password` is the Argon2 hash. It is included DELIBERATELY: without it a
    // restore leaves every user unable to log in, which is the difference
    // between a backup and a content archive. The consequence is that an export
    // directory holds credential material and must be handled like a database
    // dump — restricted permissions, encrypted if it leaves the host. The CLI
    // says so at export time rather than leaving an operator to infer it.
    //
    // Note this diverges from the zip export, where User.Password is
    // [JsonIgnore] and silently absent.
    public static IReadOnlyList<ParquetFileWriter.ColumnSpec> Schema { get; } =
    [
        Pq.Required("shortname"),
        Pq.Required("space_name"),
        Pq.Required("subpath"),
        Pq.Required("uuid"),
        Pq.Flag("is_active"),
        Pq.Optional("slug"),
        Pq.Optional("displayname"),
        Pq.Optional("description"),
        Pq.Required("tags"),
        Pq.Timestamp("created_at"),
        Pq.Timestamp("updated_at"),
        Pq.Required("owner_shortname"),
        Pq.Optional("owner_group_shortname"),
        Pq.Optional("payload"),
        Pq.Optional("last_checksum_history"),
        Pq.Optional("password"),
        Pq.Required("roles"),
        Pq.Required("groups"),
        Pq.Optional("acl"),
        Pq.Optional("relationships"),
        Pq.Required("type"),
        Pq.Required("language"),
        Pq.Optional("email"),
        Pq.Optional("msisdn"),
        Pq.Flag("locked_to_device"),
        Pq.Flag("is_email_verified"),
        Pq.Flag("is_msisdn_verified"),
        Pq.Flag("force_password_change"),
        Pq.Optional("device_id"),
        Pq.Optional("google_id"),
        Pq.Optional("facebook_id"),
        Pq.Optional("apple_id"),
        Pq.Optional("social_avatar_url"),
        Pq.OptionalInt("attempt_count"),
        Pq.Optional("last_login"),
        Pq.OptionalTimestamp("last_failed_login"),
        Pq.Optional("notes"),
        Pq.Required("query_policies"),
    ];

    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(IReadOnlyList<User> rows) =>
    [
        Pq.Str(rows, u => u.Shortname),
        Pq.Str(rows, u => u.SpaceName),
        Pq.Str(rows, u => u.Subpath),
        Pq.Str(rows, u => u.Uuid),
        Pq.Bool(rows, u => u.IsActive),
        Pq.NullableStr(rows, u => u.Slug),
        Pq.NullableStr(rows, u => Pq.Json(u.Displayname, DmartJsonContext.Default.Translation)),
        Pq.NullableStr(rows, u => Pq.Json(u.Description, DmartJsonContext.Default.Translation)),
        Pq.Str(rows, u => Pq.JsonAlways(u.Tags, DmartJsonContext.Default.ListString)),
        Pq.Ts(rows, u => u.CreatedAt),
        Pq.Ts(rows, u => u.UpdatedAt),
        Pq.Str(rows, u => u.OwnerShortname),
        Pq.NullableStr(rows, u => u.OwnerGroupShortname),
        Pq.NullableStr(rows, u => Pq.Json(u.Payload, DmartJsonContext.Default.Payload)),
        Pq.NullableStr(rows, u => u.LastChecksumHistory),
        Pq.NullableStr(rows, u => u.Password),
        Pq.Str(rows, u => Pq.JsonAlways(u.Roles, DmartJsonContext.Default.ListString)),
        Pq.Str(rows, u => Pq.JsonAlways(u.Groups, DmartJsonContext.Default.ListString)),
        Pq.NullableStr(rows, u => Pq.Json(u.Acl, DmartJsonContext.Default.ListAclEntry)),
        Pq.NullableStr(rows, u => Pq.Json(u.Relationships, DmartJsonContext.Default.ListDictionaryStringObject)),
        Pq.Str(rows, u => JsonbHelpers.EnumMember(u.Type)),
        Pq.Str(rows, u => JsonbHelpers.EnumMember(u.Language)),
        Pq.NullableStr(rows, u => u.Email),
        Pq.NullableStr(rows, u => u.Msisdn),
        Pq.Bool(rows, u => u.LockedToDevice),
        Pq.Bool(rows, u => u.IsEmailVerified),
        Pq.Bool(rows, u => u.IsMsisdnVerified),
        Pq.Bool(rows, u => u.ForcePasswordChange),
        Pq.NullableStr(rows, u => u.DeviceId),
        Pq.NullableStr(rows, u => u.GoogleId),
        Pq.NullableStr(rows, u => u.FacebookId),
        Pq.NullableStr(rows, u => u.AppleId),
        Pq.NullableStr(rows, u => u.SocialAvatarUrl),
        Pq.NullableInt(rows, u => u.AttemptCount),
        Pq.NullableStr(rows, u => Pq.Json(u.LastLogin, DmartJsonContext.Default.DictionaryStringObject)),
        Pq.NullableTs(rows, u => u.LastFailedLogin),
        Pq.NullableStr(rows, u => u.Notes),
        Pq.Str(rows, u => Pq.JsonAlways(u.QueryPolicies, DmartJsonContext.Default.ListString)),
    ];

    public static List<User> FromTable(ParquetFileReader.ParquetTable t)
    {
        var count = (int)t.RowCount;
        var shortname = Pq.Strings(t, "shortname");
        var spaceName = Pq.Strings(t, "space_name");
        var subpath = Pq.Strings(t, "subpath");
        var uuid = Pq.Strings(t, "uuid");
        var isActive = Pq.Bools(t, "is_active");
        var slug = Pq.Strings(t, "slug");
        var displayname = Pq.Strings(t, "displayname");
        var description = Pq.Strings(t, "description");
        var tags = Pq.Strings(t, "tags");
        var createdAt = t.Column("created_at").AsTimestamps();
        var updatedAt = t.Column("updated_at").AsTimestamps();
        var owner = Pq.Strings(t, "owner_shortname");
        var ownerGroup = Pq.Strings(t, "owner_group_shortname");
        var payload = Pq.Strings(t, "payload");
        var checksum = Pq.Strings(t, "last_checksum_history");
        var password = Pq.Strings(t, "password");
        var roles = Pq.Strings(t, "roles");
        var groups = Pq.Strings(t, "groups");
        var acl = Pq.Strings(t, "acl");
        var relationships = Pq.Strings(t, "relationships");
        var type = Pq.Strings(t, "type");
        var language = Pq.Strings(t, "language");
        var email = Pq.Strings(t, "email");
        var msisdn = Pq.Strings(t, "msisdn");
        var lockedToDevice = Pq.Bools(t, "locked_to_device");
        var emailVerified = Pq.Bools(t, "is_email_verified");
        var msisdnVerified = Pq.Bools(t, "is_msisdn_verified");
        var forceChange = Pq.Bools(t, "force_password_change");
        var deviceId = Pq.Strings(t, "device_id");
        var googleId = Pq.Strings(t, "google_id");
        var facebookId = Pq.Strings(t, "facebook_id");
        var appleId = Pq.Strings(t, "apple_id");
        var avatar = Pq.Strings(t, "social_avatar_url");
        var attempts = Pq.Longs(t, "attempt_count");
        var lastLogin = Pq.Strings(t, "last_login");
        var lastFailed = t.Column("last_failed_login").AsTimestamps();
        var notes = Pq.Strings(t, "notes");
        var queryPolicies = Pq.Strings(t, "query_policies");

        var result = new List<User>(count);
        for (var i = 0; i < count; i++)
            result.Add(new User
            {
                Shortname = shortname[i] ?? "",
                SpaceName = spaceName[i] ?? "",
                Subpath = subpath[i] ?? "/",
                Uuid = uuid[i] ?? "",
                IsActive = isActive[i] ?? false,
                Slug = slug[i],
                Displayname = Pq.FromJson(displayname[i], DmartJsonContext.Default.Translation),
                Description = Pq.FromJson(description[i], DmartJsonContext.Default.Translation),
                Tags = Pq.FromJson(tags[i], DmartJsonContext.Default.ListString) ?? [],
                CreatedAt = Pq.ToLocalNaive(createdAt[i]),
                UpdatedAt = Pq.ToLocalNaive(updatedAt[i]),
                OwnerShortname = owner[i] ?? "",
                OwnerGroupShortname = ownerGroup[i],
                Payload = Pq.FromJson(payload[i], DmartJsonContext.Default.Payload),
                LastChecksumHistory = checksum[i],
                Password = password[i],
                Roles = Pq.FromJson(roles[i], DmartJsonContext.Default.ListString) ?? [],
                Groups = Pq.FromJson(groups[i], DmartJsonContext.Default.ListString) ?? [],
                Acl = Pq.FromJson(acl[i], DmartJsonContext.Default.ListAclEntry),
                Relationships = Pq.FromJson(relationships[i], DmartJsonContext.Default.ListDictionaryStringObject),
                Type = JsonbHelpers.ParseEnumMember<UserType>(type[i] ?? "web"),
                Language = JsonbHelpers.ParseEnumMember<Language>(language[i] ?? "en"),
                Email = email[i],
                Msisdn = msisdn[i],
                LockedToDevice = lockedToDevice[i] ?? false,
                IsEmailVerified = emailVerified[i] ?? false,
                IsMsisdnVerified = msisdnVerified[i] ?? false,
                ForcePasswordChange = forceChange[i] ?? false,
                DeviceId = deviceId[i],
                GoogleId = googleId[i],
                FacebookId = facebookId[i],
                AppleId = appleId[i],
                SocialAvatarUrl = avatar[i],
                AttemptCount = attempts[i] is { } a ? (int)a : null,
                LastLogin = Pq.FromJson(lastLogin[i], DmartJsonContext.Default.DictionaryStringObject),
                LastFailedLogin = Pq.ToLocalNaiveOrNull(lastFailed[i]),
                Notes = notes[i],
                QueryPolicies = Pq.FromJson(queryPolicies[i], DmartJsonContext.Default.ListString) ?? [],
            });
        return result;
    }
}

internal static class RoleParquetTable
{
    public static IReadOnlyList<ParquetFileWriter.ColumnSpec> Schema { get; } =
    [
        Pq.Required("shortname"),
        Pq.Required("space_name"),
        Pq.Required("subpath"),
        Pq.Required("uuid"),
        Pq.Flag("is_active"),
        Pq.Optional("slug"),
        Pq.Optional("displayname"),
        Pq.Optional("description"),
        Pq.Required("tags"),
        Pq.Timestamp("created_at"),
        Pq.Timestamp("updated_at"),
        Pq.Required("owner_shortname"),
        Pq.Optional("owner_group_shortname"),
        Pq.Optional("acl"),
        Pq.Optional("payload"),
        Pq.Optional("relationships"),
        Pq.Optional("last_checksum_history"),
        Pq.Required("permissions"),
        Pq.Optional("grantable_by"),
        Pq.Required("query_policies"),
    ];

    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(IReadOnlyList<Role> rows) =>
    [
        Pq.Str(rows, r => r.Shortname),
        Pq.Str(rows, r => r.SpaceName),
        Pq.Str(rows, r => r.Subpath),
        Pq.Str(rows, r => r.Uuid),
        Pq.Bool(rows, r => r.IsActive),
        Pq.NullableStr(rows, r => r.Slug),
        Pq.NullableStr(rows, r => Pq.Json(r.Displayname, DmartJsonContext.Default.Translation)),
        Pq.NullableStr(rows, r => Pq.Json(r.Description, DmartJsonContext.Default.Translation)),
        Pq.Str(rows, r => Pq.JsonAlways(r.Tags, DmartJsonContext.Default.ListString)),
        Pq.Ts(rows, r => r.CreatedAt),
        Pq.Ts(rows, r => r.UpdatedAt),
        Pq.Str(rows, r => r.OwnerShortname),
        Pq.NullableStr(rows, r => r.OwnerGroupShortname),
        Pq.NullableStr(rows, r => Pq.Json(r.Acl, DmartJsonContext.Default.ListAclEntry)),
        Pq.NullableStr(rows, r => Pq.Json(r.Payload, DmartJsonContext.Default.Payload)),
        Pq.NullableStr(rows, r => Pq.Json(r.Relationships, DmartJsonContext.Default.ListDictionaryStringObject)),
        Pq.NullableStr(rows, r => r.LastChecksumHistory),
        Pq.Str(rows, r => Pq.JsonAlways(r.Permissions, DmartJsonContext.Default.ListString)),
        Pq.NullableStr(rows, r => Pq.Json(r.GrantableBy, DmartJsonContext.Default.ListString)),
        Pq.Str(rows, r => Pq.JsonAlways(r.QueryPolicies, DmartJsonContext.Default.ListString)),
    ];

    public static List<Role> FromTable(ParquetFileReader.ParquetTable t)
    {
        var count = (int)t.RowCount;
        var shortname = Pq.Strings(t, "shortname");
        var spaceName = Pq.Strings(t, "space_name");
        var subpath = Pq.Strings(t, "subpath");
        var uuid = Pq.Strings(t, "uuid");
        var isActive = Pq.Bools(t, "is_active");
        var slug = Pq.Strings(t, "slug");
        var displayname = Pq.Strings(t, "displayname");
        var description = Pq.Strings(t, "description");
        var tags = Pq.Strings(t, "tags");
        var createdAt = t.Column("created_at").AsTimestamps();
        var updatedAt = t.Column("updated_at").AsTimestamps();
        var owner = Pq.Strings(t, "owner_shortname");
        var ownerGroup = Pq.Strings(t, "owner_group_shortname");
        var acl = Pq.Strings(t, "acl");
        var payload = Pq.Strings(t, "payload");
        var relationships = Pq.Strings(t, "relationships");
        var checksum = Pq.Strings(t, "last_checksum_history");
        var permissions = Pq.Strings(t, "permissions");
        var grantableBy = Pq.Strings(t, "grantable_by");
        var queryPolicies = Pq.Strings(t, "query_policies");

        var result = new List<Role>(count);
        for (var i = 0; i < count; i++)
            result.Add(new Role
            {
                Shortname = shortname[i] ?? "",
                SpaceName = spaceName[i] ?? "",
                Subpath = subpath[i] ?? "/",
                Uuid = uuid[i] ?? "",
                IsActive = isActive[i] ?? false,
                Slug = slug[i],
                Displayname = Pq.FromJson(displayname[i], DmartJsonContext.Default.Translation),
                Description = Pq.FromJson(description[i], DmartJsonContext.Default.Translation),
                Tags = Pq.FromJson(tags[i], DmartJsonContext.Default.ListString) ?? [],
                CreatedAt = Pq.ToLocalNaive(createdAt[i]),
                UpdatedAt = Pq.ToLocalNaive(updatedAt[i]),
                OwnerShortname = owner[i] ?? "",
                OwnerGroupShortname = ownerGroup[i],
                Acl = Pq.FromJson(acl[i], DmartJsonContext.Default.ListAclEntry),
                Payload = Pq.FromJson(payload[i], DmartJsonContext.Default.Payload),
                Relationships = Pq.FromJson(relationships[i], DmartJsonContext.Default.ListDictionaryStringObject),
                LastChecksumHistory = checksum[i],
                Permissions = Pq.FromJson(permissions[i], DmartJsonContext.Default.ListString) ?? [],
                GrantableBy = Pq.FromJson(grantableBy[i], DmartJsonContext.Default.ListString),
                QueryPolicies = Pq.FromJson(queryPolicies[i], DmartJsonContext.Default.ListString) ?? [],
            });
        return result;
    }
}

internal static class PermissionParquetTable
{
    public static IReadOnlyList<ParquetFileWriter.ColumnSpec> Schema { get; } =
    [
        Pq.Required("shortname"),
        Pq.Required("space_name"),
        Pq.Required("subpath"),
        Pq.Required("uuid"),
        Pq.Flag("is_active"),
        Pq.Optional("slug"),
        Pq.Optional("displayname"),
        Pq.Optional("description"),
        Pq.Required("tags"),
        Pq.Timestamp("created_at"),
        Pq.Timestamp("updated_at"),
        Pq.Required("owner_shortname"),
        Pq.Optional("owner_group_shortname"),
        Pq.Optional("acl"),
        Pq.Optional("payload"),
        Pq.Optional("relationships"),
        Pq.Optional("last_checksum_history"),
        Pq.Required("subpaths"),
        Pq.Required("resource_types"),
        Pq.Required("actions"),
        Pq.Required("conditions"),
        Pq.Optional("restricted_fields"),
        Pq.Optional("allowed_fields_values"),
        Pq.Optional("filter_fields_values"),
        Pq.Required("query_policies"),
    ];

    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(IReadOnlyList<Permission> rows) =>
    [
        Pq.Str(rows, p => p.Shortname),
        Pq.Str(rows, p => p.SpaceName),
        Pq.Str(rows, p => p.Subpath),
        Pq.Str(rows, p => p.Uuid),
        Pq.Bool(rows, p => p.IsActive),
        Pq.NullableStr(rows, p => p.Slug),
        Pq.NullableStr(rows, p => Pq.Json(p.Displayname, DmartJsonContext.Default.Translation)),
        Pq.NullableStr(rows, p => Pq.Json(p.Description, DmartJsonContext.Default.Translation)),
        Pq.Str(rows, p => Pq.JsonAlways(p.Tags, DmartJsonContext.Default.ListString)),
        Pq.Ts(rows, p => p.CreatedAt),
        Pq.Ts(rows, p => p.UpdatedAt),
        Pq.Str(rows, p => p.OwnerShortname),
        Pq.NullableStr(rows, p => p.OwnerGroupShortname),
        Pq.NullableStr(rows, p => Pq.Json(p.Acl, DmartJsonContext.Default.ListAclEntry)),
        Pq.NullableStr(rows, p => Pq.Json(p.Payload, DmartJsonContext.Default.Payload)),
        Pq.NullableStr(rows, p => Pq.Json(p.Relationships, DmartJsonContext.Default.ListDictionaryStringObject)),
        Pq.NullableStr(rows, p => p.LastChecksumHistory),
        Pq.Str(rows, p => Pq.JsonAlways(p.Subpaths, DmartJsonContext.Default.DictionaryStringListString)),
        Pq.Str(rows, p => Pq.JsonAlways(p.ResourceTypes, DmartJsonContext.Default.ListString)),
        Pq.Str(rows, p => Pq.JsonAlways(p.Actions, DmartJsonContext.Default.ListString)),
        Pq.Str(rows, p => Pq.JsonAlways(p.Conditions, DmartJsonContext.Default.ListString)),
        Pq.NullableStr(rows, p => Pq.Json(p.RestrictedFields, DmartJsonContext.Default.ListString)),
        Pq.NullableStr(rows, p => Pq.Json(p.AllowedFieldsValues, DmartJsonContext.Default.DictionaryStringObject)),
        Pq.NullableStr(rows, p => p.FilterFieldsValues),
        Pq.Str(rows, p => Pq.JsonAlways(p.QueryPolicies, DmartJsonContext.Default.ListString)),
    ];

    public static List<Permission> FromTable(ParquetFileReader.ParquetTable t)
    {
        var count = (int)t.RowCount;
        var shortname = Pq.Strings(t, "shortname");
        var spaceName = Pq.Strings(t, "space_name");
        var subpath = Pq.Strings(t, "subpath");
        var uuid = Pq.Strings(t, "uuid");
        var isActive = Pq.Bools(t, "is_active");
        var slug = Pq.Strings(t, "slug");
        var displayname = Pq.Strings(t, "displayname");
        var description = Pq.Strings(t, "description");
        var tags = Pq.Strings(t, "tags");
        var createdAt = t.Column("created_at").AsTimestamps();
        var updatedAt = t.Column("updated_at").AsTimestamps();
        var owner = Pq.Strings(t, "owner_shortname");
        var ownerGroup = Pq.Strings(t, "owner_group_shortname");
        var acl = Pq.Strings(t, "acl");
        var payload = Pq.Strings(t, "payload");
        var relationships = Pq.Strings(t, "relationships");
        var checksum = Pq.Strings(t, "last_checksum_history");
        var subpaths = Pq.Strings(t, "subpaths");
        var resourceTypes = Pq.Strings(t, "resource_types");
        var actions = Pq.Strings(t, "actions");
        var conditions = Pq.Strings(t, "conditions");
        var restricted = Pq.Strings(t, "restricted_fields");
        var allowedValues = Pq.Strings(t, "allowed_fields_values");
        var filterValues = Pq.Strings(t, "filter_fields_values");
        var queryPolicies = Pq.Strings(t, "query_policies");

        var result = new List<Permission>(count);
        for (var i = 0; i < count; i++)
            result.Add(new Permission
            {
                Shortname = shortname[i] ?? "",
                SpaceName = spaceName[i] ?? "",
                Subpath = subpath[i] ?? "/",
                Uuid = uuid[i] ?? "",
                IsActive = isActive[i] ?? false,
                Slug = slug[i],
                Displayname = Pq.FromJson(displayname[i], DmartJsonContext.Default.Translation),
                Description = Pq.FromJson(description[i], DmartJsonContext.Default.Translation),
                Tags = Pq.FromJson(tags[i], DmartJsonContext.Default.ListString) ?? [],
                CreatedAt = Pq.ToLocalNaive(createdAt[i]),
                UpdatedAt = Pq.ToLocalNaive(updatedAt[i]),
                OwnerShortname = owner[i] ?? "",
                OwnerGroupShortname = ownerGroup[i],
                Acl = Pq.FromJson(acl[i], DmartJsonContext.Default.ListAclEntry),
                Payload = Pq.FromJson(payload[i], DmartJsonContext.Default.Payload),
                Relationships = Pq.FromJson(relationships[i], DmartJsonContext.Default.ListDictionaryStringObject),
                LastChecksumHistory = checksum[i],
                Subpaths = Pq.FromJson(subpaths[i], DmartJsonContext.Default.DictionaryStringListString) ?? [],
                ResourceTypes = Pq.FromJson(resourceTypes[i], DmartJsonContext.Default.ListString) ?? [],
                Actions = Pq.FromJson(actions[i], DmartJsonContext.Default.ListString) ?? [],
                Conditions = Pq.FromJson(conditions[i], DmartJsonContext.Default.ListString) ?? [],
                RestrictedFields = Pq.FromJson(restricted[i], DmartJsonContext.Default.ListString),
                AllowedFieldsValues = Pq.FromJson(allowedValues[i], DmartJsonContext.Default.DictionaryStringObject),
                FilterFieldsValues = filterValues[i],
                QueryPolicies = Pq.FromJson(queryPolicies[i], DmartJsonContext.Default.ListString) ?? [],
            });
        return result;
    }
}
