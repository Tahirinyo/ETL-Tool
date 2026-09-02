# ETL Tool

An ASP.NET Core MVC application for importing CSV, modern Excel (`.xlsx`), PostgreSQL, and MongoDB data through reusable ETL pipelines. It detects and compares schemas, maps fields, applies ordered transformations and validation, previews the first 100 rows, and batch-upserts valid rows while reporting invalid rows separately.

The accepted release flows are CSV/XLSX/PostgreSQL to MongoDB and MongoDB to PostgreSQL. PostgreSQL and MongoDB endpoints are managed as protected saved connections in the application; each admitted run freezes the active source and destination connection revisions so a later connection edit cannot change an already queued run.

See the [technical documentation](docs/Technical_Documentation.md) for runtime details and the [demo guide](docs/Demo_Guide.md) for repeatable file and database-to-database scenarios.

## Run with Docker Compose

### Prerequisites

- Docker Desktop with Docker Compose v2.
- A running Docker daemon. Confirm it with `docker version` before starting the stack.

### Configure local secrets

Create a local environment file from the committed template:

```powershell
Copy-Item .env.example .env
```

Edit `.env` and replace every placeholder. The file is ignored by Git and must remain local.

- `MONGO_INITDB_ROOT_USERNAME` and `MONGO_INITDB_ROOT_PASSWORD` create the local MongoDB root account.
- `MONGODB_CONNECTION_STRING` is supplied to ASP.NET Core as `MongoDb__ConnectionString`; it must use the Compose hostname `mongo`, not `localhost`, and must specify `authSource=admin`.
- `POSTGRES_DB`, `POSTGRES_USER`, and `POSTGRES_PASSWORD` create the local PostgreSQL database and account.
- URI-encode reserved characters in the MongoDB username or password before placing them in the connection string. Using a long local password made only of unreserved URI characters avoids accidental URI-format errors.

No MongoDB or PostgreSQL credentials, or usable connection strings, are committed to the repository or included in the application image.

Saved MongoDB and PostgreSQL connections entered through the **Connections** screen are protected with ASP.NET Core
Data Protection before they are written to MongoDB metadata. The key ring is stored under
`App_Data/data-protection-keys`; Docker Compose retains it in the existing `app-data` named volume. Keep that volume
together with `mongo-data` and `postgres-data` across normal container recreation. If the key ring is lost while saved
connection records remain, those protected credentials cannot be decrypted and must be entered again.

### Start and verify

```powershell
docker compose up --build -d
docker compose ps
```

`docker compose ps` should show `mongo` and `postgres` as healthy and `web` as running. Open
[http://localhost:8080](http://localhost:8080), then use **Connections** to register the two included database
services once:

- **MongoDB**: give the saved connection a name such as `Compose MongoDB` and enter
  `mongodb://<URI-encoded-username>:<URI-encoded-password>@mongo:27017/?authSource=admin`, substituting the
  matching local `.env` values.
- **PostgreSQL**: give the saved connection a name such as `Compose PostgreSQL` and enter
  `Host=postgres;Port=5432;Database=<POSTGRES_DB>;Username=<POSTGRES_USER>;Password=<POSTGRES_PASSWORD>`,
  substituting the matching local `.env` values.

These are container-to-container connection strings: `mongo` and `postgres` are Docker Compose service hostnames.
Do not use `localhost` or `host.docker.internal` from the web container. The application persists the protected saved
connection records in MongoDB and pipeline workflows select those saved connections; it does not create them
automatically.

Inspect service output when diagnosing startup problems:

```powershell
docker compose logs --follow
```

PostgreSQL has no host port because the normal demo flow uses the Connections UI from the web container. MongoDB is
published only to the local host at `127.0.0.1:27018`; both databases remain reachable from the web container through
Docker Compose's default network by using the `mongo` and `postgres` service hostnames.

To inspect ETL output with MongoDB Compass, connect locally using:

```
mongodb://<MONGO_INITDB_ROOT_USERNAME>:<MONGO_INITDB_ROOT_PASSWORD>@localhost:27018/?authSource=admin
```

Substitute the local `.env` values and URI-encode reserved characters in either value. This host-only connection is for local verification; the web container must continue using its existing `mongo` hostname connection string.

### Stop, restart, or reset

Stop the containers while keeping MongoDB, PostgreSQL, and application data:

```powershell
docker compose down
```

Restart the existing local stack and its named volumes:

```powershell
docker compose up -d
```

Keep the same database credentials in `.env` when restarting existing volumes. MongoDB initializes its root account only
when `mongo-data` is first created; PostgreSQL initializes its account, database, and demo schema only when
`postgres-data` is first created. Changing these credentials or rerunning the PostgreSQL initialization requires an
intentional volume reset. Keep the existing `app-data` volume as well so saved-connection Data Protection keys remain
available.

To remove all local data and start again from empty MongoDB, PostgreSQL, and `App_Data` volumes, run:

```powershell
docker compose down --volumes
```

> Warning: `docker compose down --volumes` permanently deletes this stack's persisted MongoDB, PostgreSQL, and
> application-data contents, including retained local error reports and saved-connection Data Protection keys. Saved
> connection records cannot be decrypted after their corresponding key ring is removed.

## Configuration notes

Docker Compose maps the external `MONGODB_CONNECTION_STRING` value to `MongoDb__ConnectionString` for application
metadata, including pipeline, run, and saved-connection records. PostgreSQL and MongoDB source/destination workflows
use protected saved connections created in the **Connections** UI. Pipelines retain only saved-connection IDs and
selected database objects; run admission resolves the current protected revision and records that safe reference in the
immutable execution snapshot. Connection strings are never copied into pipeline or run data. Legacy ASP.NET Core
PostgreSQL profile configuration remains available for compatibility but is not the normal Compose demo workflow. The
container serves local HTTP on port `8080`; TLS and reverse-proxy deployment are outside this MVP packaging setup.

## Build and test

From the repository root:

```powershell
dotnet restore EtlTool.sln
dotnet build EtlTool.sln --no-restore
dotnet test EtlTool.sln --no-build
```

The integration project includes real-provider coverage through Testcontainers, so broader integration runs require a working Docker daemon.

## MVP scope and known limitations

### Supported MVP behavior

The released MVP supports reusable pipelines for CSV, modern Excel (`.xlsx`), PostgreSQL, and MongoDB sources, with MongoDB and PostgreSQL destinations. The accepted product flows are deliberately limited to the following matrix:

| Source | MongoDB destination | PostgreSQL destination |
| --- | --- | --- |
| CSV | Supported | Outside the accepted matrix |
| XLSX | Supported | Outside the accepted matrix |
| PostgreSQL | Supported | Outside the accepted matrix |
| MongoDB | Outside the accepted matrix | Supported |

For a database source, the user selects a saved provider-compatible connection and then cascades through the available database objects. PostgreSQL uses connection, database, schema, and table; MongoDB uses connection, database, and collection. PostgreSQL sources route to MongoDB destinations, MongoDB sources route to PostgreSQL destinations, and accepted CSV/XLSX flows target MongoDB. Saved connection credentials remain protected server-side, while pipelines retain only the connection ID and logical object identities. Database-source Preview resolves the active revision; run admission freezes the active source and destination revisions for that run. PostgreSQL and MongoDB sources stream incrementally. Preview reads the first 100 rows through the same mapping, transformation, validation, filtering, and deduplication path used for execution; it performs live schema comparison and requires remapping when the source schema has changed. Unsupported PostgreSQL column types and unsupported or incompatible MongoDB BSON values fail safely instead of being silently coerced.

Execution is admitted to an in-process background queue and incrementally loads valid rows in configured batches. MongoDB uses BulkWrite-style upserts; PostgreSQL uses batch upserts against the configured key column. In both supported destinations, confirmed inserts and updates are counted separately and a rerun with the same logical keys is idempotent. Empty upsert keys and later duplicates within an input are excluded. Run status/history and counters are retained; invalid rows are excluded from loading and available in a safely generated error CSV. The Compose stack includes health-gated MongoDB and PostgreSQL services for local/demo onboarding.

### Intentional MVP exclusions

The MVP does not include authentication or multi-tenancy; legacy `.xls`; database providers other than PostgreSQL and MongoDB; source/destination combinations outside the accepted matrix above; scheduled or distributed workers; AI/fuzzy schema matching; user-defined code or regex validation; full-file dry runs; or cloud/production-SLA deployment infrastructure. A configured MongoDB connection remains required for application metadata, while pipeline sources and destinations use protected saved connections. Credentials are never stored in pipeline or run data.

### Known verification limitations

- Final acceptance did not include a live-browser rehearsal of compatible pipeline reuse or schema-change/remapping. Automated MVC and application coverage covers those behaviors.
- Compose uses attached named volumes, but restart-based volume-retention was not explicitly confirmed during final acceptance.
