-- player_classlevelstats — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/player_classlevelstats.sql. Regenerate both with tools/db/export-world.sh.
--
-- Read by: entering the world — base health and mana per class and level. Without it a
-- character logs in with zero health and the client may refuse to finish loading.

DROP TABLE IF EXISTS `player_classlevelstats`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `player_classlevelstats` (
  `class` tinyint unsigned NOT NULL,
  `level` tinyint unsigned NOT NULL,
  `basehp` smallint unsigned NOT NULL,
  `basemana` smallint unsigned NOT NULL,
  PRIMARY KEY (`class`,`level`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 PACK_KEYS=0 COMMENT='Stores levels stats.';
/*!40101 SET character_set_client = @saved_cs_client */;
