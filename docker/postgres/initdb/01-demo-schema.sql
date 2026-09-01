-- Local/demo-only initialization. The official PostgreSQL image runs this file
-- only when the postgres-data volume is first initialized.
CREATE SCHEMA IF NOT EXISTS etl_demo;

CREATE TABLE IF NOT EXISTS etl_demo.customer_source (
    customer_id bigint PRIMARY KEY,
    full_name text NOT NULL,
    email text NOT NULL,
    country text NOT NULL
);

INSERT INTO etl_demo.customer_source (customer_id, full_name, email, country)
VALUES
    (101, ' Ada Lovelace ', 'ADA@EXAMPLE.TEST', 'TR'),
    (102, ' Grace Hopper ', 'GRACE@EXAMPLE.TEST', 'TR'),
    (103, ' Filtered Customer ', 'FILTERED@EXAMPLE.TEST', 'US')
ON CONFLICT (customer_id) DO UPDATE SET
    full_name = EXCLUDED.full_name,
    email = EXCLUDED.email,
    country = EXCLUDED.country;

CREATE TABLE IF NOT EXISTS etl_demo.mongo_customers (
    source_id text PRIMARY KEY,
    customer_name text NOT NULL,
    email text NOT NULL,
    balance numeric NOT NULL
);
