using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowEmu.Data.Db.Migrations.Characters
{
    /// <inheritdoc />
    public partial class CharacterActionBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "character_action",
                columns: table => new
                {
                    guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    button = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    action = table.Column<uint>(type: "int unsigned", nullable: false),
                    type = table.Column<byte>(type: "tinyint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_action", x => new { x.guid, x.button });
                })
                .Annotation("MySQL:Charset", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_action");
        }
    }
}
