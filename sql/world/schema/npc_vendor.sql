-- npc_vendor — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/npc_vendor.sql. Regenerate both with tools/db/export-world.sh.
--
-- Needed by: Phase 10 gossip and vendors.

DROP TABLE IF EXISTS `npc_vendor`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `npc_vendor` (
  `entry` mediumint unsigned NOT NULL DEFAULT '0',
  `slot` smallint NOT NULL DEFAULT '0',
  `item` mediumint NOT NULL DEFAULT '0',
  `maxcount` tinyint unsigned NOT NULL DEFAULT '0',
  `incrtime` int unsigned NOT NULL DEFAULT '0',
  `ExtendedCost` mediumint unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`entry`,`item`,`ExtendedCost`),
  KEY `slot` (`slot`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Npc System';
/*!40101 SET character_set_client = @saved_cs_client */;
