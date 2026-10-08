A technical overview of the entity lifecycle within DMART. Entities are the fundamental units of data storage and management, defined by a common structure that includes metadata and a payload.

## Core Structure

Every entity consists of two main parts:

**Meta** System-level metadata: UUID, shortname, ownership, timestamps, and access control lists (ACLs).

**Payload** The actual content body, along with its type (JSON, Markdown, Image) and schema reference.

### Meta Structure

```json
{
  "uuid": "550e8400-e29b-41d4-a716-446655440000",
  "shortname": "unique_entity_id",
  "slug": "user-friendly-slug",
  "is_active": true,
  "displayname": {
    "en": "Entity Name",
    "ar": "اسم الكيان",
    "ku": "Navê Heyber"
  },
  "description": {
    "en": "Description here",
    "ar": "الوصف هنا"
  },
  "tags": ["tag1", "tag2"],
  "created_at": "2026-07-06T10:00:00",
  "updated_at": "2026-07-06T10:00:00",
  "owner_shortname": "admin_user",
  "owner_group_shortname": "editors",
  "payload": {
    "content_type": "json",
    "schema_shortname": "custom_schema",
    "body": { "key": "value" }
  }
}
```

## Creating & Managing Entities

Entities are managed through the REST API — the single runtime path for all create, read, update, and delete operations. Every entity is persisted to **PostgreSQL**, which is the sole source of truth. The same entities can also be exported to (and imported from) the `.dm` JSON file layout for backup, migration, and seeding.

### 1. Via REST API

The primary endpoint for CRUD operations is `POST /managed/request`. It accepts a batch of records and an action type.

```json
{
  "space_name": "data",
  "request_type": "create",
  "records": [
    {
      "resource_type": "content",
      "shortname": "my_new_article",
      "subpath": "/articles",
      "attributes": {
        "is_active": true,
        "displayname": { "en": "My New Article" },
        "payload": {
          "content_type": "markdown",
          "body": "# Hello World"
        }
      }
    }
  ]
}
```

#### Supported Request Types

| Type | Description |
|---|---|
| `create` | Create new entities |
| `update` | Update existing entities (partial or full) |
| `delete` | Remove entities |
| `move` | Move entities to a different space or subpath |
| `assign` | Change ownership or group assignment |
| `update_acl` | Modify Access Control Lists |
| `patch` | Patch specific fields |

#### Cascade Delete: `force` & `dry_run`

Two request-level flags (siblings of `request_type`, applied only to `delete`) control how far a delete reaches. `force: true` cascades — it removes a folder (or a user) together with everything it contains / owns. `dry_run: true` removes **nothing**: it runs the same statements inside a transaction that is rolled back and reports the projected blast radius instead.

```json
{
  "space_name": "s",
  "request_type": "delete",
  "force": true,
  "dry_run": true,
  "records": [
    {
      "resource_type": "folder",
      "shortname": "f",
      "subpath": "/"
    }
  ]
}
```

Each deleted record comes back annotated with an `affected` total plus a per-category `report`. On a dry run `dry_run: true` is echoed and the numbers are a projection of what a real delete would remove:

```json
{
  "resource_type": "folder",
  "shortname": "f",
  "subpath": "/",
  "attributes": {
    "affected": 42,
    "report": {
      "entries": 30,
      "attachments": 9,
      "histories": 3,
      "locks": 0
    },
    "dry_run": true
  }
}
```

A plain (non-`force`) delete of a user that has created records is rejected — you must pass `force: true` to delete the user and everything they own. A force-delete can never wipe the `management` space, even if the target user owns it. Drop `dry_run` (or set it to `false`) to actually commit the cascade.

#### Reassigning Ownership: `assign`

The `assign` request type transfers an entry's owner to another user. `owner_shortname` is required in `attributes` and the target user must already exist; an optional `collaborators` map records additional assignees. The permission check uses the dedicated `assign` action (not `update`), so an admin can grant "edit but not transfer ownership" separately.

```json
{
  "space_name": "data",
  "request_type": "assign",
  "records": [
    {
      "resource_type": "content",
      "shortname": "my_new_article",
      "subpath": "/articles",
      "attributes": {
        "owner_shortname": "alice",
        "collaborators": { "reviewer": "carol" }
      }
    }
  ]
}
```

#### Per-Entry ACLs: `update_acl`

The `update_acl` request type sets the per-entry access control list from `attributes.acl`. Each ACL entry names a `user_shortname` and the list of `allowed_actions` that user may take on this specific entry — a per-entry grant layered on top of the role/permission model.

```json
{
  "space_name": "data",
  "request_type": "update_acl",
  "records": [
    {
      "resource_type": "content",
      "shortname": "my_new_article",
      "subpath": "/articles",
      "attributes": {
        "acl": [
          {
            "user_shortname": "bob",
            "allowed_actions": ["view", "update"]
          }
        ]
      }
    }
  ]
}
```

### 2. Export & Import (Backup / Migration / Seeding)

Entities live in PostgreSQL at runtime, but they can be round-tripped to and from a portable `.dm` JSON file layout via the CLI (`export`, `import`, `seed`, `migrate`). This zip-based format mirrors each entity as a `meta.<type>.json` file and is used for backups, migrations, and seeding sample data — it is **not** a live editing mode; the database always remains the source of truth.

```
spaces/
├── space_name/
│   ├── subpath/
│   │   ├── .dm/
│   │   │   ├── entity_shortname/
│   │   │   │   ├── meta.content.json
│   │   │   │   └── history/
│   │   └── entity_shortname.json
```

**Each exported entity is represented by:**

1. The directory structure for its space and subpath

2. A `meta.<type>.json` file with its metadata

3. (Optional) A separate payload file when the body is externalized

## Types of Records

DMART supports various resource types, each serving a specific purpose:

**Core** `space`, `folder`, `user`, `group`, `role`, `permission`

**Data** `content`, `schema`, `json`, `data_asset`, `media`

**Social** `post`, `comment`, `reaction`, `share`

**Workflow** `ticket`

**System** `log`, `notification`, `plugin_wrapper`, `history`

**Big Data** `parquet`, `csv`, `jsonl`, `sqlite`

## Management Space

The `management` space is reserved for system-critical entities. Security primitives are stored in specific subpaths:

| Entity | Path |
|---|---|
| **Users** | `management/users/.dm/<username>/meta.user.json` |
| **Groups** | `management/groups/.dm/<groupname>/meta.group.json` |
| **Roles** | `management/roles/.dm/<rolename>/meta.role.json` |
| **Permissions** | `management/permissions/.dm/<permname>/meta.permission.json` |

### Role Example

Defines a collection of permissions.

```json
{
  "resource_type": "role",
  "shortname": "editor",
  "is_active": true,
  "permissions": ["read_all", "write_content"]
}
```

### Permission Example

Defines granular access controls (Actions, Conditions, Restrictions).

```json
{
  "resource_type": "permission",
  "shortname": "write_content",
  "subpaths": {
    "data": ["/articles", "/news"]
  },
  "resource_types": ["content", "media"],
  "actions": ["create", "update", "view"],
  "conditions": ["is_active", "own"],
  "restricted_fields": ["owner_shortname"],
  "allowed_fields_values": {}
}
```

### Group Example

Aggregates roles.

```json
{
  "resource_type": "group",
  "shortname": "editors_group",
  "roles": ["editor"]
}
```
