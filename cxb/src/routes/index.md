<script lang="ts">
import {StarSolid,FolderSolid,BriefcaseSolid,LayersSolid,CubeSolid} from 'flowbite-svelte-icons';
</script>
<div class="prose dark:prose-invert max-w-none px-4 sm:px-6 py-6 grid grid-cols-1 md:grid-cols-2 gap-x-10">
<div class="min-w-0">

# What is DMART?
> DMART is an open-source general-purpose low-code data platform that is capable of assimilating and servicing a wide variety of data.
> DMART offers a number of unique features that simplify the setup of your backend.

----

# <span class="flex items-center gap-2"><StarSolid size="xl" aria-hidden="true"/>Features</span>

- Unified API (Data-as-a-Service) that can readily and directly service Web and mobile frontends. OpenAPI-compatible [JSON API](https://api.dmart.cc/docs)
- Built-in user management
- Built-in security management (Permissions and Roles)
- Web-based admin management UI
- Extensible by plugins or micro-services (leveraging JWT)
- Licensed as free / open source software (AGPL3+).

----

# <span class="flex items-center gap-2"><FolderSolid size="xl" aria-hidden="true"/>Design principles</span>

- Entry-based, business-oriented data definitions (no need for relational modeling and physical RDBMS table structure).
- Entries are extensible by meta-data and arbitrary attachments
- Entries and attachments can involve structured, unstructured and binary data
- Entry changes are tracked for auditing and review : Who, when and what
- Entries are represented using file-based Json that is optionally schema-enabled
- Operational store that can be reconstructed from the file-based data.

----

# <span class="flex items-center gap-2"><CubeSolid size="xl" aria-hidden="true"/>Drivers</span>

- Python: [pydmart](https://pypi.org/project/pydmart/)
- Flutter/Dart: [dmart](https://pub.dev/packages/dmart)
- Typescript: [@edraj/tsdmart](https://www.npmjs.com/package/@edraj/tsdmart)

</div>
<div class="min-w-0">

# <span class="flex items-center gap-2"><BriefcaseSolid size="xl" aria-hidden="true"/>Use cases</span>

One initial category of use cases targets organizations and individuals to establish their online presence: provision a website that is indexed by search engines, manage users, be able to receive/send messages/emails and allow users to interact with published content.

----

# <span class="flex items-center gap-2"><LayersSolid size="xl" aria-hidden="true"/>Technology stack</span>

## Backend

- Programming language : C# on .NET 10 compiled with Native AOT (no JIT, no runtime reflection)
- Framework : ASP.NET Core Minimal APIs with async-first design
- API validation : JSON Schema via `JsonSchema.Net` + source-generated `System.Text.Json` serialization (AOT-safe, zero-reflection)
- Live-update : WebSocket (native Kestrel)
- Operational store : PostgreSQL 16+ via Npgsql
- Viewing logs and building dashboards (optional): Grafana/Loki/Alloy — or `journalctl` directly (JSON-lines logs under systemd)
- Container : Podman (or Docker) — Alpine/musl AOT image for all-in-one, or distro-native RPMs for Fedora/RHEL
- System/User level OS service management : Systemd
- Reverse-proxy : Caddy (with automatic SSL/Let's encrypt integration)

----

## Frontend

- Single-Page-Application : Svelte with Typescript (compiled as static files, embedded into the server binary)
- CSS/UI framework : Flowbite with Tailwind and full RTL support

----

## High-quality code

- Strict static typing end-to-end + source-generated JSON for every wire-format type
- Automated tests via xUnit (unit + integration against a real PostgreSQL) and curl-scenario smoke tests (`curl.sh`)
- 0 warnings / 0 errors Release build under Roslyn analyzers; trimming + AOT enforced in CI
- Load testing with vegeta / ab / Apache JMeter

</div>
</div>
