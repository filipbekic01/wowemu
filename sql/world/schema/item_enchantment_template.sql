-- item_enchantment_template — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/item_enchantment_template.sql. Regenerate both with tools/db/export-world.sh.
--
-- Needed by: random item properties — the weighted roll behind RandomProperty and RandomSuffix.

DROP TABLE IF EXISTS `item_enchantment_template`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `item_enchantment_template` (
  `entry` mediumint unsigned NOT NULL DEFAULT '0',
  `ench` mediumint unsigned NOT NULL DEFAULT '0',
  `chance` float unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`entry`,`ench`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Item Random Enchantment System';
/*!40101 SET character_set_client = @saved_cs_client */;
