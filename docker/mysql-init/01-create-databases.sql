-- Runs once, when the MySQL volume is first created.
--
-- docker-compose's MYSQL_DATABASE only creates one schema, and the server needs three:
--
--   wowemu_auth        accounts, realmlist, build_info  (owned by the logon server, EF migrations)
--   wowemu_characters  characters                       (owned by the world server, EF migrations)
--   wowemu_world       imported content                 (read-only, imported by tools/db/import-world.sh)
--
-- Creating them here rather than letting EF do it keeps the application user out of the business of
-- creating schemas, which it has no privilege for by design.
--
-- On a volume that already exists this file is never executed. To apply it to one:
--   docker exec -i wowemu-mysql mysql -uroot -pwowemu < docker/mysql-init/01-create-databases.sql

CREATE DATABASE IF NOT EXISTS `wowemu_characters`
    CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;

CREATE DATABASE IF NOT EXISTS `wowemu_world`
    CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;

GRANT ALL PRIVILEGES ON `wowemu_characters`.* TO 'wowemu'@'%';
GRANT ALL PRIVILEGES ON `wowemu_world`.* TO 'wowemu'@'%';

FLUSH PRIVILEGES;
