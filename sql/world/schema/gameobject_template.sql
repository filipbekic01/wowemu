-- gameobject_template — structure
--
-- Vendored from AzerothCore's world database (github.com/azerothcore/database-wotlk), AGPL-3.0.
-- Kept structurally close to upstream on purpose: PLAN.md §5.2 keeps `world` in upstream's shape
-- because 309 tables of community-curated content are not worth re-curating.
--
-- Applied before data/gameobject_template.sql. Regenerate both with tools/db/export-world.sh.
--
-- Needed by: gameobject spawning — display id, type, size, faction and the type-specific data block.

DROP TABLE IF EXISTS `gameobject_template`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `gameobject_template` (
  `entry` mediumint unsigned NOT NULL DEFAULT '0',
  `type` tinyint unsigned NOT NULL DEFAULT '0',
  `displayId` mediumint unsigned NOT NULL DEFAULT '0',
  `name` varchar(100) NOT NULL DEFAULT '',
  `IconName` varchar(100) NOT NULL DEFAULT '',
  `castBarCaption` varchar(100) NOT NULL DEFAULT '',
  `unk1` varchar(100) NOT NULL DEFAULT '',
  `faction` smallint unsigned NOT NULL DEFAULT '0',
  `flags` int unsigned NOT NULL DEFAULT '0',
  `size` float NOT NULL DEFAULT '1',
  `questItem1` int unsigned NOT NULL DEFAULT '0',
  `questItem2` int unsigned NOT NULL DEFAULT '0',
  `questItem3` int unsigned NOT NULL DEFAULT '0',
  `questItem4` int unsigned NOT NULL DEFAULT '0',
  `questItem5` int unsigned NOT NULL DEFAULT '0',
  `questItem6` int unsigned NOT NULL DEFAULT '0',
  `Data0` int unsigned NOT NULL DEFAULT '0',
  `Data1` int NOT NULL DEFAULT '0',
  `Data2` int unsigned NOT NULL DEFAULT '0',
  `Data3` int unsigned NOT NULL DEFAULT '0',
  `Data4` int unsigned NOT NULL DEFAULT '0',
  `Data5` int unsigned NOT NULL DEFAULT '0',
  `Data6` int NOT NULL DEFAULT '0',
  `Data7` int unsigned NOT NULL DEFAULT '0',
  `Data8` int unsigned NOT NULL DEFAULT '0',
  `Data9` int unsigned NOT NULL DEFAULT '0',
  `Data10` int unsigned NOT NULL DEFAULT '0',
  `Data11` int unsigned NOT NULL DEFAULT '0',
  `Data12` int unsigned NOT NULL DEFAULT '0',
  `Data13` int unsigned NOT NULL DEFAULT '0',
  `Data14` int unsigned NOT NULL DEFAULT '0',
  `Data15` int unsigned NOT NULL DEFAULT '0',
  `Data16` int unsigned NOT NULL DEFAULT '0',
  `Data17` int unsigned NOT NULL DEFAULT '0',
  `Data18` int unsigned NOT NULL DEFAULT '0',
  `Data19` int unsigned NOT NULL DEFAULT '0',
  `Data20` int unsigned NOT NULL DEFAULT '0',
  `Data21` int unsigned NOT NULL DEFAULT '0',
  `Data22` int unsigned NOT NULL DEFAULT '0',
  `Data23` int unsigned NOT NULL DEFAULT '0',
  `AIName` char(64) NOT NULL DEFAULT '',
  `ScriptName` varchar(64) NOT NULL DEFAULT '',
  `VerifiedBuild` smallint NOT NULL DEFAULT '1',
  PRIMARY KEY (`entry`),
  KEY `idx_name` (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Gameobject System';
/*!40101 SET character_set_client = @saved_cs_client */;
