#!/usr/bin/env bash
# Vendors a world table from AzerothCore's dumps into sql/world/.
#
# Run this when a phase needs a table we do not carry yet. It loads the upstream dump into the
# world database, then dumps it back out into sql/world/ with a provenance header — so what lands
# in the repository is what the server actually runs against, not a file we hope is equivalent.
#
# Needs database-wotlk/ checked out. Everyday use does not: sql/world/ is committed, and
# import-world.sh reads from there.
#
#   tools/db/export-world.sh creature_template "Needed by: creature spawning."
set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "usage: $0 <table> [reason]" >&2
    exit 1
fi

table="$1"
reason="${2:-Needed by: (unrecorded — please say what reads this.)}"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
upstream="$repo/database-wotlk/sql/base/$table.sql"
schema_out="$repo/sql/world/schema/$table.sql"
data_out="$repo/sql/world/data/$table.sql"

container="${WOWEMU_MYSQL_CONTAINER:-wowemu-mysql}"
password="${WOWEMU_MYSQL_ROOT_PASSWORD:-wowemu}"
database="${WOWEMU_WORLD_DATABASE:-wowemu_world}"

if [[ ! -f "$upstream" ]]; then
    echo "error: $upstream not found — is database-wotlk/ checked out?" >&2
    exit 1
fi

docker exec -i "$container" mysql -uroot -p"$password" "$database" < "$upstream" 2>/dev/null

mkdir -p "$repo/sql/world/schema" "$repo/sql/world/data"

# Structure and rows go to separate files, so re-seeding content never touches the schema.
#
# The sed below carries the table off MyISAM. ROW_FORMAT=FIXED has to go with it: it is a MyISAM
# option, and InnoDB rejects the CREATE outright with "storage engine doesn't have this option".
# DYNAMIC and COMPRESSED are left alone — InnoDB understands both.
{
    cat <<EOF
-- $table — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps \`world\` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/$table.sql. Regenerate both with tools/db/export-world.sh.
--
-- $reason

EOF

    docker exec "$container" mysqldump -uroot -p"$password" \
        --skip-comments --compact --add-drop-table --no-data \
        "$database" "$table" 2>/dev/null \
        | sed 's/ENGINE=MyISAM/ENGINE=InnoDB/; s/CHARSET=utf8mb3/CHARSET=utf8mb4/; s/ ROW_FORMAT=FIXED//'
} > "$schema_out"

{
    cat <<EOF
-- $table — data
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Rows only; the table is created by schema/$table.sql.

EOF

    docker exec "$container" mysqldump -uroot -p"$password" \
        --skip-comments --compact --no-create-info --extended-insert \
        "$database" "$table" 2>/dev/null
} > "$data_out"

rows=$(docker exec "$container" mysql -uroot -p"$password" "$database" -N -B -e \
    "SELECT COUNT(*) FROM \`$table\`" 2>/dev/null)

echo "wrote $schema_out and $data_out ($rows rows)"
echo "remember to commit it — sql/world/ is what a fresh clone imports from."
