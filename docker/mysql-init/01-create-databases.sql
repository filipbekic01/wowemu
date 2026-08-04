-- Runs once, when the MySQL volume is first created.
--
-- docker-compose's MYSQL_DATABASE only creates one schema, and the world server needs a second.
-- Creating it here rather than letting EF do it keeps the application user out of the business of
-- creating schemas, which it has no privilege for by design.
--
-- On a volume that already exists this file is never executed. To add the schema to one:
--   docker exec -i wowemu-mysql mysql -uroot -pwowemu < docker/mysql-init/01-create-databases.sql

CREATE DATABASE IF NOT EXISTS `wowemu_characters`
    CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;

GRANT ALL PRIVILEGES ON `wowemu_characters`.* TO 'wowemu'@'%';

FLUSH PRIVILEGES;
