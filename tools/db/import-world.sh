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
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
source_dir="$repo/sql/world"

container="${WOWEMU_MYSQL_CONTAINER:-wowemu-mysql}"
password="${WOWEMU_MYSQL_ROOT_PASSWORD:-wowemu}"
database="${WOWEMU_WORLD_DATABASE:-wowemu_world}"

if [[ ! -d "$source_dir" ]]; then
    echo "error: $source_dir not found" >&2
    exit 1
fi

if ! docker exec "$container" true 2>/dev/null; then
    echo "error: container '$container' is not running. Start it with: docker compose up -d" >&2
    exit 1
fi

# The schema may not exist on a volume created before it was added to docker/mysql-init/.
docker exec "$container" mysql -uroot -p"$password" -e \
    "CREATE DATABASE IF NOT EXISTS \`$database\` CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
     GRANT ALL PRIVILEGES ON \`$database\`.* TO 'wowemu'@'%';
     FLUSH PRIVILEGES;" 2>/dev/null

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

    docker exec -i "$container" mysql -uroot -p"$password" "$database" < "$schema_file" 2>/dev/null
    docker exec -i "$container" mysql -uroot -p"$password" "$database" < "$data_file" 2>/dev/null

    rows=$(docker exec "$container" mysql -uroot -p"$password" "$database" -N -B -e \
        "SELECT COUNT(*) FROM \`$table\`" 2>/dev/null)
    echo "$rows rows"

    count=$((count + 1))
done

echo "imported $count table(s) into $database from sql/world/"
