# ETL Tool

An ASP.NET Core MVC application for importing CSV, modern Excel (`.xlsx`), PostgreSQL, and MongoDB data through reusable ETL pipelines.

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
- URI-encode reserved characters in the MongoDB username or password before placing them in the connection string. Using a long local password made only of unreserved URI characters avoids accidental URI-format errors.

No MongoDB credentials or usable connection strings are committed to the repository or included in the application image.

### Start and verify

```powershell
docker compose up --build -d
docker compose ps
```

Open [http://localhost:8080](http://localhost:8080). Then open [http://localhost:8080/Pipelines](http://localhost:8080/Pipelines): a successful response confirms that the web application can use its Compose-network connection to MongoDB.

Inspect service output when diagnosing startup problems:

```powershell
docker compose logs --follow
```

MongoDB is published only to the local host at `127.0.0.1:27018`. The application continues to reach it through Docker Compose’s default network by using the `mongo` service hostname.

To inspect ETL output with MongoDB Compass, connect locally using:

```
mongodb://<MONGO_INITDB_ROOT_USERNAME>:<MONGO_INITDB_ROOT_PASSWORD>@localhost:27018/?authSource=admin
```

Substitute the local `.env` values and URI-encode reserved characters in either value. This host-only connection is for local verification; the web container must continue using its existing `mongo` hostname connection string.

### Stop, restart, or reset

Stop the containers while keeping MongoDB and application data:

```powershell
docker compose down
```

Restart the existing local stack and its named volumes:

```powershell
docker compose up -d
```

Keep the same MongoDB credentials in `.env` when restarting an existing `mongo-data` volume. MongoDB initializes its root account only when that volume is first created; changing those credentials requires an intentional volume reset.

To remove all local data and start again from an empty MongoDB and `App_Data` volume, run:

```powershell
docker compose down --volumes
```

> Warning: `docker compose down --volumes` permanently deletes this stack’s persisted MongoDB data and application-data contents, including retained local error reports.

## Configuration notes

The application continues to use its existing ASP.NET Core configuration model. Docker Compose maps the external `MONGODB_CONNECTION_STRING` value to `MongoDb__ConnectionString`; no Docker-only configuration path has been added. PostgreSQL connections are named profiles under `PostgreSql:Profiles`; configure each profile's connection string through an environment variable, user secrets, or another secret provider (for example, `PostgreSql__Profiles__Demo__ConnectionString`). Profile names and selected database objects are stored with a pipeline; connection strings are not. The container serves local HTTP on port `8080`; TLS and reverse-proxy deployment are outside this MVP packaging setup.

## MVP scope and known limitations

### Supported MVP behavior

The released MVP supports reusable pipelines for CSV, modern Excel (`.xlsx`), PostgreSQL, and MongoDB sources, with MongoDB and PostgreSQL destinations. The accepted product flows are deliberately limited to the following matrix:

| Source | MongoDB destination | PostgreSQL destination |
| --- | --- | --- |
| CSV | Supported | Not advertised as supported |
| XLSX | Supported | Not advertised as supported |
| PostgreSQL | Supported | Not advertised as supported |
| MongoDB | Not advertised as supported | Supported |

For a database source, the user selects configured connection/profile information and a table or collection, discovers its schema, and maps it into output fields. PostgreSQL and MongoDB sources stream incrementally. Preview reads the first 100 rows through the same mapping, transformation, validation, filtering, and deduplication path used for execution; it performs live schema comparison and requires remapping when the source schema has changed. Unsupported PostgreSQL column types and unsupported or incompatible MongoDB BSON values fail safely instead of being silently coerced.

Execution is admitted to an in-process background queue and incrementally loads valid rows in configured batches. MongoDB uses BulkWrite-style upserts; PostgreSQL uses batch upserts against the configured key column. In both supported destinations, confirmed inserts and updates are counted separately and a rerun with the same logical keys is idempotent. Empty upsert keys and later duplicates within an input are excluded. Run status/history and counters are retained; invalid rows are excluded from loading and available in a safely generated error CSV. The Compose build/start, MongoDB health, and `/Pipelines` HTTP 200 checks were completed during final acceptance.

### Intentional MVP exclusions

The MVP does not include authentication or multi-tenancy; legacy `.xls`; database providers other than PostgreSQL and MongoDB; source/destination combinations outside the accepted matrix above; scheduled or distributed workers; AI/fuzzy schema matching; user-defined code or regex validation; full-file dry runs; or cloud/production-SLA deployment infrastructure. The application has one configured MongoDB connection and can use explicitly configured PostgreSQL profiles; credentials are never stored in pipeline data.

### Known verification limitations

- Four integration-test failures remain due to stale or incorrect test expectations, not demonstrated application defects: three tracked `MongoEtlRunRepositoryTests` expectations about monotonic `TotalRows`, and one untracked Days 1–5 checkpoint test with an incorrect source-lifecycle expectation.
- Final acceptance did not include a live-browser rehearsal of compatible pipeline reuse or schema-change/remapping. Automated MVC and application coverage covers those behaviors.
- Compose uses attached named volumes, but restart-based volume-retention was not explicitly confirmed during final acceptance.
