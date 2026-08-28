# ETL Tool

An ASP.NET Core MVC application for importing CSV and modern Excel (`.xlsx`) data into MongoDB through reusable ETL pipelines.

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

The application continues to use its existing ASP.NET Core configuration model. Docker Compose maps the external `MONGODB_CONNECTION_STRING` value to `MongoDb__ConnectionString`; no Docker-only configuration path has been added. The container serves local HTTP on port `8080`; TLS and reverse-proxy deployment are outside this MVP packaging setup.

## MVP scope and known limitations

### Supported MVP behavior

The released MVP supports reusable pipelines for CSV and modern Excel (`.xlsx`) sources. It infers and maps source schema, requires repair when a schema change leaves mappings or rule references unresolved, applies persisted ordered transformations and validations, and previews the first 100 source rows through the same row-processing path used for execution.

Execution is admitted to an in-process background queue and incrementally loads valid rows to MongoDB in configured batches. MongoDB loading uses bulk upserts with a selected output key, so supported reruns are idempotent. Run status/history and counters are retained; invalid rows are excluded from loading and available in a safely generated error CSV. The Compose build/start, MongoDB health, and `/Pipelines` HTTP 200 checks were completed during final acceptance.

### Intentional MVP exclusions

The MVP does not include authentication or multi-tenancy; `.xls` or non-CSV/XLSX sources; non-MongoDB destinations or multiple connection profiles; scheduled or distributed workers; AI/fuzzy schema matching; user-defined code or regex validation; full-file dry runs; or cloud/production-SLA deployment infrastructure.

### Known verification limitations

- Four integration-test failures remain due to stale or incorrect test expectations, not demonstrated application defects: three tracked `MongoEtlRunRepositoryTests` expectations about monotonic `TotalRows`, and one untracked Days 1–5 checkpoint test with an incorrect source-lifecycle expectation.
- Final acceptance did not include a live-browser rehearsal of compatible pipeline reuse or schema-change/remapping. Automated MVC and application coverage covers those behaviors.
- Compose uses attached named volumes, but restart-based volume-retention was not explicitly confirmed during final acceptance.
