#!/usr/bin/env bash
# Imports the world tables the server reads, from sql/world/ in this repository.
#
# The world database is *content*, not schema we own — PLAN.md §5.2 keeps it structurally close to
# upstream because 309 tables of community-curated data are not worth re-curating. So it is
# imported rather than migrated.
#
# The dumps live in sql/world/ and are committed, so this works on a fresh clone with no
# database-wotlk/ checkout. To pull in a table upstream has and we do not, use export-world.sh.
#
# Structure and rows are separate files: sql/world/schema/<table>.sql then sql/world/data/<table>.sql.
#
# Idempotent: every dump starts with DROP TABLE IF EXISTS, so re-running replaces the table.
#
#   tools/db/import-world.sh
#
# Two ways to reach MySQL, picked automatically:
#
#   docker  the wowemu-mysql container from docker-compose.yml — the development default
#   client  a local `mysql` binary over TCP — what CI uses, where MySQL is a service container
#           that `docker exec` cannot reach by name
#
# Set WOWEMU_MYSQL_MODE to force one. The client path reads WOWEMU_MYSQL_HOST / _PORT / _USER.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
source_dir="$repo/sql/world"

container="${WOWEMU_MYSQL_CONTAINER:-wowemu-mysql}"
password="${WOWEMU_MYSQL_ROOT_PASSWORD:-wowemu}"
database="${WOWEMU_WORLD_DATABASE:-wowemu_world}"
host="${WOWEMU_MYSQL_HOST:-127.0.0.1}"
port="${WOWEMU_MYSQL_PORT:-3306}"
user="${WOWEMU_MYSQL_USER:-root}"

if [[ ! -d "$source_dir" ]]; then
    echo "error: $source_dir not found" >&2
    exit 1
fi

# Pick a transport. Explicit beats inferred, so CI never silently falls back to a container it was
# not meant to use.
mode="${WOWEMU_MYSQL_MODE:-}"

if [[ -z "$mode" ]]; then
    if docker exec "$container" true 2>/dev/null; then
        mode=docker
    elif command -v mysql >/dev/null 2>&1; then
        mode=client
    else
        echo "error: no way to reach MySQL." >&2
        echo "       container '$container' is not running and no local 'mysql' client is on PATH." >&2
        echo "       Start the container with: docker compose up -d" >&2
        exit 1
    fi
fi

# One entry point for every query below, so the two transports cannot drift apart. Both read stdin,
# which is what lets the same function take an `-e` one-liner and a piped dump file.
mysql_run() {
    case "$mode" in
        docker)
            docker exec -i "$container" mysql -u"$user" -p"$password" "$@" 2>/dev/null
            ;;
        client)
            mysql --protocol=TCP -h "$host" -P "$port" -u"$user" -p"$password" "$@" 2>/dev/null
            ;;
        *)
            echo "error: unknown WOWEMU_MYSQL_MODE '$mode' (expected 'docker' or 'client')" >&2
            exit 1
            ;;
    esac
}

# The schema may not exist on a volume created before it was added to docker/mysql-init/.
mysql_run -e \
    "CREATE DATABASE IF NOT EXISTS \`$database\` CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
     GRANT ALL PRIVILEGES ON \`$database\`.* TO 'wowemu'@'%';
     FLUSH PRIVILEGES;" </dev/null

count=0

# Structure first, then rows. Split so that re-seeding content does not touch the schema, and so a
# schema change shows up as a readable diff instead of one buried in 124 KB of INSERTs.
for schema_file in "$source_dir"/schema/*.sql; do
    table="$(basename "$schema_file" .sql)"
    data_file="$source_dir/data/$table.sql"

    if [[ ! -f "$data_file" ]]; then
        echo "error: $table has a schema but no data file" >&2
        exit 1
    fi

    printf '  %-26s ' "$table"

    mysql_run "$database" < "$schema_file"
    mysql_run "$database" < "$data_file"

    rows=$(mysql_run "$database" -N -B -e "SELECT COUNT(*) FROM \`$table\`" </dev/null)
    echo "$rows rows"

    count=$((count + 1))
done

echo "imported $count table(s) into $database from sql/world/ (via $mode)"
