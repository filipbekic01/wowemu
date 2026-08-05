-- gossip_menu — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/gossip_menu.sql. Regenerate both with tools/db/export-world.sh.
--
-- Needed by: Phase 10 gossip and vendors.

DROP TABLE IF EXISTS `gossip_menu`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `gossip_menu` (
  `entry` smallint unsigned NOT NULL DEFAULT '0',
  `text_id` mediumint unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`entry`,`text_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
/*!40101 SET character_set_client = @saved_cs_client */;
