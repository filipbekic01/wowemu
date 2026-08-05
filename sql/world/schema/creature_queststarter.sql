-- creature_queststarter — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/creature_queststarter.sql. Regenerate both with tools/db/export-world.sh.
--
-- Needed by: Phase 10 quests.

DROP TABLE IF EXISTS `creature_queststarter`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `creature_queststarter` (
  `id` mediumint unsigned NOT NULL DEFAULT '0' COMMENT 'Identifier',
  `quest` mediumint unsigned NOT NULL DEFAULT '0' COMMENT 'Quest Identifier',
  PRIMARY KEY (`id`,`quest`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Creature System';
/*!40101 SET character_set_client = @saved_cs_client */;
