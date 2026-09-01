# ETL Tool MVP demo guide

This guide uses fictional customer data from `test-data` and two small database-to-database scenarios. It follows the current MVC workflow. The file-based scenario remains useful for demonstrating error CSVs; the database scenarios demonstrate the accepted cross-database flows.

## Files

| File | Purpose |
| --- | --- |
| `test-data/clean.csv` | Primary source: 25 well-formed customer rows. |
| `test-data/dirty.csv` | Related source: deliberate conversion, validation, filtering, and duplicate cases. |
| `test-data/schema-changed.csv` | Remapping guard: `Email` is renamed to `EmailAddress`. |
| `test-data/clean.xlsx`, `test-data/dirty.xlsx` | Optional equivalent modern Excel fixtures. The short script below uses CSV. |

All values are fictional. The CSV files use comma delimiters and ISO dates (`yyyy-MM-dd`). Leave the pipeline's default source culture/date settings unchanged; the current source-upload page exposes the CSV delimiter, while the pipeline source options carry the culture and date-format settings.

## One-time setup

1. Copy `.env.example` to `.env`, fill in local MongoDB credentials, and start the application with `docker compose up --build -d`.
2. Open `http://localhost:8080/Pipelines`.
3. Create a pipeline named `Customer import demo`.
4. Open **Edit**, set a permitted destination such as database `etl_demo` and collection `customers`, and choose `CustomerId` as the upsert-key field after mapping is saved.

## Configure the pipeline

1. Open **Source**, choose `clean.csv`, choose **CSV** and **Comma**, then select **Upload and inspect**. Point out the detected headers and sample rows.
2. Open **Configure fields** and include all seven fields, keeping the target names unchanged:
   `CustomerId`, `FullName`, `Email`, `Age`, `Balance`, `BirthDate`, `Country`.
3. Open **Configure transformations** and add these rules. The order matters:

   1. **Conditional filter** — `Country`, operator **NotEquals**, comparison value `Turkey`
   2. **Trim** — `FullName`
   3. **To lower** — `Email`
   4. **Convert to integer** — `Age`
   5. **Convert to decimal** — `Balance`
   6. **Convert to date** — `BirthDate`
   7. **Deduplicate** — selected field `CustomerId`

   The conditional filter excludes matching rows, so keep it first to remove non-`Turkey` rows before typed conversion. The cards are draggable; dropping a card saves the persisted order.

4. Open **Configure validations** and add:

   - **Required** — `FullName`
   - **Email format** — `Email`
   - **Numeric range** — `Age`, minimum `18`, maximum `100`

5. Return to **Edit** and save the destination with `CustomerId` as the upsert key. If the form reports that mapped fields are unavailable, save the mapping first and reopen the destination form.

## Clean preview and run

1. Open **Preview**. The preview uses the first 100 source rows; this file has only 25 rows, so the complete file is represented.
2. Expected preview counters are **20 valid**, **0 invalid**, and **5 filtered**. The five filtered rows are the customers whose `Country` is USA, UK, Italy, Germany, or Spain. Point out that `Age`, `Balance`, and `BirthDate` are displayed after typed conversion and email is lowercased.
3. Select **Execute pipeline**. The request admits a queued background run and redirects to the polling progress page.
4. Wait for **Completed** and point out the progress counters. For a new `etl_demo.customers` collection, the expected full-run result is:

   - total/processed: `25 / 25`
   - valid: `20`; invalid: `0`; filtered: `5`; duplicate: `0`
   - inserted: `20`; updated: `0`

5. Open **Run history**, then **Details**, to show the persisted status, duration, counters, and source filename. The normal UI does not provide a MongoDB document browser; the run counters are the application-visible load evidence.

## Dirty source and error report

1. Return to **Source**, upload `dirty.csv` with the same CSV settings, and inspect it. The schema is intentionally unchanged, so no remapping is needed.
2. Open **Preview**. With the same saved rules, expected counters are **2 valid**, **8 invalid**, and **5 filtered**. The duplicate row is `CustomerId=10`; preview's current counter cards do not display a separate duplicate card, so it is excluded from those three displayed categories and is visible only through the full-run result.
3. Execute the dirty source and wait for the terminal status. Because the clean run already inserted keys `1` through `25`, the two valid dirty rows (`CustomerId=1` and `10`) are expected to be **updated**, not inserted:

   - total/processed: `16 / 16`
   - valid: `2`; invalid: `8`; filtered: `5`; duplicate: `1`
   - inserted: `0`; updated: `2`

   The eight invalid rows deliberately demonstrate: invalid email (`2`), integer conversion failure (`3`), decimal conversion failure (`4`), date conversion failure (`5`), missing required name (`6`), age range failures (`7`, `8`), and an empty upsert key (`12`). Rows with a non-`Turkey` country are filtered before validation. The second `CustomerId=10` is a duplicate after the first valid occurrence and is not loaded.

4. Open the run details and select **Error CSV download**. Point out source row numbers, fields/reasons, and original values. Filtered and duplicate rows do not appear in the invalid-row report.

## Schema-change guard (optional)

Use this short branch after the primary run if the remapping behavior is useful to show:

1. Upload `schema-changed.csv` from **Source** using the same CSV settings.
2. Point out the detected **missing `Email`** field, **new `EmailAddress`** field, and the unresolved `Email -> Email` mapping. The pipeline cannot preview or execute while this difference is unresolved.
3. Select **Review and confirm schema**, then in **Configure fields** map source `EmailAddress` to target `Email` and save. Keeping the target name `Email` means the existing email transformation and validation rules remain applicable.
4. Preview again to show that the remapping guard has been satisfied. Do not execute this branch unless a separate expected update count is useful for the presentation.

## Deliberately omitted from this short demo

The repository also supports `.xlsx`, semicolon/tab CSV delimiters, additional transformation and validation types, and the in-process run status lifecycle. They are not needed to explain this one customer scenario, so the guide keeps the presentation deterministic and uses the primary CSV fixtures.

## Database-to-database setup

The following two demos require the application's MongoDB connection plus a reachable PostgreSQL instance. Docker Compose in this repository provisions MongoDB only; configure PostgreSQL before starting the application, without committing credentials. For example, set a local process/user-secret value for the named profile used below:

```powershell
$env:PostgreSql__Profiles__Demo__ConnectionString = '<local PostgreSQL connection string>'
```

`Demo` is a profile name, not a secret. The connection string is a placeholder and must remain local. In the source and destination forms, the application discovers the allowed PostgreSQL databases, schemas, tables, columns, and eligible key constraints from that profile. MongoDB uses the configured application connection; only the chosen database and collection are saved with a pipeline.

Use simple top-level scalar fields in the MongoDB scenario. Nested documents, arrays, and unsupported BSON values are intentionally outside this demo because the source rejects unsupported or incompatible values safely.

## Demo A — PostgreSQL to MongoDB

Prepare a small PostgreSQL source table in the database exposed by the `Demo` profile. This fixture has no credentials and can be created with any local SQL client:

```sql
CREATE SCHEMA IF NOT EXISTS etl_demo;
CREATE TABLE IF NOT EXISTS etl_demo.customer_source (
    customer_id bigint PRIMARY KEY,
    full_name text NOT NULL,
    email text NOT NULL,
    country text NOT NULL
);
INSERT INTO etl_demo.customer_source (customer_id, full_name, email, country) VALUES
    (101, ' Ada Lovelace ', 'ADA@EXAMPLE.TEST', 'TR'),
    (102, ' Grace Hopper ', 'GRACE@EXAMPLE.TEST', 'TR'),
    (103, ' Filtered Customer ', 'FILTERED@EXAMPLE.TEST', 'US')
ON CONFLICT (customer_id) DO UPDATE SET
    full_name = EXCLUDED.full_name, email = EXCLUDED.email, country = EXCLUDED.country;
```

1. Create `PostgreSQL to MongoDB demo`. On **Source**, select **PostgreSQL**, choose profile `Demo`, then select the discovered database, `etl_demo` schema, and `customer_source` table. Select **Inspect table and configure mapping**.
2. In **Configure fields**, map `customer_id` to `CustomerId`, `full_name` to `FullName`, `email` to `Email`, and `country` to `Country`.
3. Add transformations in this order: **Trim** `FullName`, **To lower** `Email`, then **Conditional filter** `Country` with **NotEquals** `TR`. Add an **Email format** validation for `Email`.
4. On **Edit**, select **MongoDB**, choose a permitted database such as `etl_demo` and collection `postgres_customers`, then select `CustomerId` as the upsert key.
5. Open **Preview**. It reads at most 100 PostgreSQL rows and applies the same mapping/rules as execution. With the sample data, expect two valid rows and one filtered row. If the table schema has changed since inspection, Preview stops for remapping rather than reading stale mappings.
6. Select **Execute pipeline** and wait on the polling progress page for **Completed**. The first run should show two confirmed inserts, zero updates, zero invalid rows, and one filtered row. Check **Run history** for the saved status and counters.
7. Inspect `etl_demo.postgres_customers` with the configured MongoDB client. It contains two documents with `CustomerId` values `101` and `102`, trimmed names, and lower-case emails. Run the pipeline again without changing the source: the documents remain two and the run reports two updates rather than inserts.

## Demo B — MongoDB to PostgreSQL

Create a PostgreSQL destination table with a key constraint. The selected upsert key must correspond to a mapped destination column with a primary-key or unique constraint:

```sql
CREATE TABLE IF NOT EXISTS etl_demo.mongo_customers (
    source_id text PRIMARY KEY,
    customer_name text NOT NULL,
    email text NOT NULL,
    balance numeric NOT NULL
);
```

Seed a MongoDB collection through any local client connected with the application's configured MongoDB credentials; the following commands contain no connection string:

```javascript
db.getSiblingDB('etl_demo').mongo_customer_source.replaceOne(
  { _id: 'cust-201' },
  { _id: 'cust-201', fullName: ' Linus Torvalds ', email: 'LINUS@EXAMPLE.TEST', balance: 125.50 },
  { upsert: true });
db.getSiblingDB('etl_demo').mongo_customer_source.replaceOne(
  { _id: 'cust-202' },
  { _id: 'cust-202', fullName: ' Margaret Hamilton ', email: 'MARGARET@EXAMPLE.TEST', balance: 80.00 },
  { upsert: true });
```

1. Create `MongoDB to PostgreSQL demo`. On **Source**, select **MongoDB**, choose database `etl_demo` and collection `mongo_customer_source`, then select **Inspect collection and configure mapping**. The discovered schema includes the top-level scalar `_id`, `fullName`, `email`, and `balance` fields.
2. Map `_id` to `SourceId`, `fullName` to `CustomerName`, `email` to `Email`, and `balance` to `Balance`. Add **Trim** `CustomerName` and **To lower** `Email`, then add an **Email format** validation for `Email`.
3. On **Edit**, select **PostgreSQL**, choose profile `Demo`, the discovered database, schema `etl_demo`, and table `mongo_customers`. Map `SourceId -> source_id`, `CustomerName -> customer_name`, `Email -> email`, and `Balance -> balance`; select `source_id` as the PostgreSQL upsert-key column.
4. Open **Preview**. It reads no more than 100 MongoDB documents, uses the saved mapping/transform/validation path, and performs live schema comparison. A source schema change requires remapping before Preview or execution proceeds.
5. Execute the pipeline in the background and wait for **Completed**. The first sample run has two valid rows and two confirmed inserts. Query `etl_demo.mongo_customers` to see the renamed identifier, trimmed names, and lower-case emails.
6. Execute it again unchanged. The target still has two rows and the second run reports two updates, demonstrating PostgreSQL idempotent upsert behavior. Run details retain the status and confirmed counters; any invalid source rows would be excluded and listed in the error CSV.
