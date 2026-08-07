using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowEmu.Data.Db.Migrations.Characters
{
    /// <inheritdoc />
    public partial class CharacterTalents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "activeTalentGroup",
                table: "characters",
                type: "tinyint unsigned",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<uint>(
                name: "resettalents_cost",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<long>(
                name: "resettalents_time",
                table: "characters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte>(
                name: "specCount",
                table: "characters",
                type: "tinyint unsigned",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateTable(
                name: "character_glyphs",
                columns: table => new
                {
                    guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    talentGroup = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    slot = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    glyph = table.Column<uint>(type: "int unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_glyphs", x => new { x.guid, x.talentGroup, x.slot });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "character_talent",
                columns: table => new
                {
                    guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    talentGroup = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    talentId = table.Column<uint>(type: "int unsigned", nullable: false),
                    rank = table.Column<byte>(type: "tinyint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_talent", x => new { x.guid, x.talentGroup, x.talentId });
                })
                .Annotation("MySQL:Charset", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_glyphs");

            migrationBuilder.DropTable(
                name: "character_talent");

            migrationBuilder.DropColumn(
                name: "activeTalentGroup",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "resettalents_cost",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "resettalents_time",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "specCount",
                table: "characters");
        }
    }
}
