using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowEmu.Data.Db.Migrations.Characters
{
    /// <inheritdoc />
    public partial class QuestStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "character_queststatus",
                columns: table => new
                {
                    guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    quest = table.Column<uint>(type: "int unsigned", nullable: false),
                    status = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    slot = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    mobcount1 = table.Column<ushort>(type: "smallint unsigned", nullable: false),
                    mobcount2 = table.Column<ushort>(type: "smallint unsigned", nullable: false),
                    mobcount3 = table.Column<ushort>(type: "smallint unsigned", nullable: false),
                    mobcount4 = table.Column<ushort>(type: "smallint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_queststatus", x => new { x.guid, x.quest });
                })
                .Annotation("MySQL:Charset", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_queststatus");
        }
    }
}
