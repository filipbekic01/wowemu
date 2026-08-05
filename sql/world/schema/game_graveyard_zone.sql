-- game_graveyard_zone — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/game_graveyard_zone.sql. Regenerate both with tools/db/export-world.sh.
--
-- Needed by: player death — which graveyard a zone releases you to.

DROP TABLE IF EXISTS `game_graveyard_zone`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `game_graveyard_zone` (
  `id` mediumint unsigned NOT NULL DEFAULT '0',
  `ghost_zone` mediumint unsigned NOT NULL DEFAULT '0',
  `faction` smallint unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`id`,`ghost_zone`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Trigger System';
/*!40101 SET character_set_client = @saved_cs_client */;
