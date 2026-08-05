-- creature_classlevelstats — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/creature_classlevelstats.sql. Regenerate both with tools/db/export-world.sh.
--
-- Needed by: creature spawning — base health, mana and armor per level and unit class, scaled by the template's mods.

DROP TABLE IF EXISTS `creature_classlevelstats`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `creature_classlevelstats` (
  `level` tinyint unsigned NOT NULL,
  `class` tinyint unsigned NOT NULL,
  `basehp0` smallint unsigned NOT NULL DEFAULT '1',
  `basehp1` smallint unsigned NOT NULL DEFAULT '1',
  `basehp2` smallint unsigned NOT NULL DEFAULT '1',
  `basemana` smallint unsigned NOT NULL DEFAULT '0',
  `basearmor` smallint unsigned NOT NULL DEFAULT '1',
  `attackpower` smallint unsigned NOT NULL DEFAULT '0',
  `rangedattackpower` smallint unsigned NOT NULL DEFAULT '0',
  `damage_base` float NOT NULL DEFAULT '0',
  `damage_exp1` float NOT NULL DEFAULT '0',
  `damage_exp2` float NOT NULL DEFAULT '0',
  `comment` text,
  PRIMARY KEY (`level`,`class`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
/*!40101 SET character_set_client = @saved_cs_client */;
