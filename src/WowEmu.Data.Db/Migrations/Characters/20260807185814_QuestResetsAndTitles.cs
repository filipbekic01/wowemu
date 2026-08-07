using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowEmu.Data.Db.Migrations.Characters
{
    /// <inheritdoc />
    public partial class QuestResetsAndTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "chosenTitle",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "knownTitles",
                table: "characters",
                type: "longtext",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "character_queststatus_daily",
                columns: table => new
                {
                    guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    quest = table.Column<uint>(type: "int unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_queststatus_daily", x => new { x.guid, x.quest });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "character_queststatus_monthly",
                columns: table => new
                {
                    guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    quest = table.Column<uint>(type: "int unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_queststatus_monthly", x => new { x.guid, x.quest });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "character_queststatus_weekly",
                columns: table => new
                {
                    guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    quest = table.Column<uint>(type: "int unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_queststatus_weekly", x => new { x.guid, x.quest });
                })
                .Annotation("MySQL:Charset", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_queststatus_daily");

            migrationBuilder.DropTable(
                name: "character_queststatus_monthly");

            migrationBuilder.DropTable(
                name: "character_queststatus_weekly");

            migrationBuilder.DropColumn(
                name: "chosenTitle",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "knownTitles",
                table: "characters");
        }
    }
}
