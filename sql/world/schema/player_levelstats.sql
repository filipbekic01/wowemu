-- player_levelstats — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/player_levelstats.sql. Regenerate both with tools/db/export-world.sh.
--
-- Read by: entering the world — base strength, agility, stamina, intellect and spirit per
-- race, class and level.

DROP TABLE IF EXISTS `player_levelstats`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `player_levelstats` (
  `race` tinyint unsigned NOT NULL,
  `class` tinyint unsigned NOT NULL,
  `level` tinyint unsigned NOT NULL,
  `str` tinyint unsigned NOT NULL,
  `agi` tinyint unsigned NOT NULL,
  `sta` tinyint unsigned NOT NULL,
  `inte` tinyint unsigned NOT NULL,
  `spi` tinyint unsigned NOT NULL,
  PRIMARY KEY (`race`,`class`,`level`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 PACK_KEYS=0 COMMENT='Stores levels stats.';
/*!40101 SET character_set_client = @saved_cs_client */;
