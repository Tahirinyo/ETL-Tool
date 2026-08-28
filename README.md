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

MongoDB is not published to the host. The application reaches it through Docker Compose’s default network by using the `mongo` service hostname.

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

## Verification limitation

At the time this packaging was added, the local Docker CLI was installed but its daemon was unavailable. The Compose image build, startup, MongoDB health check, `/Pipelines` connectivity check, and volume-persistence check must be run once Docker Desktop is running; the commands above are the intended verification path.
