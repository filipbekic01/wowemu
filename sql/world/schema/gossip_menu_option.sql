-- gossip_menu_option — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/gossip_menu_option.sql. Regenerate both with tools/db/export-world.sh.
--
-- Needed by: Phase 10 gossip and vendors.

DROP TABLE IF EXISTS `gossip_menu_option`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `gossip_menu_option` (
  `menu_id` smallint unsigned NOT NULL DEFAULT '0',
  `id` smallint unsigned NOT NULL DEFAULT '0',
  `option_icon` mediumint unsigned NOT NULL DEFAULT '0',
  `option_text` text,
  `option_id` tinyint unsigned NOT NULL DEFAULT '0',
  `npc_option_npcflag` int unsigned NOT NULL DEFAULT '0',
  `action_menu_id` int unsigned NOT NULL DEFAULT '0',
  `action_poi_id` mediumint unsigned NOT NULL DEFAULT '0',
  `box_coded` tinyint unsigned NOT NULL DEFAULT '0',
  `box_money` int unsigned NOT NULL DEFAULT '0',
  `box_text` text,
  PRIMARY KEY (`menu_id`,`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
/*!40101 SET character_set_client = @saved_cs_client */;
